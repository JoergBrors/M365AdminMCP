# Entra ID Provisionierung

**Weg C (Terraform, `terraform/modules/entra-id`) ist der einzige aktiv gepflegte Weg** und über
`.github/workflows/deploy.yml` automatisiert. Die Wege A und B existieren noch im Repo, sind aber
**Legacy/nicht aktiv gepflegt** – siehe Kennzeichnung unten.

| Weg | Status | Empfehlung |
|---|---|---|
| A) `scripts/Set-EntraIdApps.ps1` (Azure CLI) | Legacy, nicht im CI | Nur falls Terraform bewusst nicht genutzt werden soll |
| B) `infra/modules/entra-id.bicep` (Microsoft Graph Bicep Extension) | Legacy, **Preview-Feature**, nicht im CI | Nur als Referenz/Ideengeber |
| C) `terraform/modules/entra-id` (hashicorp/azuread Provider) | **Aktiv, im CI verdrahtet** | **Immer verwenden** |

Alle drei legen konzeptionell dieselben Objekte an: `api-server`, `mcp-server`, die App-Role-/Delegated-Permission-Grants
zwischen beiden, die Microsoft-Graph-Application-Permissions `ServiceHealth.Read.All`,
`ServiceMessage.Read.All`, `Reports.Read.All` für die Office-365-Status-/Nachrichten-/Adoption-Tools
des MCP Servers (siehe `docs/ARCHITECTURE.md`), sowie die drei externen MCP-OAuth-Client-Apps für
**ChatGPT**, **Claude** und **Copilot Studio** (siehe `docs/DEPLOYMENT.md`, Abschnitt "MCP OAuth
Clients"). Nur Weg C (Terraform) ist aber tatsächlich aktuell und wird durch CI/CD am Leben gehalten.

**Wichtig: Immer nur EINEN Weg pro Umgebung verwenden, niemals mischen.** Alle drei Wege verwalten
dieselben Entra-Objekte inkl. Client Secrets. Läuft z. B. `Set-EntraIdApps.ps1` gegen eine bereits per
Terraform verwaltete Umgebung, erzeugt `az ad app credential reset` ein neues aktives Secret und macht
das von Terraform verwaltete Secret ungültig – der nächste `terraform plan` zeigt dann Drift.

## Vollständiger Entra-ID-Objektkatalog (Weg C, Terraform)

Diese Tabelle listet **jedes** von `terraform/modules/entra-id/main.tf` verwaltete Objekt. Für die
zugehörigen Azure-Ressourcen (Key Vault, RBAC, Managed Identities) siehe
[`docs/DEPLOYMENT.md`](DEPLOYMENT.md), Abschnitt "Terraform: Was genau in Azure bereitgestellt wird".

### App-Registrierungen

| App-Registrierung | Terraform-Ressource | Client-Typ | Zweck |
|---|---|---|---|
| `api-server-<env>` | `azuread_application.api` | Resource App | Exposed API (`api://api-server-<env>`), App Role `Tasks.ReadWrite.All` (App-only), Delegated Scope `Tasks.ReadWrite` (Nutzer-Kontext). `required_resource_access` auf Microsoft Graph: `ServiceHealth.Read.All`, `ServiceMessage.Read.All`, `Reports.Read.All` (alle als Application Permission) |
| `swagger-client-<env>` | `azuread_application.swagger` | SPA (Public Client) | Ermöglicht Login in die Swagger-UI des ApiServer per Auth-Code-Flow, Scope `Tasks.ReadWrite` auf `api-server` |
| `mcp-server-<env>` | `azuread_application.mcp` | Resource + Confidential Client | `api://mcp-server-<env>`, Delegated Scope `Mcp.Access`, Application Permission `Tasks.ReadWrite.All` auf `api-server`, Delegated Scope `Tasks.ReadWrite` auf `api-server`. `implicit_grant.id_token_issuance_enabled = true` |
| `chatgpt-mcp-client-<env>` | `azuread_application.mcp_oauth_client["chatgpt"]` | **Public Client** (PKCE, kein Secret) | Externer OAuth-Client für den ChatGPT-Connector, Delegated Scope `Mcp.Access` auf `mcp-server` |
| `claude-mcp-client-<env>` | `azuread_application.mcp_oauth_client["claude"]` | **Public Client** (PKCE, kein Secret) | Externer OAuth-Client für Claude, Delegated Scope `Mcp.Access` auf `mcp-server` |
| `copilot-mcp-client-<env>` | `azuread_application.copilot_mcp_oauth_client` | **Confidential Client** (mit Secret) | Externer OAuth-Client für Copilot Studio/Power Platform, Delegated Scope `Mcp.Access` auf `mcp-server` |

**Warum ChatGPT/Claude Public Clients sind, Copilot Studio aber ein Confidential Client:** Die Terraform-Ressource für ChatGPT/Claude nutzt die `public_client { redirect_uris }`-Plattform (kein `web`/`spa`-Block); Copilot Studio nutzt `web { redirect_uris }`. Grund (siehe Kommentare in `terraform/modules/entra-id/main.tf`, Zeilen ~195–246): Eine `web`-Plattform erzwingt bei Entra ID **immer** einen Confidential Client (Fehler `AADSTS7000218`, unabhängig vom `isFallbackPublicClient`-Flag); eine `spa`-Plattform löst bei serverseitiger Code-Einlösung ohne Origin-Header einen Cross-Origin-PKCE-Fehler aus (`AADSTS9002325`). Der einzige Weg zu einem echten Public Client ist die `publicClient`-Plattform – die aber von der Copilot-Studio-OAuth-UI nicht unterstützt wird, da diese zwingend ein Secret-Feld verlangt. Für einen **neuen** Client gilt als Faustregel: verlangt die Ziel-UI ein Secret-Feld → `web`-Plattform (Confidential Client) wie Copilot Studio; verlangt sie keines → `public_client`-Plattform wie ChatGPT/Claude.

### Berechtigungs-Verknüpfungen (App-Role-Assignments & Delegated-Permission-Grants)

| Terraform-Ressource | Von → Nach | Typ | Bedeutung |
|---|---|---|---|
| `azuread_app_role_assignment.mcp_to_api_task_readwrite` | `mcp-server` → `api-server` | App-Role-Assignment | Erlaubt `mcp-server` App-only-Zugriff auf `api-server` (`Tasks.ReadWrite.All`) |
| `azuread_app_role_assignment.api_to_graph_service_health` / `_service_message` / `_reports` | `api-server` → Microsoft Graph | App-Role-Assignment | Erlaubt `api-server` App-only-Zugriff auf Service Health / Message Center / Reports |
| `azuread_service_principal_delegated_permission_grant.mcp_delegated_to_api` | `mcp-server` → `api-server` | Delegated-Permission-Grant (Admin Consent) | Erlaubt OBO-Flow von `mcp-server` im Namen des Nutzers gegen `api-server` |
| `azuread_service_principal_delegated_permission_grant.swagger_delegated_to_api` | `swagger-client` → `api-server` | Delegated-Permission-Grant | Erlaubt Swagger-UI-Login mit Zugriff auf `api-server` |
| `azuread_service_principal_delegated_permission_grant.mcp_oauth_client_delegated_to_mcp` (`for_each` chatgpt/claude) | `<chatgpt\|claude>-mcp-client` → `mcp-server` | Delegated-Permission-Grant | Claims `["Mcp.Access", "offline_access"]`. `offline_access` wird bewusst explizit gegen `mcp-server` statt implizit gegen Graph gewährt – sonst `AADSTS65001` bei manchen Clients (beobachtet bei Claude) |
| `azuread_service_principal_delegated_permission_grant.copilot_mcp_oauth_client_delegated_to_mcp` | `copilot-mcp-client` → `mcp-server` | Delegated-Permission-Grant | Analog, Claims `["Mcp.Access", "offline_access"]` |

### Secrets

| Terraform-Ressource | App | Ablauf |
|---|---|---|
| `azuread_application_password.api` | `api-server` | `end_date_relative = "8760h"` (1 Jahr) |
| `azuread_application_password.mcp` | `mcp-server` | `end_date_relative = "8760h"` |
| `azuread_application_password.copilot_mcp_oauth_client` | `copilot-mcp-client` | `end_date_relative = "8760h"` |

Alle drei Secrets sind zeitlich befristet (1 Jahr) und müssen vor Ablauf erneuert werden – `terraform apply`
erkennt ein abgelaufenes/bald ablaufendes Secret beim nächsten Plan als Drift und erneuert es. ChatGPT/Claude
haben **kein** Secret (Public Client, PKCE-only).

## Weg A (Legacy, nicht im CI): `scripts/Set-EntraIdApps.ps1`

Nutzt die Azure CLI (`az ad app`, `az ad sp`, Microsoft Graph über `az rest`). Vorteile:
- Stabil, keine Preview-Abhängigkeit
- Idempotent: erneutes Ausführen erkennt bestehende Apps (per `displayName`-Tag) und aktualisiert statt zu duplizieren
- Legt automatisch an:
  - App Registration `api-server` mit App Role `Tasks.ReadWrite.All` und Delegated Scope `Tasks.ReadWrite`
  - App Registration `mcp-server` mit Redirect URI, Client Secret, Delegated Scope `Mcp.Access`
    (für die externen MCP-OAuth-Clients)
  - App Registrations `chatgpt-mcp-client-<env>`, `claude-mcp-client-<env>`, `copilot-mcp-client-<env>`
    als public clients (kein Secret) mit Delegated-Permission-Grant auf `Mcp.Access`. Redirect-URIs
    per `-ChatGptRedirectUris`/`-ClaudeRedirectUris`/`-CopilotRedirectUris` Parameter oder nachträglich
    via `scripts/Add-McpOauthRedirectUri.ps1` (z. B. weil ChatGPT bei jeder Connector-Neuanlage eine
    neue, zufällige Callback-URL generiert)
  - Service Principals für alle Apps
  - App-Role-Assignment: `mcp-server` → `api-server` (Application Permission)
  - OAuth2-Permission-Grant: `mcp-server` → `api-server` (Delegated Permission), inkl. Admin Consent
  - Schreibt Client Secret + IDs in Key Vault (`az keyvault secret set`)
  - Schreibt eine **Soll-Zustands-Datei** nach `infra/entra-desired-state/*.json` (inkl. `tenantId`) –
    Basis für den Config-Diff in `Invoke-BicepWhatIf.ps1` **und** für die automatische Befüllung der
    `AzureAd__*`-App-Settings beim Bicep-Deployment (siehe `docs/DEPLOYMENT.md`, Abschnitt "Weg 2: Bicep")

Aufruf:
```powershell
pwsh ./scripts/Set-EntraIdApps.ps1 -Environment dev   # z.B. dev, staging, prod
```

Benötigte Berechtigung des ausführenden Kontos: **Application Administrator** oder **Cloud Application Administrator** in Entra ID, plus Consent-Recht (oder ein separater Admin führt `az ad app permission admin-consent` aus).

### Bekannte Windows-Stolpersteine

- **`ERROR: Found multiple accounts with the same username ...` beim `az login`**: Bekannter Azure-CLI-WAM-Broker-Bug bei mehreren gecachten Sessions derselben Identität ([Azure/azure-cli#20168](https://github.com/Azure/azure-cli/issues/20168)). `scripts/Connect-Azure.ps1` erkennt diesen Fehler automatisch und weicht auf `az login --use-device-code` aus. Falls das Problem wiederholt auftritt: einmalig `az account clear` ausführen und neu anmelden.
- **`"--headers" kann syntaktisch an dieser Stelle nicht verarbeitet werden.`**: `az` ist unter Windows ein `.cmd`-Batch-Wrapper, der intern `cmd.exe`-Parsing durchläuft. Eine unquotierte URI mit runden Klammern (z. B. die Graph-Adressierung `applications(appId='...')`) bricht dabei die nachfolgende Argumentliste. `scripts/Set-EntraIdApps.ps1` adressiert Applications deshalb konsequent klammerfrei über die Object-ID (`applications/{id}` statt `applications(appId='{appId}')`) – funktioniert identisch auf macOS/Linux/CI.

## Weg B (Legacy, Preview-Feature, nicht im CI): Microsoft Graph Bicep Extension

`infra/modules/entra-id.bicep` zeigt, wie dieselben Objekte deklarativ per Bicep angelegt werden könnten (`Microsoft.Graph/applications`, `Microsoft.Graph/servicePrincipals`, `Microsoft.Graph/appRoleAssignedTo` über die Microsoft-Graph-Bicep-Extension).

**Wichtig:**
- Diese Extension ist eine **Preview-Funktion** von Bicep (`extensibility`-Feature) – Syntax/Ressourcentypen können sich ändern
- Muss explizit aktiviert werden (`infra/bicepconfig.json` → `experimentalFeaturesEnabled.extensibility: true`, plus `extensions.graphV1`-Alias auf die MCR-Registry, siehe Kopfkommentar in `entra-id.bicep`) und erfordert Bicep-CLI ≥ 0.36.1 (die alte eingebaute `extension microsoftGraph`-Direktive wurde von Microsoft im März 2025 retired – dieses Repo nutzt bereits die neue "dynamic types"-Variante)
- Das ausführende Deployment-Principal braucht Microsoft-Graph-Berechtigungen (`Application.ReadWrite.All`), nicht nur Azure-RBAC – bei Pipeline-Identitäten (Managed Identity/Service Principal) muss das separat als App-Role-Assignment auf Microsoft Graph vergeben werden
- **`what-if` deckt Microsoft-Graph-Ressourcen nur eingeschränkt/gar nicht ab** – deshalb ersetzt Weg B den Config-Diff aus `Invoke-BicepWhatIf.ps1` nicht; dieser bleibt weiterhin nötig

→ Nutze Weg B nur, wenn du bewusst mit Preview-Features arbeiten willst, sonst bleib bei Weg A oder C.

## Weg C (aktiv, einziger im CI verdrahteter Weg): `terraform/modules/entra-id`

Nutzt den offiziellen `hashicorp/azuread`-Provider – im Gegensatz zu Weg B **kein Preview-Feature**,
sondern ein produktionsreif dokumentierter Terraform-Provider. Vorteile gegenüber Weg A:

- **Ein gemeinsamer State** für Azure-Ressourcen UND Entra-ID-Objekte → `terraform plan` zeigt beide
  Diffs in einem Lauf (siehe `docs/WHATIF-GUIDE.md`, Abschnitt Terraform) – kein separates `Get-EntraIdDiff.ps1` nötig
- Drift-Erkennung "for free": läuft ein Admin manuell eine Änderung an der App-Registrierung, zeigt der
  nächste `terraform plan` das automatisch als Abweichung an
- Idempotenz und Rollback sind Kernfunktionen von Terraform, nicht selbstgebaut wie in `Set-EntraIdApps.ps1`

Enthält:
- `azuread_application` für `api-server` (App Role + Delegated Scope) und `mcp-server`
- `azuread_app_role_assignment` für: `mcp-server → api-server` (App-only) sowie `mcp-server → Microsoft Graph`
  für `ServiceHealth.Read.All`, `ServiceMessage.Read.All`, `Reports.Read.All`
- `azuread_service_principal_delegated_permission_grant` für den Delegated Scope (Admin Consent für OBO)
- `azuread_application_password` als Client Secret, landet über das Infra-Modul im Key Vault

**Berechtigung des ausführenden Principals:** Application Administrator (oder höher) in Entra ID, bzw.
für eine CI/CD-Service-Principal-Identität die granularen Microsoft-Graph-App-Rollen
`Application.ReadWrite.All` + `Directory.Read.All`. Der vollständige, einmalige Einrichtungsablauf für
eine neue CI-Identität (inkl. Azure-RBAC-Rollen, Federated Identity Credential, Graph-App-Rollen und der
Henne-Ei-Falle bei `azurerm_role_assignment.deployer_kv_admin`) steht in
[`docs/DEPLOYMENT.md`](DEPLOYMENT.md), Abschnitt "Berechtigungs-Bootstrap für CI/CD".

Aufruf: siehe [`docs/DEPLOYMENT.md`](DEPLOYMENT.md), Abschnitt "Terraform: Laufender Betrieb".

## Manuelle Kontrolle / Nachvollziehbarkeit

Nach dem Setup kannst du die erzeugten Objekte jederzeit prüfen:

```powershell
az ad app list --display-name api-server -o json | ConvertFrom-Json | Format-List
az ad app list --display-name mcp-server -o json | ConvertFrom-Json | Format-List
az ad app permission list --id <mcp-server-app-id>
```

## Rückbau

```powershell
pwsh ./scripts/Set-EntraIdApps.ps1 -Environment dev -Destroy
```

Entfernt die angelegten App-Registrierungen und Service Principals wieder (nur für Dev/Test-Umgebungen gedacht).
