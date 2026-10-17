# Audit-Vorbereitung

| Nachweis | Fundstelle | Stand |
|---|---|---|
| Architekturentscheidungen | [docs/adr/](../adr/README.md), [IMPLEMENTATION-AUDIT.md](../adr/IMPLEMENTATION-AUDIT.md) | vorhanden |
| Berechtigungsmatrix | [SECURITY-ARCHITECTURE.md](SECURITY-ARCHITECTURE.md), `docs/ENTRA-ID-SETUP.md`, `docs/microsoft-health/` | vorhanden; Rechte des CI-Principals nur per Abfrage bestätigt |
| CI/CD-Sicherheitsnachweise | `.github/workflows/deploy.yml` (OIDC, Plan-Kommentare, Gitleaks, pip-audit, NuGet-Audit) | vorhanden |
| Dependency-Scans | CI-Ausgaben (`pip-audit`, `dotnet list package --vulnerable`) | grün zum Stand 2026-10-17 |
| Secret-Scans | Gitleaks in CI | vorhanden |
| SAST | – | **fehlt** |
| Terraform-Scans | `fmt`/`validate`/Plan; kein IaC-Scanner | teilweise |
| Testberichte | [FASTMCP-GATEWAY-TESTS.md](../FASTMCP-GATEWAY-TESTS.md), CI-Läufe (GitHub Actions) | vorhanden |
| Audit-Logs | Container-Dateilogs (Kudu, 1 Tag/35 MB) für Gateway, API und MCP (API/MCP per Terraform-Block `logs`); **keine Telemetrie in Application Insights** (kein SDK im Code, am 2026-10-17 nur 1 Request und 1 Trace in der Instanz), Azure Activity Log nicht geprüft | **unzureichend für ein Audit** (Aufbewahrung 1 Tag) |
| Vulnerability Management | [SECURITY-FINDINGS.md](SECURITY-FINDINGS.md) | vorhanden |
| Sicherheitsfreigaben | – | **fehlt** (Eigentümer) |
| Ausnahmen und Restrisiken | Findings mit Status `accepted`/`open` | vorhanden |
