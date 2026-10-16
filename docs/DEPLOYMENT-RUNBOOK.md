# Deployment-Runbook (exakt)

Ergänzt [`DEPLOYMENT.md`](DEPLOYMENT.md) (Ressourcen, Variablen, Outputs, CI-Bootstrap) um den **genauen Ablauf**: welcher Befehl, in welcher Reihenfolge,
was er tut, woran man Erfolg erkennt und was bei Fehlern zu tun ist. Entscheidungen dahinter: [ADR-0001](adr/0001-terraform-als-einziger-deployment-weg.md),
[ADR-0012](adr/0012-ci-cd-oidc-plan-apply-schutz.md). Alle Befehle: **PowerShell 7 (`pwsh`)**, Repo-Root = `E:\Repro\M365AdminMCP`.

## 0. Überblick: Wer deployt was, wann?

| Auslöser | Workflow-Job | Wirkung |
|---|---|---|
| Push auf beliebigen Branch (ohne PR) | – | **Nichts.** Der Workflow läuft nur für `pull_request` und `push` auf `main`. |
| Pull Request nach `main` (erstellt/aktualisiert) | `build` → `terraform-plan` | `dotnet build`, danach `Invoke-TerraformPlan.ps1 -Environment dev`; der Plan-Report wird als PR-Kommentar gepostet (max. 60 000 Zeichen). **Kein Apply, kein Code-Deploy.** |
| Merge/Push auf `main` | `build` → `deploy-dev` (Environment `dev`) | `Invoke-TerraformApply.ps1 -Environment dev -AutoApprove`, Outputs lesen, `dotnet publish` (Release) beider Dienste, ZIP-Deploy an beide Web Apps. |

Konsequenz für „Zwischencode soll nicht deployt werden“: Arbeite immer auf `feature/…`, `docs/…` oder `fix/…`. Erst der **Merge** des PR löst das Deployment aus.
`main` ist geschützt (kein Direct-Push, Pflicht-Checks `build` + `deploy-dev`).

## 1. Einmalige Voraussetzungen (pro Entwickler-Rechner)

1. Werkzeuge installieren/prüfen (installiert fehlende per winget/brew): `pwsh ./scripts/Install-Prerequisites.ps1`
   Benötigt werden: .NET SDK ≥ 10.0, Azure CLI (`az`), GitHub CLI (`gh`), Terraform (≥ 1.7). Für den Inspector zusätzlich **Node.js** (`npx`) – nicht Teil des Skripts.
2. `.env` anlegen: `Copy-Item .env.example .env`, dann `AZURE_TENANT_ID` und `AZURE_SUBSCRIPTION_ID` eintragen (`AZURE_SUBSCRIPTION_NAME` nur informativ). `.env` ist git-ignoriert.
3. Anmeldung prüfen: `pwsh ./scripts/Connect-Azure.ps1`. Erwartete Ausgabe endet mit `OK: Aktive Subscription '<Name>' (<Id>), Tenant <Id>`.
   Das Skript ist idempotent, setzt `$env:ARM_SUBSCRIPTION_ID` (nur für den aktuellen Prozess) und weicht bei „Found multiple accounts with the same username“ automatisch auf `az login --use-device-code` aus.
