# Sicherheitsrelevante Entscheidungen

## Getroffen (Eigentümer oder dokumentiert)
| Entscheidung | Begründung | Datum |
|---|---|---|
| Gateway-Plan B1 statt F1 | F1-CPU-Quota erschöpft (`QuotaExceeded`) | 2026-10-17 (Eigentümer) |
| Kein Admin-Consent-Grant per Terraform für das Gateway | CI-Principal ohne Graph-Recht (403); Rechte nicht erweitert | 2026-10-17 |
| Foundry-App per Self-Service-Skript, nicht Terraform | Ziel: eigener Service Principal durch den Anwender | 2026-10-17 (Eigentümer) |
| Gateway: Storage-Shared-Keys aktiv (akzeptiert) | Provider-Zugriff auf die Data Plane beim Anlegen (F-012) | 2026-10-17 |
| Backend-Verbindung des Gateways auf Handshake-Protokoll festgelegt | .NET-Server lehnt moderne Per-Request-Metadaten ab | 2026-10-17 |

## Umgesetzt in dieser Arbeit (Branch `feature/security-hardening-docs`)
Report-Parametervalidierung (F-001), NuGet-Patch (F-002), zeitkonstanter API-Key-Vergleich (F-003), CI-NuGet-Audit.

## Benötigen eine ausdrückliche Freigabe (nicht still entschieden)
| ID | Entscheidung | Optionen | Empfehlung |
|---|---|---|---|
| D-1 | Autorisierungsmodell (F-004/F-005) | (a) bleibt tenantweit, (b) App-Rollen je Datenbereich auf der MCP-App, (c) nutzerbezogene Filter | (b): Rollen `Mcp.Reports.Read`, `Mcp.Health.Read`, `Mcp.Messages.Read`; **Migration**: Policies zuerst additiv einführen (Scope **oder** bisherige Rolle), Clients/Nutzer zuweisen, erst nach Messfenster die alte Policy entfernen |
| D-2 | `GetTasksDelegated` (F-006) | entfernen / Token nur serverseitig (OBO, benötigt Secret statt MI) | entfernen, solange kein Task-Anwendungsfall besteht |
| D-3 | Netzwerkisolation (F-008) | IP-Restriktion auf Gateway-Ausgang; Private Endpoint (kostenpflichtig) | IP-Restriktion prüfen, bevor Geld ausgegeben wird |
| D-4 | `X-MCP-API-Key` (ADR-0014) | abschaffen / pro Client scopen | abschaffen (in Azure ohnehin ungenutzt) |
| D-5 | Rate Limiting / Limits (F-009, F-011) | in-app (.NET `RateLimiter`, Gateway-Middleware) | umsetzen (P1) |
| D-6 | Zuständigkeit OAuth (Fassade vs. Gateway) | pro Client genau ein Weg | Q-ARCH-1 |
