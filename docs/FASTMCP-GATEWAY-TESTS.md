# FastMCP OAuth Gateway – Tests und Gates

Stand 2026-10-17. Ausführen: `cd src/OAuthGateway && pip install -r requirements.txt -r requirements-dev.txt && pytest tests -q` (lokal: 39 passed).

## Was die Tests prüfen
`tests/harness.py` startet einen Mock-IdP (Entra-Ersatz), ein Python-Backend mit denselben JWT-Regeln wie der .NET-Server (Stub!) und das Gateway. Abgedeckt: Discovery, DCR (getrennte Client-IDs, kein Secret), Consent/CSRF/Browser-Bindung, feste Upstream-Callback, PKCE, Redirect-Manipulation, `initialize`/`tools/list`/`tools/call`, Fehlerweitergabe, Abbruch (Client-Timeout, Gateway bleibt gesund), PUT/PATCH → 404/405, Konfigurationsvalidierung, Health-Endpunkte, Secret-freie Logs.
`poc/fastmcp-gateway/tools/dotnet_leg.py`: Versuch gegen den echten .NET-Server; offline nicht erfolgreich (Microsoft.Identity.Web verlangt HTTPS-Metadaten und Microsoft-Issuer; keine Aufweichung vorgenommen).

## Gate-Status (Stand 2026-10-17, Dev, Gateway auf B1)
| Gate | Status | Beleg |
|---|---|---|
| F1 Plan provisionierbar | **erfüllt, aber unzureichend** | Ein sechster Linux-F1-Plan wurde angelegt. Die F1-CPU-Quota war nach Absturz und Redeploys erschöpft (`QuotaExceeded`); auf Entscheidung des Eigentümers läuft der Gateway-Plan jetzt als **B1** |
| G1 Echtes Entra-Token → echter .NET-`/mcp` | **erfüllt** | `tools/list` (damals 18 Tools, inzwischen 17 nach Entfernen von `GetTasksDelegated`) und `tools/call get_service_health_overview` (Graph-Daten) über das Gateway, `src/OAuthGateway/tools/gateway_e2e.py` |
| G2 Auth Code + PKCE über Entra | **erfüllt** | Entra-Login im Browser, Consent, Token, Zugriff (vom Eigentümer bestätigt, Screenshot „Authentication successful“, Log `POST /token 200`) |
| G3 DCR/Discovery mit echten Clients | **erfüllt für ChatGPT, Claude, Grok** (Copilot Studio, M365 Copilot offen) | Gateway-Log 2026-10-17: nach der Weiterleitung zu `chatgpt.com/connector_platform_oauth_redirect` (17:07, 17:09) folgen `POST /token` 200 und `POST /mcp` 200; Claude und Grok vom Eigentümer als funktionierend gemeldet (nicht von mir gemessen). Foundry läuft bewusst **nicht** über das Gateway, sondern per OAuth-Passthrough direkt gegen die MCP-Adresse ([FOUNDRY-OAUTH.md](FOUNDRY-OAUTH.md)). Warum ChatGPT jetzt funktioniert, ist ungeklärt (Client-Fix von OpenAI oder Wirkung der Gateway-Änderungen; Flag `authorization_response_iss_parameter_supported` steht wieder auf dem Standard `true`) |
| G4 Transport/Streaming | **teilweise** | `initialize`, `tools/list`, `tools/call` ok. Lange SSE-Antworten, GET/DELETE, parallele Requests **nicht** geprüft |
| G5 Neustart/Refresh | **teilweise** | Eine DCR-Registrierung überstand `az webapp restart`. Refresh-Token und Re-Deploy **nicht** geprüft |
| G6 Backend-Zugriff/Sicherheit | **teilweise** | Direktzugriff auf den .NET-`/mcp` bleibt möglich, siehe [FASTMCP-GATEWAY-SECURITY.md](FASTMCP-GATEWAY-SECURITY.md) |
| G7 Kosten | **offen** | B1-Preis nicht verifiziert; in Azure Cost Management beobachten |

## Lehren aus dem Live-Test (jeweils mit Regressionstest)
1. `aiohttp` fehlte im Lockfile (Container-Absturz beim Start) – Azure-Tables-Pfad wird jetzt getestet.
2. `AzureProvider` prüft als Audience die Gateway-App; Entra stellt das Token für die **MCP-App** aus – Audience wird auf MCP-App-GUID und -URI festgelegt (`MCP_APP_CLIENT_ID`).
3. Der Proxy spiegelt das Protokoll des Clients zum Backend; der .NET-Server (2025-11-25) lehnt moderne Per-Request-Metadaten ab – die Backend-Verbindung ist auf `legacy` (Handshake) festgelegt.
4. Ein Admin-Consent-Grant per Terraform scheiterte (CI-Principal ohne Graph-Recht); entfernt, Nutzer-Consent beim ersten Login.
5. Der Start des Containers dauert ca. 140–170 s (auch auf B1). Für Clients mit kurzem Timeout ein reales Problem.

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

## F1-Befund (2026-10-17)
Nach dem ersten Deploy (Startabsturz durch fehlendes `aiohttp`, danach mehrere Redeploys eines 37-MB-Pakets mit ca. 8.700 Dateien) stand die Web App auf `QuotaExceeded` (`usageState: Exceeded`): Kudu/OneDeploy lieferte „Site Disabled (403)“, `/healthz` 403. Das ist das tägliche F1-CPU-Kontingent; der Zeitpunkt des Zurücksetzens wurde nicht verifiziert. Gegenmaßnahmen in diesem PR: `WEBSITE_RUN_FROM_PACKAGE=1` (kein Entpacken) und kleineres Paket (24 MB, ca. 5.400 Dateien, ohne Bytecode). Ob das für F1 reicht, ist **ungeprüft**; falls nicht, Hosting-NO-GO für F1 (kein automatischer Wechsel auf einen bezahlten Plan).
