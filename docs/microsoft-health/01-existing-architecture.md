# 01 – Bestehende Architektur (Ist-Zustand vor der Health-Erweiterung)

Stand: 2026-10-16, Branch `main` (Commit `32502a5`). Alle Pfade und Resource-Adressen wurden aus dem Repository gelesen, nicht angenommen.
Hinweis: Die im Auftrag genannte Datei `MCP-Gesamtdokumentation.md` existiert im Repository **nicht**. Als MCP-Referenz dienten der Code, `docs/ARCHITECTURE.md`, ADR-0003 und die Tool-Discovery gegen den laufenden Server (siehe [02-implementation.md](02-implementation.md)).

## 1. .NET und MCP

| Merkmal | Befund | Fundstelle |
|---|---|---|
| Projekte | `src/McpServer`, `src/ApiServer`, Tests `tests/ApiServer.Tests`; Solution `EntraMcpMvp.sln` | `EntraMcpMvp.sln` |
| Target Framework | `net10.0` | `src/*/*.csproj` |
| MCP-SDK | `ModelContextProtocol.AspNetCore` **2.2.0** | `src/McpServer/McpServer.csproj` |
| Transport | **Streamable HTTP, stateless**, einziger Endpoint `/mcp` (kein SSE) | `src/McpServer/Program.cs` (`WithHttpTransport`, `MapMcp("/mcp")`), ADR-0003 |
| Tool-Registrierung | explizit: `.WithTools<T>()` je Klasse (kein `WithToolsFromAssembly`) | `src/McpServer/Program.cs` |
| Bestehende Tool-Klassen | `TasksTool`, `M365StatusTool`, `M365MessagesTool`, `M365AdoptionTool`, `M365AdoptionCatalogTool` (11 Tools) | `src/McpServer/Tools/` |
| Aufrufkette | LLM → McpServer-Tool → `ApiServerClient` → ApiServer-Controller → `GraphApiClient` → Microsoft Graph | ADR-0002 |
| DI | ASP.NET Core Minimal Hosting, Singletons (`ApiTokenService`, `ApiServerClient`, `GraphTokenService`, `GraphApiClient`) | `Program.cs` beider Projekte |
| Authentifizierung MCP | Entra-ID-JWT (`AddMicrosoftIdentityWebApi`), Policy `McpAccess`; Alternative `X-MCP-API-Key`; OAuth-Proxy-Fassade | `src/McpServer/Program.cs`, `Auth/OAuthProxy.cs`, ADR-0004/0014 |
| Authentifizierung API | Entra-ID-JWT, Policy `TasksReadWrite` (Rolle `Tasks.ReadWrite.All` oder Scope `Tasks.ReadWrite`) für **alle** Controller | `src/ApiServer/Program.cs`, `Auth/PolicyNames.cs` |
| Graph-Client | eigener `GraphApiClient` über `IHttpClientFactory` (kein `GraphServiceClient`); Token über `GraphTokenService` | `src/ApiServer/Services/GraphApiClient.cs`, `Auth/GraphTokenService.cs` |
| Azure-Clients | **keine** vor der Erweiterung | – |
| Credentials | `ManagedIdentityCredential` (Azure) bzw. MSAL Client Secret (lokal); kein `DefaultAzureCredential` | `GraphTokenService`, `ApiTokenService` |
| Retry/Logging | `GraphApiClient`: begrenzte Retries für transiente Fehler (PR #4); `ILogger` | `GraphApiClient.cs` |
| Bestehende Health-Funktionen | `M365StatusTool` (`get_service_health_overview`, `…_detail`, `get_degraded_service_health_details`, `get_service_health_issues`) und `M365MessagesTool` (`get_message_center_messages`) – unfiltert/ungeprüft für Intune | `src/McpServer/Tools/M365StatusTool.cs`, `M365MessagesTool.cs`, `GraphApiClient.cs` |
| Tests | `tests/ApiServer.Tests` (xUnit, 22 Tests vor der Erweiterung) | `tests/ApiServer.Tests` |

**Entscheidung zur Platzierung:** Der Auftrag nennt Clients „im MCP-Server“. Dieses Repo trennt bewusst (ADR-0002): Der **McpServer besitzt keine Graph-/Azure-Rechte**, der **ApiServer** hält Identität und Berechtigungen. Die neuen Clients liegen deshalb im ApiServer (`src/ApiServer/Health/`), die MCP-Tools im McpServer. Es gibt keine zweite MCP-Instanz.

## 2. Terraform

| Merkmal | Befund |
|---|---|
| Root | `terraform/` (`main.tf`, `providers.tf`, `variables.tf`, `outputs.tf`, `terraform.dev.tfvars`) |
| Module | `terraform/modules/entra-id` (Entra-Objekte), `terraform/modules/infra` (Azure-Ressourcen) |
| Provider | `azurerm ~> 4.54` (Lock: 4.81.0), `azuread ~> 3.0` (Lock: 3.9.0), `random ~> 3.6`; `use_oidc = true` |
| Backend | `azurerm`: RG `rg-tfstate`, Storage `stgeuwmcpo365dev`, Container `tfstate`, Key `entra-mcp-mvp.tfstate` |
| Hosting | Azure App Service (Linux, Plan `entramcp-<env>-plan`, F1) mit zwei Web Apps `-api` und `-mcp`, je **System-Assigned Managed Identity** |
| Konfigurationsweitergabe | `app_settings` der Web Apps (`Abschnitt__Schlüssel`), Secrets als Key-Vault-Referenz |
| Deployment | `.github/workflows/deploy.yml`: `build` → `terraform-plan` (PR) / `deploy-dev` (Push `main`), GitHub-OIDC, Wrapper `scripts/Invoke-Terraform{Plan,Apply}.ps1` (ADR-0012) |
| Nicht vorhanden | Container Apps, AKS, Functions, Provider-Aliases, mehrere Subscriptions |

## 3. Identitäten (wichtig: App Registration ≠ Runtime-Identität)

| Identität | Objekt | Verwendung |
|---|---|---|
| `api-server-<env>` App Registration + Service Principal | `azuread_application.api`, `azuread_service_principal.api` (`entra-id/main.tf`) | **nur lokal/Debug** (Client Secret `azuread_application_password.api`) |
| **Managed Identity der API-Web-App** | `azurerm_linux_web_app.api.identity[0]` (`infra/main.tf`) | **Runtime in Azure**: holt Graph-Token (`GraphAuth__UseManagedIdentity=true`) und – neu – ARM-Token |
| Managed Identity der MCP-Web-App | `azurerm_linux_web_app.mcp.identity[0]` | holt App-only-Token für die API (`Tasks.ReadWrite.All`); **keine** Graph-Rechte |
| `mcp-server-<env>`, `chatgpt/claude/copilot-mcp-client-<env>`, `swagger-client-<env>` | `entra-id/main.tf` | OAuth/Swagger, für die Health-Erweiterung unbeteiligt |

Die Graph-Rollen müssen deshalb **an zwei Stellen** vergeben sein (App-Registrierung für lokal **und** Managed Identity für Azure) – das ist bereits so (siehe [03-permissions.md](03-permissions.md)).

## 4. Relevante Terraform-Resource-Adressen (vor der Erweiterung)

- `module.entra_id.azuread_application.api` (`required_resource_access` Microsoft Graph: `ServiceHealth.Read.All`, `ServiceMessage.Read.All`, `Reports.Read.All`)
- `module.entra_id.azuread_app_role_assignment.api_to_graph_service_health` / `api_to_graph_service_message` / `api_to_graph_reports`
- `module.infra.azuread_app_role_assignment.api_managed_identity_to_graph_service_health` / `…_service_message` / `…_reports`
- `module.infra.azurerm_linux_web_app.api` / `.mcp`
- `module.infra.azurerm_role_assignment.deployer_kv_admin`, `mcp_kv_secrets_user`, `api_kv_secrets_user` (nur Key Vault; **keine** Subscription-/Resource-Health-Rolle)

## 5. CI-Identität (Deployer)

Gemäß `docs/DEPLOYMENT.md`: Federated Credential (OIDC), `Storage Account Contributor` (State), `Contributor` auf `rg-entramcp-<env>`, `Role Based Access Control Administrator` **nur** auf dem Key Vault, Graph-Rollen `Application.ReadWrite.All` und `Directory.Read.All`.
Konsequenz für diese Erweiterung: Eine Rollenzuweisung auf **Subscription-Ebene** ist dem Deployer **nicht** erlaubt, bis ihm dort `Microsoft.Authorization/roleAssignments/write` gegeben wird (siehe [04-terraform.md](04-terraform.md)).
