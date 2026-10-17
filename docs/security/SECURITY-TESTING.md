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

## Lücken
Kein Testprojekt für den McpServer; keine Autorisierungstests auf Tool-Ebene; keine Tests für Rate Limiting/Limits (nicht implementiert); keine echten Client-Tests (Claude, ChatGPT, Copilot Studio, Foundry).
