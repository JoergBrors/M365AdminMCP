# Design-Entscheidungen → Code-Fundstellen

Suchhilfe für Entwickler und Prompts: *Welche Entscheidung ist wo umgesetzt, und was muss man mitändern?*
Details zu jeder Entscheidung: [`adr/`](adr/README.md). Komponentenbeschreibung: [`CODE-REFERENCE.md`](CODE-REFERENCE.md).

| ADR | Entscheidung | Umgesetzt in (Datei · Symbol) | Zwingend mitändern bei Änderung |
|---|---|---|---|
| 0001 | Nur Terraform | `terraform/**`, `scripts/Invoke-Terraform*.ps1` · Legacy: `infra/`, `Set-EntraIdApps.ps1` | `deploy.yml`, Skripte |
| 0002 | MCP-Fassade + API mit Graph-Rechten | `src/McpServer/Services/ApiServerClient.cs`; `src/ApiServer/Services/GraphApiClient.cs`; `entra-id/main.tf` `azuread_application.api` | Terraform an **zwei** Stellen (Entra-Modul + `infra`-Modul) |
| 0003 | Streamable HTTP stateless | `McpServer/Program.cs` · `WithHttpTransport`, `MapMcp("/mcp")` | OAuth-Resource-Metadaten (`resource = …/mcp`), Client-Konfiguration |
| 0004 | OAuth-Proxy-Fassade | `McpServer/Auth/OAuthProxy.cs`; `Program.cs` · `MapOAuthProtectedResourceMetadata`, `OnChallenge`, CORS `McpDiscovery` | `McpAuth:ExternalBaseUrl`, Entra-Redirect-URIs |
| 0005 | Vorregistrierte Clients | `entra-id/main.tf` · `local.mcp_oauth_clients`, `mcp_oauth_client`, `copilot_mcp_oauth_client`; `terraform.dev.tfvars`; `Add-McpOauthRedirectUri.ps1` | Variablen (Root+Modul), Outputs, Key-Vault-Secrets in `infra/main.tf` |
| 0006 | App-only Standard, OBO optional | `McpServer/Auth/ApiTokenService.cs`; `Tools/TasksTool.cs`; `ApiServer/Program.cs` Policy; `Auth/PolicyNames.cs`; Controller-Attribute | Rollen/Scopes in `entra-id/main.tf`, MI-Zuweisung in `infra/main.tf` |
| 0007 | MI in Azure / Secret lokal / KV-Referenzen | `ApiTokenService`, `GraphTokenService`; `infra/main.tf` (Key Vault, App Settings); `Export-TerraformLocalSettings.ps1`; `*.csproj` | App Settings, Rollen `Key Vault Secrets User`, `.gitignore` |
| 0008 | Report-Katalog | `McpServer/Reporting/ReportCatalog.cs`; `GraphApiClient.GetCatalogReportAsJsonAsync`; `M365ReportsController`; `M365AdoptionCatalogTool` | Beschreibung der Parameter im Controller (`catalog`) und Tool-`Description` |
| 0009 | Geteilte Quelldateien | `ApiServer/ApiServer.csproj` (`Compile Include`) | beide Projekte, Solution |
| 0010 | Beta + JSON/CSV | `GraphApiClient.GetReportAsJsonAsync`; `ApiServer/Program.cs` `GraphNoRedirect`; `CsvJsonConverter.cs` | Graph-Rechte `Reports.Read.All` |
| 0011 | Health-Fallback | `GraphApiClient.GetServiceHealthDetailAsync/…FallbackAsync/GetDegraded…`; `M365StatusController`; `M365StatusTool` | Tool-Beschreibungen |
| 0012 | CI/CD + Schutz | `.github/workflows/deploy.yml`; `Invoke-TerraformApply.ps1` (Lösch-Check, `-Force`); `providers.tf` Backend | Branch-Protection, Secrets `AZURE_*`, Federated Credential |
| 0013 | F1 Free-Tier | `variables.tf` `app_service_sku`; `infra/main.tf` `is_free_tier`, `always_on` | `terraform.dev.tfvars`, `docs/DEPLOYMENT.md` Kosten |
| 0014 | API-Key-Auth | `McpServer/Program.cs` API-Key-Middleware, Policy `McpAccess`; `McpAuthOptions.ApiKey` | App Setting `McpAuth__ApiKey` |
| 0015 | Debug ohne Auth + Inspector | `.vscode/launch.json`, `.vscode/tasks.json`; `McpServer/Program.cs` Development-Guard + Loopback-Middleware; `scripts/Start-McpInspector.ps1`, `Stop-LocalDebugServers.ps1`; `tools/mcp-inspector/` | README-Abschnitt |

## Querschnitts-Konventionen (aus dem Code abgeleitet)
| Konvention | Beispiel / Ort |
|---|---|
| Konfig-Hierarchie `Abschnitt:Schlüssel`, in Azure als `Abschnitt__Schlüssel` | `AzureAd__ClientSecret` in `infra/main.tf` |
| Platzhalter in `appsettings.json` heißen `__REPLACE_WITH_…__` | `src/*/appsettings.json` |
| Tool- und Parameter-Beschreibungen sind **Deutsch** und für das LLM geschrieben | `Tools/*.cs` `[Description]` |
| ApiServer liefert rohe Graph-JSON-Strings (`ContentResult`), keine DTOs | `Controllers/*.cs` |
| Fehler = Exceptions → HTTP 500 (kein ProblemDetails-Mapping) | `GraphApiClient` |
| Namens-/Pfad-Schema Azure: `entramcp-<env>-{api|mcp|kv|law|appi|plan}` | `terraform/main.tf` `name_prefix` |
| Entra-Apps: `<name>-<env>`; Identifier-URIs `api://<name>-<env>` | `entra-id/main.tf` |
| Terraform-Kommentare erklären „WARUM“ (Entra-Eigenheiten) – vor Änderungen lesen | `mcp_oauth_client`-Block |
| Branches: `feature/…`, `docs/…`, `fix/…`; Merge nur per PR | `AGENTS.md` |

## Typische Änderungen → betroffene Stellen
| Änderungswunsch | Stellen (in Reihenfolge) |
|---|---|
| Neuer Graph-Datenbereich | Graph-Permission (Terraform ×2) → `GraphApiClient` → Controller → Tool-Klasse → `Program.cs` `WithTools` → Doku (`ARCHITECTURE.md` Tool-Katalog) |
| Neuer Report | `ReportCatalog.cs` (ggf. neuer `ReportParamMode`) |
| Neuer LLM-Client | Terraform (public vs. confidential), Redirect-URIs, tfvars, Outputs/KV, Doku (`DEPLOYMENT.md`) |
| Nutzerbezogene Rechte | ADR-0006 Änderungs-Leitfaden |
| Neue Umgebung | ADR-0012 Änderungs-Leitfaden |
