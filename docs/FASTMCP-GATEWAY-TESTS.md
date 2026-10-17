# FastMCP OAuth Gateway – Tests und Gates

Stand 2026-10-17. Ausführen: `cd src/OAuthGateway && pip install -r requirements.txt -r requirements-dev.txt && pytest tests -q` (lokal: 39 passed).

## Was die Tests prüfen
`tests/harness.py` startet einen Mock-IdP (Entra-Ersatz), ein Python-Backend mit denselben JWT-Regeln wie der .NET-Server (Stub!) und das Gateway. Abgedeckt: Discovery, DCR (getrennte Client-IDs, kein Secret), Consent/CSRF/Browser-Bindung, feste Upstream-Callback, PKCE, Redirect-Manipulation, `initialize`/`tools/list`/`tools/call`, Fehlerweitergabe, Abbruch (Client-Timeout, Gateway bleibt gesund), PUT/PATCH → 404/405, Konfigurationsvalidierung, Health-Endpunkte, Secret-freie Logs.
`poc/fastmcp-gateway/tools/dotnet_leg.py`: Versuch gegen den echten .NET-Server; offline nicht erfolgreich (Microsoft.Identity.Web verlangt HTTPS-Metadaten und Microsoft-Issuer; keine Aufweichung vorgenommen).

## Gate-Status
| Gate | Status | Begründung |
|---|---|---|
| F1 Plan provisionierbar | **offen** | lesend geprüft (siehe unten), tatsächliche Provisionierung nicht durchgeführt |
| G1 Echtes Entra-Token → echter .NET-`/mcp` | **offen** | braucht deployte Gateway-App im Dev-Tenant (Freigabe) |
| G2 Entra Auth-Code + PKCE über Gateway | **offen** | dito |
| G3 DCR/Discovery mit Copilot Studio + ChatGPT/Claude | **offen** | keine echten Clients getestet |
| G4 Transport/Streaming | **teilweise** | Basisfälle gegen Mock bestanden; lange SSE-Antworten, GET-Stream, DELETE (liefert 400 im stateless-Modus), Cold Start, parallele Requests **nicht** geprüft |
| G5 Neustart/Refresh | **offen** | Azure-Tables-Store nicht gegen echten Storage getestet (kein Azurite/Account) |
| G6 Backend-Zugriff/Sicherheit | **teilweise** | siehe [FASTMCP-GATEWAY-SECURITY.md](FASTMCP-GATEWAY-SECURITY.md); Direktzugriff bleibt offen |
| G7 Kosten | **teilweise** | siehe [FASTMCP-GATEWAY-COSTS.md](FASTMCP-GATEWAY-COSTS.md) |

**Echte Clients (Claude, ChatGPT, Copilot Studio, M365 Copilot, Foundry): nicht getestet. Es wird keine Kompatibilität behauptet.**

## F1-Vorprüfung (nur lesende Azure-Abfragen, Subscription BRORS.NET)
- Die Subscription besitzt bereits **fünf Linux-F1-Pläne in West Europe** (`entramcp-dev-plan`, `ASP-API-a3b9`, `euw001webapp`, `asp-policy-studio`, `euw001api`). Ein weiterer F1-Plan ist damit nicht ausgeschlossen, ein Limit wurde aber **nicht** abgefragt; ob Azure den sechsten zulässt, zeigt erst die Erstellung.
- `az appservice list-locations --sku F1 --linux-workers-enabled` listet West Europe.
- `az webapp list-runtimes --os-type linux`: Python 3.10–3.15 verfügbar; FastMCP 4.1.0 verlangt Python ≥ 3.10; getestet mit 3.12.
- Nicht geprüft: CPU-/Memory-/Storage-Quoten von F1, Verhalten des 37-MB-Artefakts auf F1, Startzeit. Cold-Start während Discovery/Callback ist ein reales Risiko; falls F1 sich als ungeeignet erweist, ist das ein **Hosting-NO-GO** – kein automatischer Wechsel auf einen bezahlten Plan.

## Pflichtprogramm nach Freigabe
1. Apply nur nach PR-Plan mit CI-Identität; Gateway-Deploy; `/healthz`, `/readyz`, Discovery.
2. G1: Entra-Login im Browser → Token über Gateway → `tools/list` am echten .NET; negativ: FastMCP-JWT direkt an .NET → 401.
3. G2/G3: Claude, ChatGPT, Copilot Studio, M365 Copilot, Foundry jeweils: Discovery, DCR/CIMD, Consent, `initialize`, `tools/list`, `tools/call`, Refresh, Reconnect, Gateway-Neustart.
4. G4/G5: lange Antworten, parallele Requests, Neustart und Re-Deploy mit bestehenden Registrierungen.
