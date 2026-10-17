# Security-Dokumentation

Stand 2026-10-17. Grundsatz: Nur belegte Aussagen gelten als verifiziert; alles andere ist als „unbestätigt“ oder „nicht geprüft“ markiert.

| Dokument | Inhalt |
|---|---|
| [SECURITY-ARCHITECTURE.md](SECURITY-ARCHITECTURE.md) | Vertrauensgrenzen, Identitäten, Datenflüsse |
| [THREAT-MODEL.md](THREAT-MODEL.md) | STRIDE je Komponente |
| [API-HARDENING.md](API-HARDENING.md) | ASP.NET-Core-Maßnahmen mit Fundstellen |
| [OAUTH-SECURITY.md](OAUTH-SECURITY.md) | Gateway, OAuth-Fassade, Tokenmodell |
| [MCP-SECURITY.md](MCP-SECURITY.md) | MCP-spezifische Risiken (Tools, Prompt Injection, Tokens) |
| [AZURE-SECURITY.md](AZURE-SECURITY.md) | Hosting, Netzwerk, F1/B1-Grenzen |
| [TERRAFORM-SECURITY.md](TERRAFORM-SECURITY.md) | State, CI-Identität, Löschschutz |
| [SECRETS-MANAGEMENT.md](SECRETS-MANAGEMENT.md) | Wo welche Secrets liegen |
| [SECURITY-CHECKLIST.md](SECURITY-CHECKLIST.md) | Prüfliste vor Releases |
| [SECURITY-TESTING.md](SECURITY-TESTING.md) | Automatisierte Prüfungen in CI |
| [PENTEST-PREPARATION.md](PENTEST-PREPARATION.md), [PENTEST-SCOPE.md](PENTEST-SCOPE.md) | Vorbereitung autorisierter Tests |
| [AUDIT-PREPARATION.md](AUDIT-PREPARATION.md) | Nachweise für ein Audit |
| [SECURITY-FINDINGS.md](SECURITY-FINDINGS.md) | Findings-Register |
| [SECURITY-DECISIONS.md](SECURITY-DECISIONS.md) | Sicherheitsrelevante Entscheidungen und offene Freigaben |
| [INCIDENT-RESPONSE.md](INCIDENT-RESPONSE.md) | Vorgehen bei Vorfällen |

Gesamtstand und offene Punkte: [../STATUS.md](../STATUS.md). Verwandt: [ADR-Audit](../adr/IMPLEMENTATION-AUDIT.md), [Gateway-Sicherheit](../FASTMCP-GATEWAY-SECURITY.md), [Offene Fragen](../OPEN-QUESTIONS.md).
