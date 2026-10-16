# Debugging-Runbook (exakt)

Wie man ApiServer und McpServer lokal in VS Code debuggt, mit dem [MCP Inspector](https://github.com/modelcontextprotocol/inspector) testet, gegen das Dev-System in Azure prüft
und Fehler eingrenzt. Entscheidung: [ADR-0015](adr/0015-lokales-debugging-mit-inspector.md). Deployment: [`DEPLOYMENT-RUNBOOK.md`](DEPLOYMENT-RUNBOOK.md).

## 1. Voraussetzungen
| Was | Prüfen | Quelle |
|---|---|---|
| .NET SDK ≥ 10.0 | `dotnet --version` | `Install-Prerequisites.ps1` |
| VS Code + C#-Erweiterung (`coreclr`-Debugger) | Run-and-Debug-Ansicht zeigt die Konfigurationen | – |
| Node.js (für `npx`) | `node --version`, `npx --version` | manuell installieren |
| Azure-Anmeldung | `pwsh ./scripts/Connect-Azure.ps1` | `.env` mit Tenant/Subscription |
| **Lokale Settings** (git-ignoriert) | existieren `src/ApiServer/appsettings.Development.json` und `src/McpServer/appsettings.Development.json`? | sonst `pwsh ./scripts/Export-TerraformLocalSettings.ps1 -Environment dev` (liest Secrets aus dem Key Vault, schreibt auch `.env`) |

Inhalt der Development-Settings (Schlüssel, ohne Werte): ApiServer `AzureAd:{TenantId,ClientId,ClientSecret,Audience}`, `GraphAuth:UseManagedIdentity=false`, `SwaggerOAuth:{ClientId,Scope}`;
McpServer `AzureAd:{…,ApiAppIdUri,UseManagedIdentity=false}`, `McpAuth:{ExternalBaseUrl,Scope,ApiKey,RequireAuthentication}`, `ApiServer:BaseUrl`.
**Nie committen** (`.gitignore`: `**/appsettings.Development.json`; zusätzlich `CopyToPublishDirectory="Never"`).

## 2. Ports und Prozesse
| Dienst | HTTP | HTTPS | Zweck |
|---|---|---|---|
| ApiServer | 5043 | 7043 | API, Swagger (`/swagger`, nur Development), `/healthz` |
| McpServer | 5143 | 7143 | `/mcp`, OAuth-Endpunkte, `/healthz` |
| Inspector UI | 6274 | – | Browser-Oberfläche |
| Inspector Proxy | 6277 | – | Node-Proxy, der den MCP-Server anspricht |

Belegte Ports freigeben: `pwsh ./scripts/Stop-LocalDebugServers.ps1` (beendet alle Prozesse auf 5043, 7043, 5143, 7143, 6274, 6277; wird von den `prepare-*`-Tasks automatisch ausgeführt).

## 3. VS-Code-Konfigurationen (`.vscode/launch.json`)
| Name | Was startet | Auth am MCP | `preLaunchTask` |
|---|---|---|---|
| **Debug API + MCP + Inspector** (Compound) | ApiServer + McpServer (Inspector-Variante) + Inspector | **aus** (`McpAuth__RequireAuthentication=false`) | `prepare-inspector-debug` |
| Debug API + MCP (Compound) | ApiServer + McpServer | an | `prepare-combined-debug` |
| Debug ApiServer | nur ApiServer, öffnet Swagger (`/swagger/index.html?localDebug=1`) | an | `prepare-api-debug` |
| Debug McpServer | nur McpServer, öffnet `/healthz`; `McpAuth__ExternalBaseUrl=https://mydevice-mcp.brors.date` | an | `prepare-mcp-debug` (+ `postDebugTask: stop-local-debug-servers`) |
| (versteckt) `… (no prep)` | werden von den Compounds benutzt, damit der Build nur einmal läuft | – | – |

Gemeinsam: `ASPNETCORE_ENVIRONMENT=Development`, McpServer → `ApiServer__BaseUrl=http://localhost:5043`.

Tasks (`.vscode/tasks.json`): `stop-local-debug-servers` → `build` (`dotnet build EntraMcpMvp.sln`) → (`mcp-inspector`, nur bei Inspector).
`mcp-inspector` startet `npx -y @modelcontextprotocol/inspector` als Hintergrundtask mit `MCP_AUTO_OPEN_ENABLED=false` (Browser öffnet nicht automatisch) in einem eigenen Terminal-Panel.
Der Task gilt als „bereit“, wenn die Ausgabe `MCP Inspector is up and running` enthält (Muster im `problemMatcher`; bei abweichender Inspector-Version Muster anpassen).

## 4. Ablauf A – Lokal debuggen mit Inspector (Standard)
1. Run-and-Debug öffnen, **„Debug API + MCP + Inspector“** wählen, **F5**.
2. Reihenfolge, die VS Code ausführt: Ports freigeben → Build → Inspector-Task → beide .NET-Prozesse starten. Warte, bis im Terminal `Now listening on: http://localhost:5043` (API) und `http://localhost:5143` (MCP) erscheint.
3. Im Terminal-Panel `mcp-inspector` die ausgegebene URL öffnen. Neuere Inspector-Versionen drucken sie mit Proxy-Token (`http://localhost:6274/?MCP_PROXY_AUTH_TOKEN=…`); diese URL verwenden. Alternativ direkt vorkonfiguriert:
   `http://localhost:6274/?transport=streamable-http&serverUrl=http://localhost:5143/mcp` (Token ggf. anhängen/in den Konfigurationsdialog eintragen).
4. Im Inspector einstellen: **Transport Type** = `Streamable HTTP`, **URL** = `http://localhost:5143/mcp`, **Connection Type** = `Via Proxy`. Kein Auth-Header nötig. **Connect**.
5. Tab **Tools → List Tools**: erwartet sind 11 Tools aus 5 Klassen (Anzeige je nach SDK in PascalCase oder snake_case):
   `GetTasksAppOnly`, `GetTasksDelegated` (TasksTool); `GetServiceHealthOverview`, `GetServiceHealthDetail`, `GetDegradedServiceHealthDetails`, `GetServiceHealthIssues` (M365StatusTool);
   `GetMessageCenterMessages` (M365MessagesTool); `GetOffice365ActiveUserDetail`, `GetM365AppUserDetail` (M365AdoptionTool); `ListAdoptionReports`, `GetAdoptionReport` (M365AdoptionCatalogTool).
   Maßgeblich ist, was `List Tools` tatsächlich anzeigt.
6. Breakpoint setzen, z. B. in `Tools/M365StatusTool.cs` (`GetServiceHealthOverview`) und in `ApiServer/Services/GraphApiClient.cs` (`GetGraphV1Async`). Tool im Inspector ausführen (**Run Tool**) → der Debugger hält an.
7. Beenden: Debug-Session stoppen (Shift+F5). `stopAll` beendet beide .NET-Prozesse. **Den Inspector-Task im Terminal-Panel separat beenden** (Papierkorb-Symbol) oder `Stop-LocalDebugServers.ps1` ausführen (killt 6274/6277 mit).

Erwartetes Verhalten lokal:
- App-only-Tools (Status, Messages, Reports, `GetTasksAppOnly`) funktionieren, weil der McpServer mit dem lokalen Client Secret ein App-only-Token für den ApiServer holt und der ApiServer ebenso für Graph (`UseManagedIdentity=false`).
- `GetTasksDelegated` braucht ein echtes Nutzer-Token als Parameter und funktioniert ohne Auth nicht sinnvoll.
- Die erste Antwort kann länger dauern (Token holen); Graph-Reports als CSV→JSON können groß sein.

## 5. Ablauf B – ApiServer allein mit Swagger
1. **„Debug ApiServer“** (F5): öffnet `http://localhost:5043/swagger/index.html?localDebug=1`.
2. **Authorize** in Swagger: OAuth2 Auth-Code + PKCE gegen Entra (`SwaggerOAuth:ClientId`, Scope `api://api-server-dev/Tasks.ReadWrite`, `prompt=select_account`). Redirect-URIs `http://localhost:5043/swagger/oauth2-redirect.html` und `https://localhost:7043/…` sind per Terraform registriert.
3. Endpunkte ausprobieren: `GET /api/tasks`, `GET /api/m365/status/health-overviews`, `GET /api/m365/reports/catalog` usw. Nutzer-Tokens tragen `scp=Tasks.ReadWrite` und erfüllen die Policy `TasksReadWrite`.
4. Swagger-Antworten sind im Development per `no-store` nicht gecacht; nach Änderungen Seite hart neu laden.

## 6. Ablauf C – Inspector gegen das Dev-System in Azure
Zweck: die **deployte** Auth-Kette prüfen. Inspector läuft lokal (`npx -y @modelcontextprotocol/inspector` oder Task `mcp-inspector` einzeln), Ziel = `https://entramcp-dev-mcp.azurewebsites.net/mcp`.

| Variante | Voraussetzung | Einstellung im Inspector |
|---|---|---|
| **C1: API-Key** | In Azure App Setting `McpAuth__ApiKey` gesetzt (derzeit leer = deaktiviert, ADR-0014); Key aus sicherer Quelle | Transport `Streamable HTTP`, URL `…/mcp`, Custom Header `X-MCP-API-Key: <Key>`. Der Server vergibt dem Aufrufer die Rolle `Mcp.Access`. |
| **C2: OAuth** | Inspector-Redirect-URI (`http://localhost:6274/oauth/callback`) in einem Entra-Client eingetragen. Aktuell **nicht** registriert; ein public client erlaubt dort `http://localhost` nicht ohne Weiteres (die Clients sind mit `https`-Redirects für ChatGPT/Claude angelegt) | Authentication → OAuth mit Client-ID eines public Clients; Scope `api://mcp-server-dev/Mcp.Access offline_access openid profile` |

Erwartete Ergebnisse: ohne Token `401` mit `WWW-Authenticate: Bearer resource_metadata=…` (Beweis, dass Auth aktiv ist). Mit gültiger Auth `List Tools` wie in Ablauf A.
Cold-Start: erster Request bis 30 s – im Inspector Timeout ggf. erhöhen oder erneut verbinden (ADR-0013).

## 7. Ablauf D – Tunnel (`mydevice-mcp.brors.date`) für echte Clients gegen lokalen Server
Ziel: Claude/ChatGPT/Copilot sprechen mit dem **lokalen** Server (Breakpoints bei echtem OAuth).
1. Tunnel von `https://mydevice-mcp.brors.date` auf `http://localhost:5143` einrichten (außerhalb des Repos; Werkzeug nach Wahl).
2. **„Debug API + MCP“** (Auth an; **nicht** die Inspector-Variante) starten. `McpAuth__ExternalBaseUrl` steht in „Debug McpServer“ bereits auf die Tunnel-URL (die Compound-„no prep“-Variante setzt es ebenfalls).
3. Client-Connector auf `https://mydevice-mcp.brors.date/mcp` zeigen lassen. Die Redirect-URI des Connectors muss in Entra stehen (Terraform/`Add-McpOauthRedirectUri.ps1`).
4. Prüfen: `https://mydevice-mcp.brors.date/.well-known/oauth-protected-resource` liefert `resource = https://mydevice-mcp.brors.date/mcp`.
Der Tunnel macht `/mcp` öffentlich erreichbar – nur bei aktivierter Auth nutzen und danach schließen.

## 8. Ablauf E – Einzelne Komponenten gezielt prüfen (curl/PowerShell)
```powershell
# Gesundheit
Invoke-RestMethod http://localhost:5143/healthz ; Invoke-RestMethod http://localhost:5043/healthz

# Discovery lokal (Auth an)
Invoke-RestMethod http://localhost:5143/.well-known/oauth-protected-resource
Invoke-RestMethod http://localhost:5143/.well-known/oauth-authorization-server

# MCP-Handshake ohne Auth (nur Inspector-Variante, Auth aus). Streamable HTTP verlangt beide Accept-Typen.
$h = @{ Accept = "application/json, text/event-stream" }
$body = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"ps","version":"1"}}}'
Invoke-WebRequest http://localhost:5143/mcp -Method Post -Headers $h -ContentType "application/json" -Body $body
```
Antworten sind SSE-formatiert (`event: message` / `data: {…}`); stateless → keine `Mcp-Session-Id`.

## 9. Ablauf F – Logs und Telemetrie
| Wo | Wie |
|---|---|
| Lokal (Development) | Debug-Konsole/Terminal; Provider Console + Debug sind aktiv, Level `Information` (`Microsoft.AspNetCore` = `Warning`). OAuth-Proxy loggt `/token`-Felder (ohne `code`/`code_verifier`) und Entra-Fehler-Bodies. |
| Azure Log Stream | `az webapp log tail --name entramcp-dev-mcp --resource-group rg-entramcp-dev` (bzw. `-api`). Dateisystem-Logging ist in Terraform **nicht** konfiguriert; ggf. vorübergehend `az webapp log config --name entramcp-dev-mcp --resource-group rg-entramcp-dev --application-logging filesystem --level information` setzen. |
| Application Insights | Ressource `entramcp-dev-appi` (Connection String ist als App Setting gesetzt). Die csproj enthalten **kein** Application-Insights-SDK; ob Telemetrie ankommt, hängt von der App-Service-Auto-Instrumentierung ab – **nicht verifiziert**. Vor Verlass darauf im Portal prüfen. |
| Log Analytics | Tageslimit 1 GB: ist es erreicht, werden bis zur UTC-Tagesgrenze keine Logs mehr angenommen. |
| Web-App-Konfiguration | Portal → Web App → Konfiguration → Key-Vault-Referenzen müssen „Resolved“ sein. |

## 10. Fehlerbilder beim Debuggen

| Symptom | Ursache | Maßnahme |
|---|---|---|
| Build: `MSB3021`/`MSB3027`, DLL „wird von … Debug Adapter … gesperrt“ | alte Debug-Session läuft | `Stop-LocalDebugServers.ps1` oder Session beenden |
| `address already in use` auf 5043/5143/6274/6277 | alter Prozess | `Stop-LocalDebugServers.ps1` |
| McpServer startet nicht: `McpAuth:RequireAuthentication=false ist nur in der Development-Umgebung erlaubt.` | `ASPNETCORE_ENVIRONMENT` ist nicht `Development` | Launch-Konfiguration prüfen / Env-Variable setzen |
| McpServer startet nicht mit `InvalidOperationException` zu `EnableLegacySse`/`Stateless` | SSE-Option aktiviert | entfernen (ADR-0003) |
| ApiServer: `GraphAuth ist nicht konfiguriert …` | `appsettings.Development.json` fehlt oder `ClientSecret` leer | `Export-TerraformLocalSettings.ps1 -Environment dev` |
| Tool-Fehler `HttpRequestException … 401` zwischen MCP und API | App-only-Token wird abgelehnt: Audience/Rolle oder Secret falsch | `AzureAd:ApiAppIdUri` (`api://api-server-dev`), Rollenzuweisung `mcp_to_api_task_readwrite`, Secret aktuell? |
| `… 403` von Graph | Graph-Rolle fehlt am ApiServer-Principal | Terraform: `azuread_app_role_assignment.api_to_graph_*` (lokal) bzw. `api_managed_identity_to_graph_*` (Azure) |
| `GetServiceHealthDetail` liefert Fallback-JSON mit `directMicrosoftGraphStatus` | direkter Graph-Endpoint 403/404 | erwartetes Verhalten (ADR-0011) |
| Inspector „Connection Error“/Proxy-Token | Inspector verlangt Token | URL mit `MCP_PROXY_AUTH_TOKEN` aus dem Terminal benutzen |
| Inspector verbindet nicht zu Azure | Cold-Start, 401, CORS | warten/erneut; Auth-Header prüfen; Proxy-Modus (`Via Proxy`) nutzen |
| `AADSTS9010010` (resource-Parameter) | Client spricht direkt Entra statt die Fassade | `authorization_servers` im Protected-Resource-Dokument prüfen (muss die MCP-Origin sein), ADR-0004 |
| `AADSTS7000218` (client_secret/assertion fehlt) | Client ist confidential, wird aber als public benutzt (oder `web`-Redirect) | ADR-0005: public client = `public_client{}`; Copilot = `web{}` + Secret |
| `AADSTS9002325` (PKCE/Origin) | Redirect-URI auf Plattform `spa` | auf `public_client{}` ändern (ADR-0005) |
| `AADSTS65001` (not consented) | Grant `offline_access` gegen `mcp-server` fehlt | `azuread_service_principal_delegated_permission_grant.*` prüfen |
| `redirect_uri is not in the allowed list` | `McpAuth:AllowedRedirectHosts` aktiv und Host fehlt, oder nicht `https` | Host ergänzen oder Allowlist leeren |
| Claude/ChatGPT: „Connector kann nicht verbinden“ nach Pause | Cold-Start (F1) | erneut versuchen (ADR-0013) |
| Breakpoint wird nicht getroffen (Symbole nicht geladen) | Release-Build oder alte DLL | `build` neu, Debug-Konfiguration prüfen, „Just My Code“ aus |

## 11. Debugging-Checkliste vor dem Merge
- [ ] `dotnet build EntraMcpMvp.sln` grün
- [ ] Tool im Inspector (Ablauf A) mit realen Parametern ausgeführt, Antwort plausibel
- [ ] Bei Auth-/OAuth-Änderungen: Ablauf C/D mit Claude **und** ChatGPT **und** Copilot geprüft
- [ ] `McpAuth__RequireAuthentication` nur in der Inspector-Launch-Konfiguration `false`, nirgends in Terraform/Settings
- [ ] Keine Secrets in Diff/Logs
