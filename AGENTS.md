# AGENTS.md — Kompakt-Anleitung für Codex / Claude Code

Zweck dieser Datei: Ein KI-Coding-Agent soll dieses Repo **ohne** die ausführlichen `docs/*.md` lesen zu
müssen produktiv weiterentwickeln können. Nur bei Bedarf gezielt in die verlinkten Docs vertiefen.

## Was ist das hier

MCP-Server + API-Server für Microsoft-365-Admin-Daten (.NET 10, ASP.NET Core), deployt auf Azure App
Service via Terraform. `main` = geschützter Branch, nur PR-Merge löst Deploy aus.

```
src/ApiServer/   -> geschützte Web-API (Entra ID Auth), ruft Microsoft Graph auf
src/McpServer/   -> MCP-Server (Streamable HTTP, /mcp), ruft ApiServer + direkt Graph auf
terraform/       -> EINZIGER Deployment-Weg (Azure-Ressourcen + Entra-ID-Objekte im selben State)
infra/           -> LEGACY Bicep, NICHT im CI verdrahtet, nicht anfassen/nicht als Vorbild nehmen
scripts/         -> PowerShell 7 (.ps1), Connect-Azure.ps1 wird von allen anderen automatisch aufgerufen
.github/workflows/deploy.yml -> build -> terraform-plan (PR) / terraform apply + zip-deploy (push main)
```

**Immer Terraform verwenden, nie `scripts/Set-EntraIdApps.ps1`/`Invoke-Bicep*.ps1` für neue Arbeit** (Legacy, würde Terraform-State-Drift erzeugen, falls parallel genutzt).

## Branch-Regel (zwingend)

`main` ist geschützt: kein Direct-Push, Pflicht-PR, Pflicht-Checks `build` + `deploy-dev`. Immer:

```bash
git checkout main && git pull
git checkout -b feature/<name>   # oder docs/<name>, fix/<name>
# Änderungen, commit, push
gh pr create --base main
```

## Wo was ändern (häufigste Aufgaben)

| Aufgabe | Dateien |
|---|---|
| Neues MCP-Tool | Neue Klasse `src/McpServer/Tools/*.cs` mit `[McpServerToolType]`/`[McpServerTool]`, dann in `src/McpServer/Program.cs` bei `.WithTools<...>()` registrieren |
| Neuer API-Endpoint | `src/ApiServer/Controllers/*.cs` + ggf. `src/ApiServer/Services/GraphApiClient.cs` erweitern |
| Neue Graph-Permission für ApiServer | `terraform/modules/entra-id/main.tf`: `required_resource_access`-Block von `azuread_application.api` erweitern + passendes `azuread_app_role_assignment` ergänzen; **zusätzlich** Production-Pendant für die Managed Identity in `terraform/modules/infra/main.tf` (`azuread_app_role_assignment.api_managed_identity_to_graph_*`) |
| Neuer externer OAuth-Client (Public, wie ChatGPT/Claude) | `terraform/modules/entra-id/main.tf` `local.mcp_oauth_clients` + neue Variable in `variables.tf` (Root + Modul) + `terraform.dev.tfvars` |
| Neuer externer OAuth-Client (Confidential, wie Copilot Studio) | Eigener Ressourcenblock analog `azuread_application.copilot_mcp_oauth_client` (nutzt `web{}` statt `public_client{}`, weil die Ziel-UI ein Secret-Feld verlangt) |
| Neue Redirect-URI für bestehenden Client | `pwsh ./scripts/Add-McpOauthRedirectUri.ps1 -Client <chatgpt\|claude\|copilot> -RedirectUri "..."` |
| Terraform-Änderung testen | `pwsh ./scripts/Invoke-TerraformPlan.ps1 -Environment dev` (nie `terraform plan` direkt ohne das Skript – es prüft/warnt vor Löschungen) |
| Terraform-Änderung anwenden | `pwsh ./scripts/Invoke-TerraformApply.ps1 -Environment dev` (bricht bei geplanten Löschungen ohne `-Force` ab — das ist Absicht, nicht umgehen ohne Rückfrage) |

## Kritische Fakten, die man nicht neu recherchieren muss

