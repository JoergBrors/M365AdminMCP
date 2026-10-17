# Threat Model (STRIDE)

Bewertung: W = Eintrittswahrscheinlichkeit, A = Auswirkung (N/M/H). „Kontrolle“ nennt nur belegte Maßnahmen. Restrisiko ohne Pentest eine Einschätzung, kein Messwert.

| Komponente | Bedrohung (STRIDE) | W | A | Kontrolle (belegt) | Restrisiko / Finding |
|---|---|---|---|---|---|
| Python-Gateway | **S**poofing: gefälschter Client, fremder Browser im Consent | M | M | Consent mit CSRF + Browser-Bindung, Redirect-Allowlist per Registrierung (Tests) | gering |
| | **T**ampering: manipulierte `redirect_uri`, PKCE-Bypass | M | H | abgewiesen (Tests `test_redirect_manipulation_rejected`, `test_pkce_wrong_verifier_rejected`) | gering |
| | **R**epudiation: fehlende Nachvollziehbarkeit | M | M | Zugriffs- und Fehlerprotokoll, Redirect-Log (`iss`, Ziel) | Log nur 1 Tag/35 MB, kein Log Analytics |
| | **I**nformation Disclosure: Codes/State im Log, Upstream-Secret | M | H | Maskierung (F-010), `client_secret=None` in DCR (Test) | Live-Retest offen |
| | **D**oS: Massenregistrierung, Kaltstart | M | M | Rate Limit je IP (`/register` 10/min), Body-Limits | F-011 (TTL/Gesamtquote fehlt), F-014 |
| | **E**levation: Token-Substitution (FastMCP-JWT am Backend) | N | H | Gateway reicht nur das Entra-Token weiter; Backend validiert Audience | gering |
| McpServer | S: API-Key-Erraten | N | H | in Azure nicht konfiguriert; `FixedTimeEquals` | gering |
| | I: Tool-Daten tenantweit | H | H | `Mcp.Access` | **F-004** |
| | D: fehlende Limits | M | M | Rate Limiter + Request-Limit (`RequestHardening`) | gering; Response-Größen unbegrenzt |
| ApiServer | T: Path-Injection per Report-Parameter | M | H | Validierung | behoben (F-001) |
| | E: Policy zu breit | M | M | eine Policy für alle | F-005 |
| | I: Preauthorized CSV-URLs | N | M | Download ohne Graph-Token, URL nicht geloggt (ADR-0010) | gering |
| Entra ID | S: falscher Tenant | N | H | Single-Tenant-Apps, Tenant-GUID-Pflicht im Gateway | gering |
| Microsoft Graph / ARM | E: Überprivilegierung der MI | N | H | nur Lese-Rollen | Rollenumfang je Datenbereich offen |
| GitHub Actions | T/E: kompromittierter Workflow | N | H | OIDC, geschützter `main`, Plan/Apply-Schutz | CI-Principal ist sehr mächtig (RBAC Administrator auf Subscription) |
| Terraform State | I: Secrets im State | M | H | Remote State | **F-013** (RBAC nicht geprüft) |
| Key Vault | I: Fehlkonfiguration | N | H | RBAC, Purge Protection | gering |
| Externe LLM-Clients | T/E: Prompt Injection steuert Tool-Aufrufe | M | M | alle 17 MCP-Tools lesen nur (die API-Route `POST /tasks` ist kein Tool); `GetTasksDelegated` (Token als Parameter) ist entfernt (F-006) | siehe [MCP-SECURITY.md](MCP-SECURITY.md) |
