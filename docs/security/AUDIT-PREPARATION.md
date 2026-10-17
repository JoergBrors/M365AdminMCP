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
| Audit-Logs | Container-Logs (Kudu, 1 Tag), App Insights/Log Analytics (.NET-Apps), Azure Activity Log (nicht geprüft) | teilweise |
| Vulnerability Management | [SECURITY-FINDINGS.md](SECURITY-FINDINGS.md) | vorhanden |
| Sicherheitsfreigaben | – | **fehlt** (Eigentümer) |
| Ausnahmen und Restrisiken | Findings mit Status `accepted`/`open` | vorhanden |