- **Backend/State**: Azure Storage `stgeuwmcpo365dev` / RG `rg-tfstate`, Key `entra-mcp-mvp.tfstate`. `providers.tf` hat `use_oidc = true` für beide Provider (`azurerm`, `azuread`) — Pflicht, weil CI per GitHub-OIDC (nicht Client-Secret) angemeldet ist.
- **CI-Auth**: `azure/login@v2` via OIDC, Repo-Secrets `AZURE_CLIENT_ID`/`AZURE_TENANT_ID`/`AZURE_SUBSCRIPTION_ID`. Terraform selbst braucht zusätzlich `ARM_USE_OIDC=true` + `ARM_CLIENT_ID`/`ARM_TENANT_ID`/`ARM_SUBSCRIPTION_ID` als Job-Env (bereits in `deploy.yml` gesetzt).
- **CI-Service-Principal-Berechtigungen** (bereits eingerichtet, nur relevant falls eine neue Umgebung/neuer SP aufgesetzt wird): Federated Credential mit Subject `repo:<owner>@<ownerId>/<repo>@<repoId>:environment:dev` (Achtung: nicht das übliche `owner/repo`-Format — dieses Repo braucht die numerischen IDs, siehe `docs/DEPLOYMENT.md`), plus Azure-RBAC (`Storage Account Contributor` auf State-Storage, `Contributor` auf Ziel-RG, `Role Based Access Control Administrator` scope-begrenzt auf den Key Vault) plus Graph-App-Rollen (`Application.ReadWrite.All`, `Directory.Read.All`).
- **MCP-Endpoint**: nur `/mcp` (Streamable HTTP, stateless). Kein `/sse`. `ModelContextProtocol.AspNetCore` v2 lässt `EnableLegacySse` + `Stateless` nicht gleichzeitig zu.
- **Entra ID implementiert kein RFC 8707 (resource-Parameter) und kein DCR/CIMD** — deshalb existiert `src/McpServer/Auth/OAuthProxy.cs` als Fassade vor Entra, und neue externe Clients müssen immer per Pre-Registration (Terraform) angelegt werden, nie per Self-Service-Client-Registrierung.
- **ChatGPT/Claude = Public Client (PKCE, kein Secret), Copilot Studio = Confidential Client (mit Secret)** — Faustregel: verlangt die Ziel-Connector-UI ein Secret-Feld → `web{}`-Plattform, sonst `public_client{}`.
- **Free-Tier (F1) App Service Plan**: kein Always-On, App schläft nach Inaktivität ein → erster Request nach Cold-Start kann 10-30s dauern. Das ist kein Bug.
- Report-Endpunkte (92 Stück) sind **datengetrieben** in `src/McpServer/Reporting/ReportCatalog.cs`, nicht als 92 Einzel-Tools — bei neuen Graph-Report-Endpunkten dort einen Katalogeintrag ergänzen statt eine neue Tool-Methode zu schreiben.

## Bei Blockern: nicht raten

- Fehlende Azure/Entra-Berechtigung während `terraform apply` in CI → nicht versuchen, den Workflow "zu reparieren", indem Rechte umgangen werden. Fehlende Rolle exakt benennen (Scope + Rollenname) und den Nutzer fragen, ob sie vergeben werden soll (`az role assignment create` hat auf diesem Tenant einen bekannten `MissingSubscription`-CLI-Bug — Workaround ist `az rest --method put` direkt gegen die ARM-API, siehe `docs/DEPLOYMENT.md`).
- Nie `-Force`/`--force` bei Terraform-Skripten dauerhaft in `deploy.yml` einbauen, um eine geplante Löschung zu übergehen — das ist ein Sicherheitsnetz. Nur einmalig für einen nachweislich erwarteten, harmlosen Übergang (mit Rückfrage) und danach sofort wieder entfernen.

## Tiefere Doku (nur bei Bedarf laden)

- [`README.md`](README.md) — Setup, Ordnerstruktur, Branch-Workflow
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — Auth-Flows (OBO/App-only), OAuth-Proxy-Details, vollständiger Tool-Katalog
- [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md) — jede Terraform-Ressource/jeder Output/jede Variable, CI/CD-Ablauf, CI-Berechtigungs-Bootstrap im Detail, Kosten
- [`docs/ENTRA-ID-SETUP.md`](docs/ENTRA-ID-SETUP.md) — jede App-Registrierung/jedes Permission-Grant im Detail
- [`docs/WHATIF-GUIDE.md`](docs/WHATIF-GUIDE.md) — `terraform plan`-Review-Workflow