4. Auf dem Konto werden benötigt: Zugriff auf die Subscription, Lesezugriff auf den State-Storage `stgeuwmcpo365dev` (RG `rg-tfstate`), und Entra-Rechte, die der `azuread`-Provider braucht
   (App-Registrierungen anlegen; siehe [`DEPLOYMENT.md`](DEPLOYMENT.md#berechtigungs-bootstrap-für-cicd-einmalig-pro-neuer-umgebungneuem-service-principal)).

## 2. Einmalige Voraussetzungen (pro Umgebung / neues Tenant-Setup)

Nur nötig, wenn die Umgebung **neu** aufgesetzt wird; für `dev` ist alles vorhanden.

| Schritt | Befehl / Aktion | Ergebnis |
|---|---|---|
| State-Backend | `pwsh ./scripts/Initialize-TerraformBackend.ps1 -StorageAccountName <eindeutiger-name>` | Legt RG `rg-tfstate`, Storage Account (Standard_LRS, TLS 1.2, kein öffentlicher Blob-Zugriff) und Container `tfstate` an. Werte danach in den `backend "azurerm"`-Block in `terraform/providers.tf` eintragen (Key: `entra-mcp-mvp.tfstate`). |
| CI-Identität | Federated Credential + RBAC + Graph-Rollen laut [`DEPLOYMENT.md`](DEPLOYMENT.md) | `azure/login@v2` funktioniert per OIDC. Subject-Format dieses Repos: `repo:<owner>@<ownerId>/<repo>@<repoId>:environment:dev`; für den Plan-Job zusätzlich ein Credential für `pull_request`. |
| GitHub-Settings | `pwsh ./scripts/Set-GitHubActionsSettings.ps1 -Environment dev -AzureClientId <client-id-der-OIDC-App>` | Setzt Variablen/Secrets (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`). Braucht `gh` angemeldet, Terraform-Outputs und `.env`. |
| tfvars | `terraform/terraform.<env>.tfvars` aus `terraform.example.tfvars.example` erzeugen | `environment_name`, `location`, `app_service_sku`, `log_analytics_daily_quota_gb`, Redirect-URI-Listen |
| Branch-Schutz | GitHub: Regel für `main` mit Pflicht-PR und Pflicht-Checks `build` + `deploy-dev` | Kein Direct-Push |

## 3. Der reguläre Änderungsablauf (Schritt für Schritt)

### 3.1 Branch anlegen
```powershell
git checkout main
git pull
git checkout -b feature/<kurzer-name>     # oder docs/<name>, fix/<name>
```

### 3.2 Entwickeln und lokal prüfen
```powershell
dotnet build EntraMcpMvp.sln              # muss ohne Fehler laufen (wie Job "build")
```
Lokal testen: siehe [`DEBUGGING.md`](DEBUGGING.md). **Hinweis:** Läuft gerade eine Debug-Session, sperrt sie `bin\Debug\net10.0\*.dll`; der Build meldet dann `MSB3021/MSB3027`.
Erst `pwsh ./scripts/Stop-LocalDebugServers.ps1` ausführen oder die Session beenden – oder in einen anderen Ordner bauen: `dotnet build src/McpServer/McpServer.csproj -o $env:TEMP\mcpchk`.

### 3.3 Terraform-Änderungen lokal prüfen (nur wenn `terraform/**` geändert wurde)
```powershell
pwsh ./scripts/Invoke-TerraformPlan.ps1 -Environment dev
```
Was das Skript exakt tut (Reihenfolge): `Connect-Azure.ps1` → `terraform init -input=false -upgrade=false` → `terraform validate` →
`terraform plan -var-file=terraform.dev.tfvars -out=reports/tfplan-dev-<UTC-Zeitstempel>.bin -input=false` → Report `reports/tfplan-dev-<Zeitstempel>.md` (Markdown mit dem `terraform show`-Text)
→ Zählung der geplanten `delete`-Aktionen (Warnung, wenn > 0) → Pfad des Plans in `reports/.latest-plan-dev`.
**Immer den `.md`-Report lesen**; Warnung „Plan enthaelt N geplante Loeschung(en)“ ernst nehmen. Nie `terraform plan` direkt aufrufen. Anleitung zur Bewertung: [`WHATIF-GUIDE.md`](WHATIF-GUIDE.md).

Lokales **Apply** (nur wenn ausdrücklich gewollt, z. B. neue Redirect-URI sofort benötigt):
```powershell
pwsh ./scripts/Invoke-TerraformApply.ps1 -Environment dev
```
Ablauf: Schritt 1/3 Plan (siehe oben) → bei Löschungen **Abbruch** ohne `-Force` → Schritt 2/3 Rückfrage `[y/N]` (entfällt mit `-AutoApprove`) → Schritt 3/3 `terraform apply -input=false <plan.bin>` (wendet **exakt** den erzeugten Plan an) → `terraform output` →
`Export-TerraformLocalSettings.ps1` (aktualisiert `.env` und beide `appsettings.Development.json`, liest die Client-Secrets aus dem Key Vault).
`-Force` nie dauerhaft in Workflows; nur einmalig, mit Freigabe, danach entfernen.

### 3.4 Commit, Push, PR
```powershell
git add <dateien>
git commit -m "<Beschreibung>"
git push -u origin feature/<kurzer-name>
gh pr create --base main --title "<Titel>" --body "<Beschreibung>"
```
Der Push allein deployt nichts. Der PR startet `build` und `terraform-plan`.

### 3.5 PR prüfen
1. Check `build` grün.
2. Check `terraform-plan` grün; den **Plan-Kommentar** im PR lesen (neue/geänderte/gelöschte Ressourcen). Löschungen von `azuread_application*`, `azurerm_key_vault*` oder Rollen-Zuweisungen besonders prüfen.
3. Der Kommentar enthält den **neuesten** Report aus `reports/`; er wird bei jedem neuen Push erneut erzeugt.

### 3.6 Merge → Deployment (automatisch)
Mit dem Merge startet `deploy-dev`:

| # | Schritt | Detail |
|---|---|---|
| 1 | Checkout, .NET 10, Terraform 1.7.5 | `terraform_wrapper: false` |
| 2 | `azure/login@v2` | OIDC mit `AZURE_CLIENT_ID/TENANT_ID/SUBSCRIPTION_ID`; zusätzlich Job-Env `ARM_USE_OIDC=true`, `ARM_*` |
| 3 | `Invoke-TerraformApply.ps1 -Environment dev -AutoApprove` | **Neuer** Plan zum Merge-Zeitpunkt (nicht der PR-Plan), Abbruch bei Löschungen, dann Apply. Bei Fehler bricht der Job ab, **kein** Code-Deploy. |
| 4 | Outputs lesen | `terraform -chdir=terraform output -json`; `API_APP_NAME`/`MCP_APP_NAME` = Hostname ohne `.azurewebsites.net`; Abbruch, wenn der Name nicht auf `-api` bzw. `-mcp` endet |
| 5 | `dotnet publish src/ApiServer/ApiServer.csproj -c Release -o publish/api` | `appsettings.Development.json` wird nie mitpubliziert |
| 6 | `dotnet publish src/McpServer/McpServer.csproj -c Release -o publish/mcp` | dito |
| 7 | `azure/webapps-deploy@v3` für API, dann MCP | ZIP-Deploy des jeweiligen Publish-Ordners |
| 8 | Ausgabe der URLs | `ApiServer: https://entramcp-dev-api.azurewebsites.net`, `McpServer: https://entramcp-dev-mcp.azurewebsites.net` |

Laufzeit-Konfiguration kommt ausschließlich aus den von Terraform gesetzten App Settings (`ASPNETCORE_ENVIRONMENT=Production`, `McpAuth__RequireAuthentication=true`, Key-Vault-Referenzen, `UseManagedIdentity=true`).

### 3.7 Nach dem Deployment prüfen (Smoke-Tests)
Free-Tier: erster Request nach Inaktivität kann 10–30 s dauern → ggf. wiederholen (ADR-0013).
```powershell
$mcp = "https://entramcp-dev-mcp.azurewebsites.net"
$api = "https://entramcp-dev-api.azurewebsites.net"

Invoke-RestMethod "$mcp/healthz"       # -> status = healthy
Invoke-RestMethod "$api/healthz"       # -> status = healthy

# OAuth-Discovery (anonym)
Invoke-RestMethod "$mcp/.well-known/oauth-protected-resource"
#   resource              = https://entramcp-dev-mcp.azurewebsites.net/mcp
#   authorization_servers = {https://entramcp-dev-mcp.azurewebsites.net}
#   scopes_supported      = {api://mcp-server-dev/Mcp.Access}
Invoke-RestMethod "$mcp/.well-known/oauth-authorization-server"
#   issuer / authorization_endpoint / token_endpoint zeigen auf $mcp

# /mcp ohne Token muss 401 liefern (Auth ist aktiv)
try { Invoke-WebRequest "$mcp/mcp" -Method Post -Body '{}' -ContentType 'application/json' } catch { $_.Exception.Response.StatusCode; $_.Exception.Response.Headers.WwwAuthenticate }
#   401, Header: Bearer resource_metadata="…/.well-known/oauth-protected-resource", scope="api://mcp-server-dev/Mcp.Access"
```
Funktionstest mit einem echten Client (Reihenfolge): MCP Inspector gegen die Azure-URL ([`DEBUGGING.md`](DEBUGGING.md), Abschnitt C) oder Claude/ChatGPT/Copilot Studio: `ListAdoptionReports`, `GetServiceHealthOverview`.

## 4. Typische Änderungsfälle – exakte Befehlsfolge

| Fall | Schritte |
|---|---|
| **Nur Code** (`src/**`) | 3.1 → 3.2 → 3.4 → PR → Merge. Terraform-Plan im PR zeigt „No changes“. |
| **Neue Redirect-URI** (z. B. neue ChatGPT-Connector-URL) | `pwsh ./scripts/Add-McpOauthRedirectUri.ps1 -Client <chatgpt\|claude\|copilot> -RedirectUri "https://…" [-Environment dev]` – ergänzt die tfvars (nur `https://`) und führt **sofort** `Invoke-TerraformApply.ps1 -AutoApprove` aus (das Skript wendet also direkt an). Danach die geänderte `terraform.dev.tfvars` per Feature-Branch/PR committen, sonst fällt der nächste CI-Apply auf den alten Stand zurück. |
| **Neue Graph-Permission** | In `terraform/modules/entra-id/main.tf` **und** `terraform/modules/infra/main.tf` ergänzen (ADR-0002) → 3.3 → PR. Beim ersten Apply braucht die CI-Identität die nötigen Entra-Rechte; fehlt eine Rolle: Rollenname und Scope benennen und Rückfrage stellen. |
| **Neues Secret** | `azurerm_key_vault_secret` in `infra/main.tf`, Key-Vault-Referenz in den App Settings, lokale Ausgabe in `Export-TerraformLocalSettings.ps1` |
| **Neue Umgebung** | tfvars, State-Key, Federated Credential, Workflow-Job; `Set-GitHubActionsSettings.ps1 -Environment <env>` |
| **Copilot-Secret läuft ab** | Nächster `Invoke-TerraformPlan.ps1` erkennt die Drift (8760 h) und plant Erneuerung; nach Apply `Export-TerraformLocalSettings.ps1` und Secret im Copilot-Connector aktualisieren |

## 5. Rollback und Notfall
- **Code-Fehler nach Deploy:** Revert-PR (`git revert <merge-commit>` auf neuem Branch → PR → Merge). Der Merge deployt den vorherigen Stand erneut.
- **Terraform-Fehler im Apply (CI):** Job lesen. `AuthorizationFailed` kurz nach einer Rollenvergabe ist oft Propagation (1–2 min), dann Job erneut starten. Bei fehlender Rolle nicht umgehen (AGENTS.md): Rolle und Scope benennen, Rückfrage.
- **Geplante Löschung blockiert den Apply:** Das ist gewollt. Ursache klären; nur bei nachweislich harmlosem Übergang einmalig `-Force` (mit Freigabe) – danach sofort wieder entfernen.
- **State-Probleme:** nie manuell die State-Datei editieren. `terraform import`/`state` nur nach Rücksprache (Beispiel im Bootstrap: `azurerm_role_assignment.deployer_kv_admin`).
- **App schläft/antwortet nicht:** Free-Tier-Verhalten, `GET /healthz` wiederholen. Bei echten Fehlern Logs ansehen ([`DEBUGGING.md`](DEBUGGING.md), Abschnitt F).

## 6. Fehlerbilder beim Deployment

| Meldung / Symptom | Ursache | Maßnahme |
|---|---|---|
| `AADSTS700213: No matching federated identity record …` | Federated-Credential-Subject passt nicht (Repo-/Owner-ID-Format) | Subject aus der Fehlermeldung 1:1 übernehmen; IDs: `gh api repos/<owner>/<repo> --jq '{id: .id, owner_id: .owner.id}'` |
| `AuthorizationFailed … Microsoft.Authorization/roleAssignments/*` | CI-Identität ohne *Role Based Access Control Administrator* auf dem Key Vault | Rolle gemäß `DEPLOYMENT.md` vergeben; `az role assignment create` hat auf diesem Tenant den `MissingSubscription`-Bug → `az rest --method put` gegen die ARM-API |
| `RoleAssignmentExists` | `deployer_kv_admin` nicht im State | `terraform import module.infra.azurerm_role_assignment.deployer_kv_admin <id>` |
| `Plan enthaelt N Loeschung(en). Abbruch.` | Schutz in `Invoke-TerraformApply.ps1` | siehe Abschnitt 5 |
| `Ungueltiger API/MCP App Name aus Terraform Output` | Output-Name entspricht nicht `*-api`/`*-mcp` | Outputs und Namensschema in `infra/main.tf` prüfen |
| `Keine .env gefunden` / `AZURE_TENANT_ID fehlt` | lokale `.env` fehlt | Abschnitt 1, Punkt 2 (in CI nicht relevant, `CI=true`) |
| `Terraform-Output '<name>' fehlt` (Export-Skript) | Apply noch nie erfolgreich | zuerst `Invoke-TerraformApply.ps1` |
| Deploy grün, App liefert 500 beim Start | App Setting/Key-Vault-Referenz fehlt oder MI hat keine Rolle | Portal → Web App → Konfiguration: Referenzstatus „Resolved“; `Key Vault Secrets User` für die MI prüfen |
| Start schlägt fehl mit `McpAuth:RequireAuthentication=false ist nur in der Development-Umgebung erlaubt.` | Wert fälschlich gesetzt | App Setting `McpAuth__RequireAuthentication` auf `true` (Terraform setzt es so) |
