# ADR-0016: Microsoft Health – Intune/Microsoft 365 (Graph) und Azure Service/Resource Health (ARM)
- Status: Akzeptiert · Datum: 2026-10-16

## Kontext
Der Server soll Intune-Service-Health, Intune-Vorfälle, Message-Center-Meldungen sowie Azure Service Health und Resource Health lesend bereitstellen. Es gibt bereits generische Graph-Health-Tools (`get_service_health_*`, `get_message_center_messages`), aber ohne Intune-Filter, ohne Azure-Teil und ohne einheitliches Antwortschema. Azure Resource Health braucht Azure Resource Manager (ARM) und Azure-RBAC – bisher besitzt keine Identität des Projekts eine ARM-Rolle.

## Entscheidung
1. **Platzierung gemäß ADR-0002:** Clients, Identität und Konfiguration im **ApiServer** (`src/ApiServer/Health/`), dünne MCP-Tools im **McpServer** (`MicrosoftHealthTools`). Kein zweiter MCP-Server, bestehende Tools bleiben unverändert.
2. **Identität:** dieselbe Managed Identity der API-Web-App für Graph und ARM (`ArmTokenService` analog `GraphTokenService`). **Keine** neuen Graph-Berechtigungen: `ServiceHealth.Read.All` und `ServiceMessage.Read.All` existieren bereits für App-Registrierung und Managed Identity.
3. **Azure-RBAC optional und standardmäßig aus:** Variable `azure_health_subscription_ids` (Standard `[]`) steuert gleichzeitig die Anwendungs-Allowlist und die Rollenzuweisung (`azurerm_role_assignment.api_azure_health_reader`, Built-in-Rolle *Service Health Security Reader*, Scope Subscription). Grund: Der CI-Deployer darf Rollen bisher nur auf dem Key Vault zuweisen; ein Standardwert ≠ `[]` würde den Apply brechen.
4. **Sicherheit:** Allowlist für Subscriptions; Resource-IDs strikt validiert (keine URLs/Hosts/Query/Traversal); feste Hosts; `nextLink` nur `https` auf `graph.microsoft.com` bzw. `management.azure.com`; HTTP-Client ohne Redirects; Startvalidierung der Optionen (inkl. Tenant-Gleichheit); nur GET.
5. **Robustheit:** begrenzte Retries (429 mit `Retry-After`, 502/503/504, Netzwerk, Timeout), begrenzte Pagination, Teilfehler-Isolation in der Zusammenfassung.
6. **Antwortmodell:** einheitliches Schema (`source`, `queriedAtUtc`, `status`, `items`, `errors`, `isPartial`, `truncated`, …); `status` beschreibt den Abruf, nicht die Gesundheit; kein Gesundheitsurteil bei fehlenden Daten (`no_data`).
7. **Geteilte Validierung:** `HealthInputValidator` liegt im McpServer und wird vom ApiServer per Compile-Link genutzt (wie `ReportCatalog`, ADR-0009), damit es genau eine Implementierung gibt.

## Alternativen
- Clients direkt im McpServer: hätte Graph-/ARM-Rechte auf die externe MCP-Angriffsfläche gezogen (verletzt ADR-0002).
- Neue App-Registrierung/Identität für Azure: unnötig; die vorhandene Managed Identity genügt.
- `Reader` oder Custom Role als Standard: `Reader` zu breit; Custom Role braucht `roleDefinitions/write` beim Deployer.
- RBAC-Standardwert mit einer Subscription: bricht den CI-Apply ohne vorherige Freigabe.
- Server-seitige Graph-Filter (`$filter=service eq …`) statt clientseitig: in der Doku nicht für alle Felder zugesichert; bewusst nur dokumentierte Abfragen.

## Konsequenzen
+ Intune-Health/-Incidents/-Messages sofort nutzbar (Rollen vorhanden); Azure-Teil kontrolliert aktivierbar.
+ Einheitliche, testbare Fehler- und Antwortstruktur; 97 neue Tests.
− Aktivierung des Azure-Teils braucht eine manuelle Freigabe (Deployer-Rolle) und einen zweiten PR.
− Zwei teils überlappende Tool-Gruppen (alte `get_service_health_*` und neue `intune_*`/`microsoft_*`); die alten bleiben aus Kompatibilitätsgründen bestehen.
− Auth-Modell unverändert (ADR-0006): alle MCP-Nutzer sehen dieselben tenantweiten Daten.

## Umsetzung
`src/ApiServer/Health/*`, `src/ApiServer/Auth/ArmTokenService.cs`, `src/ApiServer/Controllers/MicrosoftHealthController.cs`, `src/ApiServer/Program.cs`, `src/McpServer/Tools/MicrosoftHealthTools.cs`, `src/McpServer/Utils/HealthInputValidator.cs`, `terraform/modules/infra/main.tf` (`api_azure_health_reader`, App Settings), `terraform/variables.tf`, `tests/ApiServer.Tests/Health/*`. Ausführlich: [`docs/microsoft-health/`](../microsoft-health/01-existing-architecture.md).

## Änderungs-Leitfaden
Weitere Subscriptions: nur `azure_health_subscription_ids` erweitern (Deployer-Recht beachten). Neue Health-Quelle: Client in `Health/`, Controller-Route, Tool, Tests, Summary erweitern. Andere Rolle: `azure_health_role_name` (nie Contributor/Owner). Notabschaltung ohne Deployment: App Setting `MicrosoftHealth__Enabled=false`.
