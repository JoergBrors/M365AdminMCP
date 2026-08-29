# Deployment

**Terraform** (`terraform/`) ist der einzige aktiv gepflegte und über CI/CD verdrahtete Deployment-Weg.
Ein älterer, paralleler **Bicep**-Weg (`infra/`) existiert noch im Repo, ist aber Legacy (siehe
[Legacy: Bicep-Weg](#legacy-bicep-weg) am Ende dieses Dokuments) und läuft nicht in
`.github/workflows/deploy.yml`. Beide Wege erzeugen dieselbe Zielarchitektur, verwalten aber getrennten
State – niemals beide gleichzeitig gegen dieselbe Umgebung laufen lassen.

Alle Skripte liegen als PowerShell 7 (`.ps1`) vor und laufen identisch unter macOS, Windows und Linux
(`pwsh`). Fehlende CLIs (`az`, `gh`, `terraform`) installiert `Install-Prerequisites.ps1` automatisch.

## Tenant/Subscription-Kontext (einmalig, vor allem anderen)

Alle Skripte, die Azure-Ressourcen anlegen/ändern, rufen `scripts/Connect-Azure.ps1` automatisch als
ersten Schritt auf. Dieses Skript liest Tenant und Subscription aus einer lokalen `.env`-Datei und
stellt per `az login --tenant`/`az account set --subscription` sicher, dass garantiert gegen den
richtigen Tenant/die richtige Subscription gearbeitet wird (idempotent, meldet nicht erneut an, wenn
bereits korrekt eingeloggt).

```powershell
Copy-Item .env.example .env
# .env öffnen und AZURE_TENANT_ID / AZURE_SUBSCRIPTION_ID eintragen (Werte z.B. aus dem Azure-Portal,
# Übersichtsseite der Ziel-Subscription)
```

`.env` ist über `.gitignore` vom Commit ausgeschlossen (nur `.env.example` wird versioniert). In
GitHub Actions (`CI=true`) überspringt `Connect-Azure.ps1` die `.env`-Prüfung automatisch, weil dort
bereits per `azure/login@v2` (OIDC) angemeldet wird.

## Kosten (Dev-Umgebung)

Die Standardwerte für `dev` sind bewusst auf minimale Kosten getrimmt:

| Ressource | Dev-Default | Kostenstatus |
|---|---|---|
| App Service Plan | `F1` (Free-Tier) | Kostenlos. Kein "Always On" – die App schläft nach ca. 20 Min. Inaktivität ein und braucht beim nächsten Aufruf etwas länger zum Starten (Cold Start). Für Dev/Test unproblematisch. |
| Log Analytics Workspace | `PerGB2018` mit `dailyQuotaGb`/`daily_quota_gb = 1` | Hart auf 1 GB/Tag gedeckelt – bleibt bei MVP-Traffic i.d.R. im Cent-Bereich bzw. innerhalb des kostenlosen 5-GB/Monat-Kontingents (pro Azure-Billing-Account). Retention 30 Tage ist ebenfalls kostenlos. |
| Application Insights | nutzt denselben Log Analytics Workspace | unterliegt demselben Tageslimit |
| Key Vault | `standard` | Keine Grundgebühr, reine Pay-per-Operation-Abrechnung ohne Freikontingent – bei MVP-Nutzung praktisch vernachlässigbar (Cent-Bereich) |

Für `staging`/`prod` sollten `appServiceSku`/`app_service_sku` (z. B. `B1`/`S1`, wegen "Always On" und
SLA) und `logAnalyticsDailyQuotaGb`/`log_analytics_daily_quota_gb` (z. B. `-1` = kein Limit, wegen
Beobachtbarkeit bei echtem Traffic) bewusst in der jeweiligen Parameter-/tfvars-Datei angepasst werden –
siehe `infra/parameters/main.example.bicepparam.example` bzw. `terraform/terraform.example.tfvars.example`.

**Wichtig:** Ein `dailyQuotaGb`-Limit bedeutet, dass Log Analytics ab Erreichen des Tageslimits **keine
weiteren Logs mehr annimmt**, bis die UTC-Tagesgrenze erreicht ist – bei Diagnose-Bedarf mit viel Traffic
das Limit temporär erhöhen oder auf `-1` setzen.

## Terraform: Was genau in Azure bereitgestellt wird

Ein `terraform apply` gegen `terraform.<env>.tfvars` legt in **einem gemeinsamen State** sowohl
Azure-Ressourcen (`azurerm_*`) als auch Entra-ID-Objekte (`azuread_*`) an. Alle Namen verwenden das
Präfix `entramcp-<environment_name>` (kurz `name_prefix`).

### Azure-Ressourcen (`terraform/modules/infra/`)

| Ressource | Name-Schema | Zweck / wichtige Parameter |
|---|---|---|
| `azurerm_resource_group.this` (Root-Modul) | `rg-entramcp-<env>` | Container für alle Azure-Ressourcen dieser Umgebung |
| `azurerm_service_plan.this` | `<name_prefix>-plan` | Linux App Service Plan. SKU aus `var.app_service_sku` (Dev-Default: `F1` Free-Tier – kein Always-On, App schläft nach Inaktivität ein) |
| `azurerm_linux_web_app.api` | `<name_prefix>-api` | Hosting des ApiServer. `.NET 10.0`-Stack, `https_only = true`, **System-Assigned Managed Identity**, App Settings inkl. `AzureAd__ClientSecret` als Key-Vault-Reference |
| `azurerm_linux_web_app.mcp` | `<name_prefix>-mcp` | Hosting des McpServer. Analog zu oben, zusätzlich `McpAuth__ExternalBaseUrl`, `ApiServer__BaseUrl` (zeigt auf die ApiServer-Web-App) |
| `azurerm_key_vault.this` | `<name_prefix>-kv-<4-stelliges-zufalls-suffix>` (max. 24 Zeichen) | Secret-Storage. `enable_rbac_authorization = true` (kein Access-Policy-Modell), `purge_protection_enabled = true`, `soft_delete_retention_days = 7` |
| `azurerm_key_vault_secret.*` (6 Secrets + `for_each` über OAuth-Client-IDs) | `mcp-server-client-secret`, `api-server-client-secret`, `api-server-app-id`, `mcp-server-app-id`, `<chatgpt\|claude\|copilot>-mcp-client-id`, `copilot-mcp-client-secret` | Ablage der Entra-Werte im Key Vault, damit die Web-Apps sie per Key-Vault-Reference lesen können |
| `azurerm_log_analytics_workspace.this` | `<name_prefix>-law` | SKU `PerGB2018`, Retention 30 Tage, `daily_quota_gb = var.log_analytics_daily_quota_gb` (Dev-Default: `1` GB/Tag, harter Kostendeckel) |
| `azurerm_application_insights.this` | `<name_prefix>-appi` | APM für beide Web-Apps, nutzt obigen Log Analytics Workspace als Backend |
| `azurerm_role_assignment.deployer_kv_admin` | — | RBAC: Der **ausführende Deployer** (lokaler User oder CI-Service-Principal, `data.azurerm_client_config.current.object_id`) erhält **Key Vault Secrets Officer** auf den Key Vault – nötig, damit Terraform selbst die Secrets oben schreiben kann |
| `azurerm_role_assignment.api_kv_secrets_user` / `.mcp_kv_secrets_user` | — | RBAC: Die **Managed Identity** der jeweiligen Web-App erhält **Key Vault Secrets User**, damit die Key-Vault-Reference-App-Settings zur Laufzeit aufgelöst werden können |
| `azuread_app_role_assignment.mcp_managed_identity_to_api_task_readwrite` | — | Production-Pendant zum Client-Secret-Flow: MCP-Web-App-Managed-Identity → `api-server` App Role `Tasks.ReadWrite.All` |
| `azuread_app_role_assignment.api_managed_identity_to_graph_*` (3x) | — | Production-Pendant: API-Web-App-Managed-Identity → Microsoft Graph App Roles `ServiceHealth.Read.All`, `ServiceMessage.Read.All`, `Reports.Read.All` |

### Entra-ID-Objekte (`terraform/modules/entra-id/`)

Siehe [`docs/ENTRA-ID-SETUP.md`](ENTRA-ID-SETUP.md) für die vollständige Liste aller App-Registrierungen, App-Roles, Delegated Scopes und Permission-Grants.

### Terraform-Outputs (Root-Modul, `terraform/outputs.tf`)

| Output | Bedeutung |
|---|---|
| `api_app_hostname` / `mcp_app_hostname` | `*.azurewebsites.net`-Hostname der jeweiligen Web-App – wird von `deploy.yml` genutzt, um den ZIP-Deploy-Zielnamen zu ermitteln |
| `key_vault_name` | Name des Key Vault dieser Umgebung |
| `api_app_id` / `mcp_app_id` | Client-IDs der jeweiligen App-Registrierung |
| `api_app_identifier_uri` / `mcp_app_identifier_uri` | `api://api-server-<env>` bzw. `api://mcp-server-<env>` |
| `api_app_client_secret` | (sensitive) Client-Secret der `api-server`-App |
| `swagger_client_app_id` | Client-ID des Swagger-SPA-Clients |
| `mcp_oauth_client_ids` | Map `{chatgpt, claude, copilot} -> client_id` |

### Terraform-Variablen (Root-Modul, `terraform/variables.tf`)

| Variable | Default | Zweck |
|---|---|---|
| `environment_name` | *(Pflicht)* | Kurzname der Umgebung (`dev`/`staging`/`prod`), fließt in alle Ressourcennamen |
| `location` | `westeurope` | Azure-Region |
| `app_service_sku` | `F1` | App Service Plan SKU. Für `staging`/`prod` z. B. `B1`/`S1` wegen "Always On" + SLA |
| `log_analytics_daily_quota_gb` | `1` | Kostendeckel für Log-Ingestion (GB/Tag). Für `staging`/`prod` z. B. `-1` (kein Limit) |
| `chatgpt_mcp_redirect_uris` / `claude_mcp_redirect_uris` / `copilot_mcp_redirect_uris` | `[]` | Redirect-URIs der jeweiligen externen OAuth-Clients |

## Berechtigungs-Bootstrap für CI/CD (einmalig pro neuer Umgebung/neuem Service Principal)

Damit GitHub Actions Terraform gegen Azure/Entra ID ausführen kann, braucht die dort per OIDC
authentifizierte Identität (App-Registrierung + Service Principal, referenziert über die Repo-Secrets
`AZURE_CLIENT_ID`/`AZURE_TENANT_ID`/`AZURE_SUBSCRIPTION_ID`) eine Reihe von Berechtigungen, die
**Terraform selbst nicht verwalten kann** (zirkulär – der Principal, der die Rechte braucht, kann sie sich
nicht selbst geben, solange er sie noch nicht hat). Das ist ein einmaliger, manueller Vorbereitungsschritt
außerhalb von Terraform:

1. **GitHub OIDC Federated Identity Credential** auf der App-Registrierung (nicht Client-Secret-basiert):

   ```powershell
   az ad app federated-credential create --id <app-object-id> --parameters '{
     "name": "github-actions-dev-environment",
     "issuer": "https://token.actions.githubusercontent.com",
     "subject": "repo:<owner>/<repo>:environment:dev",
     "audiences": ["api://AzureADTokenExchange"]
   }'
   ```

   **Achtung Subject-Format:** Bei manchen Repos (z. B. nach einer Umbenennung) verwendet GitHub statt
   `repo:<owner>/<repo>:environment:dev` das Format `repo:<owner>@<ownerId>/<repo>@<repoId>:environment:dev`
   (mit numerischen IDs). Steht im Subject-Feld der Fehlermeldung `AADSTS700213: No matching federated
   identity record found for presented assertion subject '...'` beim Pipeline-Lauf – der dort angezeigte
   exakte Subject-String muss 1:1 übernommen werden. Owner-/Repo-ID ermitteln mit:
   `gh api repos/<owner>/<repo> --jq '{id: .id, owner_id: .owner.id}'`. Für den `terraform-plan`-Job (läuft
   bei `pull_request`, kein `environment:`-Suffix) zusätzlich eine zweite Credential mit Subject
   `repo:<owner>/<repo>:pull_request` (bzw. dem entsprechenden `owner@id/repo@id`-Format) anlegen.

2. **Azure-RBAC-Rollen** auf der Subscription:
   - **Storage Account Contributor** auf dem Terraform-State-Storage-Account (Resource Group `rg-tfstate`) – nötig für `terraform init`/State-Zugriff
   - **Contributor** auf der Ziel-Resource-Group (`rg-entramcp-<env>`) – nötig, um App Service Plan, Web Apps, Key Vault, Log Analytics anzulegen
   - **Role Based Access Control Administrator**, scope-begrenzt auf den Key Vault dieser Umgebung – nötig, damit Terraform die `azurerm_role_assignment.*`-Ressourcen (Secrets-Officer/-User) selbst verwalten kann. Ohne diese Rolle scheitert `terraform apply` mit `AuthorizationFailed` auf `Microsoft.Authorization/roleAssignments/*`.
   - **Key Vault Secrets Officer** auf dem Key Vault – wird von Terraform selbst über `azurerm_role_assignment.deployer_kv_admin` verwaltet, muss aber beim **allerersten** Lauf einmalig manuell vorab gesetzt werden (Henne-Ei-Problem: der `terraform plan`-Schritt liest bereits bestehende Secrets, bevor der Apply-Schritt die Rolle setzen könnte). Danach per `terraform import module.infra.azurerm_role_assignment.deployer_kv_admin <role-assignment-resource-id>` in den State übernehmen, damit Terraform die Ressource fortan selbst verwaltet statt sie erneut anzulegen (`RoleAssignmentExists`-Fehler) oder sie fälschlich zu ersetzen.

3. **Microsoft-Graph-Anwendungsberechtigungen** (App-Rollen auf dem Microsoft-Graph-Service-Principal), da der `azuread`-Terraform-Provider App-Registrierungen/Service-Principals/Permission-Grants verwaltet:
   - `Application.ReadWrite.All`
   - `Directory.Read.All`

   Diese Rollen per Graph-API direkt zuweisen (nicht nur im App-Manifest unter `requiredResourceAccess`
   eintragen – `az ad app permission admin-consent` aktiviert **Application**-Permissions/App-Roles
   erfahrungsgemäß nicht zuverlässig, nur delegierte Scopes):

   ```powershell
   $spObjectId = az ad sp show --id <app-id> --query id -o tsv
   $graphSpId  = az ad sp show --id 00000003-0000-0000-c000-000000000000 --query id -o tsv
   az rest --method post --url "https://graph.microsoft.com/v1.0/servicePrincipals/$spObjectId/appRoleAssignments" `
     --body "{`"principalId`":`"$spObjectId`",`"resourceId`":`"$graphSpId`",`"appRoleId`":`"1bfefb4e-e0b5-418b-a88f-73c46d2cc8e9`"}"  # Application.ReadWrite.All
   ```

   Alternativ (gröber, deckt mehr ab als nötig): dem Service Principal die **Entra-ID-Verzeichnisrolle**
   `Application Administrator` zuweisen statt granularer Graph-App-Permissions.

4. Nach jedem dieser Schritte kann `terraform apply` erneut ausgeführt werden – Rollenzuweisungen und
   OAuth-Token-Caches brauchen manchmal 1–2 Minuten, bis sie propagiert sind (`AADSTS`/`AuthorizationFailed`
   direkt nach der Zuweisung ist meist ein Propagations-Timing-Problem, kein dauerhafter Fehler).

## Terraform: Laufender Betrieb (Ablauf)

### Einmalige Vorbereitung

```powershell
# Remote State Backend anlegen (einmalig pro Subscription/Tenant)
pwsh ./scripts/Initialize-TerraformBackend.ps1 -StorageAccountName <global-eindeutiger-name>
# -> Ausgabe in terraform/providers.tf im "backend \"azurerm\""-Block eintragen und Kommentar entfernen
```

### Laufender Betrieb

```powershell
Set-Location terraform; terraform init; Set-Location ..

# Plan (Azure-Ressourcen + Entra-ID-Objekte in einem Diff, siehe docs/WHATIF-GUIDE.md)
pwsh ./scripts/Invoke-TerraformPlan.ps1 -Environment dev

# Apply (wendet exakt den zuvor erzeugten Plan an, fragt vorher nach Bestätigung,
# bricht bei erkannten Löschungen ohne -Force ab)
pwsh ./scripts/Invoke-TerraformApply.ps1 -Environment dev
```

Umgebungen werden über `terraform/terraform.<env>.tfvars` unterschieden (Beispiel: `terraform.dev.tfvars`
liegt bereits im Repo). Für weitere Umgebungen `terraform/terraform.example.tfvars.example` nach
`terraform.<env>.tfvars` kopieren und anpassen.

### MCP OAuth Clients

Der MCP Server ist als OAuth Resource Server geschützt. Für ChatGPT, Claude und Copilot Studio muss die öffentliche MCP-URL
verwendet werden, nicht localhost:

```text
https://entramcp-<env>-mcp.azurewebsites.net/mcp
```

`/mcp` ist der **einzige** MCP-Endpunkt (Streamable HTTP, stateless). Ein separater Legacy-SSE-Endpunkt
(`/sse`) wird bewusst nicht angeboten – ChatGPT, Claude und Copilot Studio sprechen alle bereits
Streamable HTTP. `ModelContextProtocol.AspNetCore` v2 laesst `EnableLegacySse` und `Stateless`
(Default: `true`) nicht gleichzeitig zu (wirft beim Start eine `InvalidOperationException`); dieses
Repo bleibt daher konsequent bei Stateless + Streamable HTTP.

Der Server veröffentlicht die MCP OAuth Protected Resource Metadata unter:

```text
https://entramcp-<env>-mcp.azurewebsites.net/.well-known/oauth-protected-resource
```

Der benötigte Scope lautet:

```text
api://mcp-server-<env>/Mcp.Access
```

#### OAuth-Proxy-Fassade vor Entra ID (`Auth/OAuthProxy.cs`)

Der MCP-Autorisierungs-Standard (RFC 8707/9728) verlangt, dass Clients einen `resource`-Parameter mit
der kanonischen MCP-Server-URL an `/authorize` und `/token` senden. **Entra ID implementiert RFC 8707
nicht** und lehnt jeden `resource`-Wert, der nicht die eigene App-ID-URI ist, mit `AADSTS9010010` ab.
Claude sendet `resource` (im Gegensatz zu ChatGPT, das RFC 8707 nicht nutzt) – ohne Fassade schlägt
der Verbindungsaufbau deshalb fehl.

Deshalb serviert der MCP Server eine eigene, dünne Authorization-Server-Fassade auf seiner eigenen
Origin:

```text
GET  /.well-known/oauth-authorization-server   -> synthetisiertes RFC-8414-Dokument (issuer/jwks_uri
                                                    von Entra übernommen, authorization_endpoint/
                                                    token_endpoint zeigen auf sich selbst)
GET  /authorize   -> entfernt "resource" aus der Query, 302-Redirect zu Entras echtem /authorize
                      (alle anderen Parameter inkl. PKCE, state, scope, redirect_uri unverändert)
POST /token       -> entfernt "resource" aus dem Form-Body, leitet serverseitig an Entras echtes
                      /token weiter, gibt Status/Body 1:1 zurück (inkl. Fehler-JSON)
```

Die Fassade terminiert PKCE **nicht** selbst – `code_challenge`/`code_verifier` laufen unverändert
Ende-zu-Ende zwischen Client und Entra durch, es wird kein eigenes Secret/State gehalten oder geloggt.
`authorization_servers` in `/.well-known/oauth-protected-resource` zeigt deshalb auf die eigene
Origin, nicht direkt auf Entra.

Wenn ChatGPT beim Erstellen des MCP Connectors eine OAuth Redirect URI/Callback URI anzeigt, muss diese
in `terraform/terraform.<env>.tfvars` ergänzt werden:

```hcl
chatgpt_mcp_redirect_uris = [
  "https://<chatgpt-callback-uri>"
]

claude_mcp_redirect_uris = [
  "https://<claude-callback-uri>"
]

copilot_mcp_redirect_uris = [
  "https://<copilot-studio-callback-uri>"
]
```

Terraform erstellt getrennte OAuth Client Apps:

| Client | Client-Typ | Terraform Output / `.env` | Key Vault Secrets |
|---|---|---|---|
| ChatGPT | Public Client (PKCE, kein Secret) | `CHATGPT_MCP_CLIENT_ID` | `chatgpt-mcp-client-id` |
| Claude | Public Client (PKCE, kein Secret) | `CLAUDE_MCP_CLIENT_ID` | `claude-mcp-client-id` |
| Copilot Studio | Confidential Client (mit Secret) | `COPILOT_MCP_CLIENT_ID` | `copilot-mcp-client-id`, `copilot-mcp-client-secret` |

ChatGPT und Claude erhalten **kein** Secret im Key Vault, da sie als Public Clients (`fallback_public_client_enabled = true`, reine PKCE-Absicherung) registriert sind. Nur Copilot Studio ist ein Confidential Client mit eigenem Client-Secret, weil die Power-Platform-OAuth-UI ("Benutzerdefinierter Connector") ein Secret-Feld zwingend verlangt – siehe `terraform/modules/entra-id/main.tf`, Ressourcen `azuread_application.mcp_oauth_client` (chatgpt/claude, `for_each`) vs. `azuread_application.copilot_mcp_oauth_client` (eigener Block mit `web`-Plattform statt `public_client`).

Alle drei Clients verwenden denselben Scope:

```text
api://mcp-server-<env>/Mcp.Access offline_access openid profile
```

Danach:

```powershell
pwsh ./scripts/Invoke-TerraformPlan.ps1 -Environment dev
pwsh ./scripts/Invoke-TerraformApply.ps1 -Environment dev
pwsh ./scripts/Export-TerraformLocalSettings.ps1 -Environment dev
pwsh ./scripts/Set-GitHubActionsSettings.ps1 -Environment dev
```

OAuth bleibt der bevorzugte Weg. Optional kann für einfache Test-Clients ein statischer Header
`X-MCP-API-Key` verwendet werden, wenn `McpAuth__ApiKey` als App Setting oder lokal als User Secret
gesetzt ist. Ohne gesetzten Key ist diese Alternative deaktiviert.

#### Warum "Dynamic Client Registration" (DCR) und "CIMD" beim Verbinden grau/deaktiviert sind

Der aktuelle MCP-Autorisierungs-Standard sieht drei Wege vor, wie ein Client (ChatGPT, Claude, ...) an
eine OAuth-Client-ID kommt: **Pre-Registration** (manuell, was dieses Repo nutzt), **CIMD** (Client ID
Metadata Document) und **DCR** (Dynamic Client Registration, RFC 7591). **Microsoft Entra ID
implementiert weder DCR noch CIMD** – es gibt keinen `registration_endpoint` und keine
CIMD-Unterstützung. Das ist kein Bug in diesem Repo oder im MCP Server, sondern eine bekannte Lücke von
Entra ID selbst. Deshalb zeigen ChatGPT & Co. bei "Benutzerdefinierter OAuth-Client" die Optionen DCR/CIMD
grau an, sobald die Autorisierungs-URL auf `login.microsoftonline.com` zeigt.

**Die einzige praktikable Lösung ist Pre-Registration** – genau das, was `terraform/modules/entra-id`
automatisiert (separate App-Registrierung pro Ziel-Client: ChatGPT/Claude/Copilot Studio). Trage die
resultierende Client-ID (und optional das Secret, falls der Client eines verlangt) manuell im jeweiligen
Connector-UI ein, wie im Screenshot des ChatGPT-Connectors ("Benutzerdefinierter OAuth-Client", Feld
"OAuth-Client-ID"). Eine Alternative wäre ein selbst betriebener DCR/CIMD-Proxy vor Entra ID – für dieses
MVP bewusst nicht umgesetzt, da er zusätzliche Angriffsfläche und Betriebsaufwand bedeutet.

## Legacy: Bicep-Weg

> **Nicht mehr aktiv gepflegt.** Läuft nicht in `.github/workflows/deploy.yml`. Für alles Neue Terraform
> (oben) verwenden. Dieser Abschnitt bleibt als Referenz stehen, falls die Bicep-Dateien im Repo als
> Ideengeber gebraucht werden (u. a. für die Microsoft-Graph-Bicep-Extension, siehe
> [`docs/ENTRA-ID-SETUP.md`](ENTRA-ID-SETUP.md), Weg B).

Parameterdateien liegen in `infra/parameters/`, eine pro Umgebung (`main.dev.bicepparam` liegt bereits
im Repo). Für weitere Umgebungen `infra/parameters/main.example.bicepparam.example` nach
`main.<env>.bicepparam` kopieren und anpassen (z. B. `main.staging.bicepparam`).

**Entra-ID-Werte werden automatisch befüllt:** `tenantId`, `apiAppId`, `apiAppIdentifierUri` und
`mcpAppId` stehen bewusst nicht in den `.bicepparam`-Dateien, sondern werden von
`Invoke-BicepWhatIf.ps1`/`Invoke-BicepDeploy.ps1` zur Laufzeit aus
`infra/entra-desired-state/<env>.json` (geschrieben von `Set-EntraIdApps.ps1`) als zusätzliche
`--parameters`-Overrides übergeben. Dadurch setzt `infra/modules/appservice.bicep` auf beiden Web
Apps automatisch `AzureAd__TenantId`/`AzureAd__ClientId`/`AzureAd__Audience` (api-server) bzw.
`AzureAd__TenantId`/`AzureAd__ClientId`/`AzureAd__ClientSecret` (mcp-server, als Key-Vault-Reference)
– analog zum Terraform-Weg, der das im selben State ohnehin automatisch macht.

### Ablauf (lokal)

```powershell
# 1. Entra ID (idempotent, siehe docs/ENTRA-ID-SETUP.md)
pwsh ./scripts/Set-EntraIdApps.ps1 -Environment dev

# 2. What-If: zeigt Azure-Ressourcen-Diff + Entra-Config-Diff, schreibt Report nach reports/
pwsh ./scripts/Invoke-BicepWhatIf.ps1 -Environment dev

# 3. Deployment (fragt interaktiv nochmal nach Bestätigung, bevor "az deployment group create" läuft)
pwsh ./scripts/Invoke-BicepDeploy.ps1 -Environment dev
```

`Invoke-BicepDeploy.ps1` ist bewusst so gebaut, dass es **niemals ohne vorherigen What-If-Lauf** deployed – es ruft `Invoke-BicepWhatIf.ps1` selbst als ersten Schritt auf und bricht ab, wenn kein Report erzeugt wurde.

## Repo-Setup (GitHub CLI)

```powershell
pwsh ./scripts/New-GitHubRepo.ps1 -Name entra-mcp-mvp -Visibility private
```

Installiert `gh` automatisch, falls nicht vorhanden, meldet bei Bedarf per `gh auth login` an, initialisiert
`git` lokal (falls noch nicht geschehen) und legt das Repository inklusive erstem Push an.

## Ablauf (CI/CD, GitHub Actions)

`.github/workflows/deploy.yml` hat drei Jobs, Trigger sind ausschließlich `pull_request`/`push` auf `main`:

1. **`build`** (immer): Checkout, .NET-10-Setup, `dotnet build EntraMcpMvp.sln`. Kein separater Unit-Test-Step im Workflow.
2. **`terraform-plan`** (nur bei `pull_request`, braucht `build`): `azure/login@v2` (OIDC), `./scripts/Invoke-TerraformPlan.ps1 -Environment dev`, postet den erzeugten Markdown-Report als PR-Kommentar (`actions/github-script@v7`).
3. **`deploy-dev`** (nur bei `push` auf `main`, `environment: dev`, braucht `build`): `azure/login@v2` (OIDC), `./scripts/Invoke-TerraformApply.ps1 -Environment dev -AutoApprove`, liest die Terraform-Outputs `api_app_hostname`/`mcp_app_hostname`, führt `dotnet publish` für beide Server aus, deployt beide via `azure/webapps-deploy@v3` (ZIP-Deploy) auf die per Terraform ermittelten App-Service-Namen.

Es gibt **nur die Umgebung `dev`** im Workflow – kein automatisiertes `staging`/`prod`-Stufenkonzept mit Required Reviewers. Für weitere Umgebungen müsste `deploy.yml` um zusätzliche Jobs/Environments erweitert werden (analog zu `deploy-dev`, mit eigenem `terraform.<env>.tfvars` und eigenem GitHub Environment inkl. Required Reviewers als Protection Rule).

`main` ist per Branch-Protection geschützt: Pull-Request-Pflicht (kein Direct-Push, auch nicht für Repo-Admins), Pflicht-Status-Checks `build` + `deploy-dev` vor jedem Merge. Ein Merge nach `main` löst automatisch `deploy-dev` aus.

Alle Jobs laufen auf `ubuntu-latest`. `shell: pwsh` wird für die Terraform-Skript-Aufrufe genutzt – GitHub-gehostete Ubuntu-Runner haben PowerShell 7 vorinstalliert.

Benötigte Secrets im Repo (Settings → Secrets and variables → Actions):
- `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` (für OIDC-Login via `azure/login`, kein langlebiges Secret für die Pipeline-Identität nötig)

Voraussetzung, damit diese OIDC-Anmeldung überhaupt funktioniert: der einmalige [Berechtigungs-Bootstrap](#berechtigungs-bootstrap-für-cicd-einmalig-pro-neuer-umgebungneuem-service-principal) weiter oben muss für die referenzierte App-Registrierung bereits durchgeführt worden sein.

## Rollback

Terraform-Deployments sind idempotent: einfach den vorherigen Commit/Tag auschecken und `terraform apply` erneut laufen lassen (bzw. den entsprechenden PR mergen). Da Azure-Ressourcen und Entra-ID-Objekte im selben State liegen, deckt ein Rollback beides gleichzeitig ab. Für reine Entra-ID-Änderungen ohne Azure-Ressourcen-Änderung genügt `terraform apply` mit dem vorherigen `terraform.<env>.tfvars`-Stand.

## Empfehlung: Deployment Stacks für Drift-Erkennung

Zusätzlich zum What-If (Momentaufnahme vor dem Deployment) empfiehlt sich für den weiteren Ausbau der Einsatz von **Azure Deployment Stacks** (`az stack group create`), da diese:
- verwaiste Ressourcen automatisch erkennen/aufräumen (`--action-on-unmanage`)
- eine dauerhafte Sicht auf "was gehört zu diesem Deployment" bieten, statt nur einen einmaligen Diff

Im MVP-Scope bewusst nicht enthalten, aber empfohlene nächste Ausbaustufe – siehe `docs/WHATIF-GUIDE.md`.
