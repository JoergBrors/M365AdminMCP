# Architecture Decision Records (ADR)

Jede Datei hält **eine** Entscheidung fest: Kontext, Entscheidung, Konsequenzen und – wichtig für die
Weiterentwicklung – **wo sie im Code umgesetzt ist** und **wie man sie ändert**.

Fundstellen sind `Datei` + Symbol (Zeilennummern driften, Symbole nicht). Gesamtübersicht
Entscheidung → Code: [`../DESIGN-DECISION-MAP.md`](../DESIGN-DECISION-MAP.md).

| ADR | Titel | Status |
|---|---|---|
| [0001](0001-terraform-als-einziger-deployment-weg.md) | Terraform als einziger Deployment-Weg | Akzeptiert |
| [0002](0002-zwei-dienste-mcp-und-api.md) | Zwei Dienste: McpServer (Fassade) + ApiServer (Graph-Zugriff) | Akzeptiert |
| [0003](0003-streamable-http-stateless.md) | Streamable HTTP, stateless, nur `/mcp` | Akzeptiert |
| [0004](0004-oauth-proxy-fassade-vor-entra.md) | OAuth-Proxy-Fassade vor Entra ID | Akzeptiert |
| [0005](0005-vorregistrierte-oauth-clients.md) | Vorregistrierte OAuth-Clients (public vs. confidential) | Akzeptiert |
| [0006](0006-token-strategie-app-only-und-obo.md) | Token-Strategie: App-only als Standard, OBO optional | Akzeptiert (mit Schuld) |
| [0007](0007-identitaeten-und-secrets.md) | Managed Identity in Azure, Client-Secret lokal, Key-Vault-Referenzen | Akzeptiert |
| [0008](0008-datengetriebener-report-katalog.md) | Datengetriebener Report-Katalog statt 92 Tools | Akzeptiert |
| [0009](0009-geteilte-quelldateien-api-mcp.md) | Geteilte Quelldateien zwischen ApiServer und McpServer | Akzeptiert (mit Schuld) |
| [0010](0010-graph-reports-beta-json-csv.md) | Graph-Reports über Beta, JSON-first mit CSV-Fallback | Akzeptiert |
| [0011](0011-service-health-fallback.md) | Service-Health-Detail mit Fallback über Overview + Issues | Akzeptiert |
| [0012](0012-ci-cd-oidc-plan-apply-schutz.md) | CI/CD: OIDC, Plan im PR, Apply nur auf `main`, Lösch-Sicherheitsnetz | Akzeptiert |
| [0013](0013-free-tier-app-service.md) | Free-Tier (F1) App Service Plan | Akzeptiert |
| [0014](0014-doppelte-mcp-authentifizierung-api-key.md) | Zweiter Auth-Weg: `X-MCP-API-Key` | Akzeptiert (mit Risiko) |
| [0015](0015-lokales-debugging-mit-inspector.md) | Lokales Debugging ohne Auth mit MCP Inspector | Akzeptiert |
| [0016](0016-microsoft-health-intune-und-azure.md) | Microsoft Health: Intune/M365 (Graph) und Azure Service/Resource Health (ARM) | Akzeptiert |
| [0017](0017-fastmcp-oauth-gateway.md) | FastMCP-OAuth-Gateway vor dem .NET-MCP-Server | Vorgeschlagen (G1/G2 erfüllt, G3–G7 offen) |

Alle ADRs wurden **rückwirkend** aus dem Code und der bestehenden Doku abgeleitet (Datum der Dokumentation:
2026-10-16). Wo die ursprüngliche Motivation nur aus Code-Kommentaren stammt, steht das im Kontext.

## Vorlage

```markdown
# ADR-NNNN: Titel
- Status: Vorgeschlagen | Akzeptiert | Ersetzt durch ADR-XXXX · Datum: YYYY-MM-DD
## Kontext
## Entscheidung
## Alternativen
## Konsequenzen
## Umsetzung
## Änderungs-Leitfaden
```

Neue ADR: nächste Nummer, in obiger Tabelle und in `DESIGN-DECISION-MAP.md` eintragen.

Code-Abgleich aller ADRs: [IMPLEMENTATION-AUDIT.md](IMPLEMENTATION-AUDIT.md). Sicherheitsfolgen: [../security/README.md](../security/README.md).
