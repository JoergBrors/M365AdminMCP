# Plattform erweitern

Dieses Dokument beschreibt, wie man mit den **vorhandenen** Bausteinen Funktionen ergänzt. Es gibt bewusst kein Plugin-Framework; die Erweiterungsschnittstellen sind die in `AGENTS.md` genannten Muster.

## A. Neues Tool im bestehenden MCP-Server
1. **Daten holen (API):** Controller in `src/ApiServer/Controllers/` + Methode in `src/ApiServer/Services/GraphApiClient.cs`. Eingaben mit festen Formaten/Whitelists validieren (Muster: `ReportParameterValidator`, `HealthInputValidator`). Nur lesende Aufrufe.
2. **Tool (MCP):** Klasse in `src/McpServer/Tools/` mit `[McpServerToolType]`/`[McpServerTool]`, die über `ApiServerClient.GetAsync(path)` den ApiServer aufruft (App-only, ADR-0006). In `src/McpServer/Program.cs` mit `.WithTools<…>()` registrieren.
3. **Reports:** Neuer Graph-Report = ein Eintrag in `src/McpServer/Reporting/ReportCatalog.cs` (ADR-0008), kein neues Tool.
4. **Graph-Berechtigung:** `terraform/modules/entra-id/main.tf` (`required_resource_access` + `azuread_app_role_assignment`) **und** das Gegenstück für die Managed Identity in `terraform/modules/infra/main.tf`. Jede neue Berechtigung braucht Freigabe und einen Eintrag in [security/SECURITY-DECISIONS.md](security/SECURITY-DECISIONS.md).
5. **Tests:** Unit-Tests in `tests/ApiServer.Tests/` (Stub-`HttpMessageHandler`, wie `GraphApiClientReportTests`); live: [`src/OAuthGateway/tools/gateway_e2e.py`](../src/OAuthGateway/tools/gateway_e2e.py) oder MCP Inspector ([ADR-0015](adr/0015-lokales-debugging-mit-inspector.md)).

Beispiel (Tool-Gerüst):
```csharp
[McpServerToolType]
public class ExampleTool(ApiServerClient api)
{
    [McpServerTool, Description("Liest <Datenbereich> (App-only, nur lesend).")]
    public Task<string> GetExample(string id) => api.GetAsync($"/api/example/{Uri.EscapeDataString(id)}");
}
```

## B. Zweiter MCP-Server (eigene Audience)
Ziel: ein weiterer Dienst ohne Zugriff auf Rechte des ersten.

| Baustein | Vorgehen |
|---|---|
| Entra-App | eigene App `mcp-<name>-<env>` mit eigener Identifier-URI `api://mcp-<name>-<env>` und eigenem Scope; **nicht** `Mcp.Access` der ersten App wiederverwenden. Muster: `azuread_application.mcp` in `modules/entra-id/main.tf` |
| Client-Apps | je Client eine Pre-Registration mit delegierter Berechtigung auf **genau diese** MCP-App (Muster `mcp_oauth_client`, `copilot_mcp_oauth_client`) oder per Self-Service-Skript ([FOUNDRY-OAUTH.md](FOUNDRY-OAUTH.md)) |
| Web App + Identität | eigene `azurerm_linux_web_app` mit eigener Managed Identity (Muster `modules/infra`). Rollen nur an dieser Identität vergeben |
| Backend-Zugriff | eigene App-Rolle auf der zugehörigen API-App, nur für die neue MI. Dadurch erreicht der neue MCP nur die Datenbereiche, die ausdrücklich zugewiesen wurden |
| Gateway | Das Gateway ist heute **pro MCP-Instanz** konfiguriert (`BACKEND_MCP_URL`, `MCP_APP_ID_URI`, `MCP_APP_CLIENT_ID`). Für einen zweiten MCP eine zweite Gateway-Instanz (Modul `modules/gateway` mit `count`/`for_each`) – nicht eine geteilte, damit Audiences getrennt bleiben |
| CI | `deploy.yml` um einen Publish-/Deploy-Schritt erweitern; App-Namen aus Terraform-Outputs validieren (Muster `*-mcp`) |

## C. Neue REST-/Graph-Datenquelle
Neuer typisierter Client im ApiServer (Muster `src/ApiServer/Health/`: feste Hosts, kein Auto-Redirect, begrenzte Retries/Pagination, Allowlist, Startvalidierung der Optionen). Identität: Managed Identity der API; nur lesende Rollen.

## D. Anbindung in Clients
| Client | Weg |
|---|---|
| ChatGPT, Claude | Pre-Registration (Terraform) oder Gateway (DCR/CIMD, Tests offen) |
| Copilot Studio | Pre-Registration (`web{}`, Secret) oder „Dynamic discovery“ gegen das Gateway (ungetestet) |
| Foundry | Self-Service-App ([FOUNDRY-OAUTH.md](FOUNDRY-OAUTH.md)) |

## E. Mandantenspezifisch
Tenant-ID, Subscription-IDs, Redirect-URIs der Clients, Namenspräfix (`entramcp-<env>`), App-Service-SKU, `azure_health_subscription_ids`. Alles in `terraform/terraform.<env>.tfvars`.

Siehe auch [REUSING-MCP-BASE.md](REUSING-MCP-BASE.md).
