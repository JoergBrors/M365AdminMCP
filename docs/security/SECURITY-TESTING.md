# Security-Tests und CI

## Schnelle PR-Checks (laufen in jedem PR, `.github/workflows/deploy.yml`)
| Job/Schritt | Prüft | Stand |
|---|---|---|
| `build`: `dotnet build` + `dotnet test` | 180 Tests (ApiServer) | grün |
| `build`: NuGet-Audit | `dotnet list package --vulnerable --include-transitive`, bricht bei Treffern ab | neu, lokal grün nach F-002 |
| `gateway-ci`: `ruff` | Lint Gateway | grün |
| `gateway-ci`: `pytest` | 58 Tests: OAuth-Flow, Limits, DCR, Consent, PKCE, Redirect, Konfiguration, Logging, Issuer | grün |
| `gateway-ci`: `pip-audit` | bekannte Schwachstellen in Python-Abhängigkeiten | grün |
| `gateway-ci`: Gitleaks | Secrets (Test-Fixtures per `.gitleaks.toml` ausgenommen) | grün |
| `gateway-ci`: Paketbau + Import in leerer Umgebung | fehlende Module/Pakete | grün |
| `terraform-plan`: `fmt`, `validate`, Plan | IaC-Korrektheit, ungewollte Löschungen | grün |

## Zeitintensive/externe Prüfungen (noch nicht eingerichtet)
SAST (z. B. CodeQL), IaC-Scanner, SBOM-Erzeugung, DAST gegen eine Testinstanz. Nicht gegen produktive öffentliche Endpunkte ohne Freigabe.

## Manuell / Live (nicht in CI)
`src/OAuthGateway/tools/gateway_e2e.py`: echter OAuth-Durchlauf (Browser-Login) gegen das Dev-Gateway, `tools/list` und `tools/call` bis Graph. MCP-Inspector-Konfiguration: [ADR-0015](../adr/0015-lokales-debugging-mit-inspector.md).

## Live-Nachweise (manuell, 2026-10-17)
| Prüfung | Ergebnis |
|---|---|
| `tools/list` über das Gateway | 17 Tools |
| `tools/call get_service_health_overview` (Entra-Login im Browser) | Graph-Daten, unter Policy `DataAccess` und Zuweisungspflicht |
| Rate Limit Gateway `/register` (Burst 13) | 10× `201`, danach `429`; `/healthz` und Discovery unberührt |
| McpServer HSTS | `Strict-Transport-Security: max-age=2592000` |
| Maskierung im Zugriffsprotokoll | kein unmaskierter `code=` im Container-Log |
| UserContext | API-Log: `MCP caller authMethod=entra oid=… tid=… upn=…` |
| Nicht getestet | Nicht zugewiesener Nutzer gegen die API (F-016, kein Testnutzer), API-Key-Pfad, Last, echte Clients außer FastMCP-Client |

## Lücken
Kein Testprojekt für den McpServer; keine Autorisierungstests auf Tool-Ebene; keine Tests für Rate Limiting/Limits (nicht implementiert); keine echten Client-Tests (Claude, ChatGPT, Copilot Studio, Foundry).
