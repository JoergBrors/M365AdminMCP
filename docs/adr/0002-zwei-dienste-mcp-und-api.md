# ADR-0002: Zwei Dienste – McpServer (Fassade) + ApiServer (Graph-Zugriff)
- Status: Akzeptiert · Datum: 2026-10-16 (rückwirkend)

## Kontext
LLM-Clients sprechen MCP. Die Daten kommen aus Microsoft Graph (Service Health, Message Center, Reports) und erfordern hohe
App-Berechtigungen (`ServiceHealth.Read.All`, `ServiceMessage.Read.All`, `Reports.Read.All`).

## Entscheidung
- **McpServer** (`src/McpServer`) spricht MCP/OAuth nach außen. Er besitzt **keine** Graph-Berechtigungen und fordert keine Graph-Tokens an.
- **ApiServer** (`src/ApiServer`) ist eine geschützte Web-API, hält die Graph-Rechte und kapselt alle Graph-Aufrufe.
- Aufrufkette: LLM → `/mcp` (Tool) → `ApiServerClient` → ApiServer-Controller → `GraphApiClient` → Microsoft Graph.

## Alternativen
Ein einziger Dienst, der MCP und Graph mischt: kleiner, aber die externe MCP/OAuth-Angriffsfläche und die Graph-Rechte lägen im selben
Prozess; keine eigenständig testbare Daten-Schicht (Swagger).

## Konsequenzen
+ Least Privilege: Graph-Rechte nur am ApiServer; API separat per Swagger testbar.
+ Neue Datenquelle = Controller + Tool, getrennt testbar.
− Zusätzlicher Hop (Latenz), zwei Deployments, zwei Token-Strecken (ADR-0006).
− Beim Cold-Start (F1) müssen unter Umständen zwei Apps nacheinander aufwachen (ADR-0013).

## Umsetzung
- McpServer: `src/McpServer/Tools/*.cs` → `src/McpServer/Services/ApiServerClient.cs` (Konfiguration `ApiServer:BaseUrl`).
- ApiServer: `src/ApiServer/Controllers/*.cs` → `src/ApiServer/Services/GraphApiClient.cs` → `src/ApiServer/Auth/GraphTokenService.cs`.
- Graph-Rechte: `terraform/modules/entra-id/main.tf` (`required_resource_access` in `azuread_application.api`, `azuread_app_role_assignment.api_to_graph_*`)
  **und** das Managed-Identity-Pendant `terraform/modules/infra/main.tf` (`azuread_app_role_assignment.api_managed_identity_to_graph_*`).

## Änderungs-Leitfaden
Neue Graph-Permission: **beide** Terraform-Stellen ändern (Entra-Modul für lokal/Client-Secret, Infra-Modul für Production/Managed Identity),
sonst funktioniert es nur in einer Umgebung. Neuer Endpoint: Controller + `GraphApiClient`-Methode + MCP-Tool + Registrierung
in `McpServer/Program.cs` (`.WithTools<...>()`).
