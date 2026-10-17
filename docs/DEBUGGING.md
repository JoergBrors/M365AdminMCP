# Debugging-Runbook (exakt)

Lokales Debugging von ApiServer und McpServer in VS Code mit dem **MCP Inspector v2** (automatisch verbunden), Prüfung gegen das Dev-System in Azure und Fehlereingrenzung.
Entscheidung: [ADR-0015](adr/0015-lokales-debugging-mit-inspector.md). Deployment: [`DEPLOYMENT-RUNBOOK.md`](DEPLOYMENT-RUNBOOK.md).

## 1. Voraussetzungen
| Was | Prüfen | Quelle |
|---|---|---|
| .NET SDK ≥ 10.0 | `dotnet --version` | `Install-Prerequisites.ps1` |
| VS Code + C#-Erweiterung (`coreclr`-Debugger) | Run-and-Debug zeigt die Konfigurationen | – |
| **Node.js ≥ 22.19.0** (Inspector v2) | `node --version` | manuell installieren; das Startskript prüft die Version |
| PowerShell 7 (`pwsh`) | `pwsh --version` | Startskript und Tasks laufen mit `pwsh` |
| Azure-Anmeldung (nur zum Erzeugen der Settings) | `pwsh ./scripts/Connect-Azure.ps1` | `.env` mit Tenant/Subscription |
| **Lokale Settings** (git-ignoriert) | `src/ApiServer/appsettings.Development.json` und `src/McpServer/appsettings.Development.json` | `pwsh ./scripts/Export-TerraformLocalSettings.ps1 -Environment dev` (liest Secrets aus dem Key Vault) |

Das Startskript bricht mit einer klaren Meldung ab, wenn Node zu alt ist oder eine Development-Settings-Datei fehlt bzw. kein `AzureAd:ClientSecret` enthält (Werte werden nie ausgegeben).
Development-Settings nie committen (`.gitignore`: `**/appsettings.Development.json`; `CopyToPublishDirectory="Never"`).

## 2. Ports
| Dienst | Port | Bindung | Zweck |
|---|---|---|---|
| ApiServer | 5043 (HTTP), 7043 (HTTPS) | `localhost` (Loopback) | API, Swagger (nur Development), `/healthz` |
| McpServer | 5143 (HTTP), 7143 (HTTPS) | `localhost` (Loopback) | `/mcp`, OAuth-Endpunkte, `/healthz` |
| Inspector UI + API | 6274 | `127.0.0.1` | Web-Oberfläche **und** Backend (v2 hat **keinen** separaten Proxy-Port 6277 mehr) |
| Inspector MCP-Apps-Sandbox | 6275 | `127.0.0.1` | wird vom Inspector v2 zusätzlich geöffnet |

## 3. Inspector v2 – was im Repo festgelegt ist
- **Version gepinnt:** `tools/mcp-inspector/package.json` (`@modelcontextprotocol/inspector` = `2.11.0`) + `package-lock.json` (committed). Installation mit `npm ci --ignore-scripts` beim ersten Start in `tools/mcp-inspector/node_modules/` (git-ignoriert). Versionswechsel = Pin ändern, `npm install --ignore-scripts` im Ordner ausführen, Lockfile committen.
- **Start:** `scripts/Start-McpInspector.ps1` (direkt `node …/clients/launcher/build/index.js`, kein `npx` zur Laufzeit).
- **Umgebung des Inspector-Prozesses:** `MCP_INSPECTOR_API_TOKEN=<Zufallstoken>`, `CLIENT_PORT=6274`, `HOST=127.0.0.1`, `MCP_AUTO_OPEN_ENABLED=false` (den Browser öffnet das Skript selbst, mit Deep-Link). `DANGEROUSLY_OMIT_AUTH` wird nicht gesetzt.
- **Token:** pro Start 32 Zufallsbytes (Hex), nur im Speicher des Startskripts und im Inspector-Prozess. Weder Datei noch Git noch Doku. Die Inspector-Ausgabe wird durchgereicht, Zeilen mit dem Token (Banner-URL, „Auth token:“) werden unterdrückt bzw. maskiert. Er steht nur in der Kommandozeile des Browser-Aufrufs der lokalen Sitzung.
- **Deep-Link (v2):** `http://127.0.0.1:6274/?MCP_INSPECTOR_API_TOKEN=<Token>&serverUrl=http%3A%2F%2Flocalhost%3A5143%2Fmcp&transport=http&autoConnect=<Token>`
  Der Inspector verbindet automatisch, wenn `autoConnect` exakt dem API-Token entspricht (ohne gültigen Wert wird `serverUrl` verworfen). `transport` ist `http` (Streamable HTTP) oder `sse`. Veraltete v1-Parameter (`transport=streamable-http`, `MCP_PROXY_AUTH_TOKEN`, „Via Proxy“, Port 6277) gelten nicht mehr.

## 4. Was F5 bei „Debug API + MCP + Inspector“ tut (tatsächliche Reihenfolge)
1. `preLaunchTask: prepare-inspector-debug` (sequenziell):
   1. `stop-local-debug-servers` → `scripts/Stop-LocalDebugServers.ps1` beendet **nur eigene** Prozesse (siehe Abschnitt 7) und wartet bis zu 10 s auf freie Ports.
   2. `build` → `dotnet build EntraMcpMvp.sln`.
   3. `mcp-inspector` (Hintergrundtask) → `Start-McpInspector.ps1`: prüft Voraussetzungen, startet den Inspector auf 127.0.0.1:6274 und meldet `[inspector] ready`. Erst dann gilt der Task als bereit (Muster `^\[inspector\] ready`).
2. Compound startet parallel die Debug-Sessions **„Debug ApiServer (Inspector, no prep)“** (ohne Swagger-Browser) und **„Debug McpServer (Inspector, no prep)“** (`McpAuth__RequireAuthentication=false`, `ApiServer__BaseUrl=http://localhost:5043`).
3. Das Startskript pollt im Hintergrund `http://localhost:5043/healthz` und `http://localhost:5143/healthz` (1 s Takt, max. 180 s). Antworten beide mit 200, öffnet es den Browser mit dem Deep-Link. Wird die Zeit überschritten: `[inspector] ERROR …` und Task-Fehler.
4. Der Inspector verbindet automatisch mit `http://localhost:5143/mcp`; die Tools sind sofort sichtbar.
5. Beenden (Shift+F5 oder Stop): `stopAll` beendet beide Debug-Sessions; `postDebugTask: stop-local-debug-servers` (an der McpServer-Konfiguration) beendet den Inspector und evtl. übrige Server-Prozesse. Das Startskript endet danach selbst.
6. Erneutes F5: Schritt 1.i räumt Reste auf; der Ablauf ist wiederholbar.

Die Konfiguration **„Debug API + MCP“** (ohne Inspector) und die Einzelkonfigurationen bleiben unverändert (Auth an).

## 5. Erwartetes Ergebnis lokal
- **17 Tools** (10 bestehende + 7 Microsoft-Health-Tools, SDK liefert snake_case-Namen). Neu: `intune_service_health`, `intune_service_incidents`, `microsoft_service_messages`, `azure_service_health`, `azure_resource_health`, `azure_subscription_resource_health`, `microsoft_health_summary` (siehe `docs/microsoft-health/`). Bestehend: `get_tasks_app_only`, `get_tasks_delegated`, `get_service_health_overview`, `get_service_health_detail`, `get_degraded_service_health_details`, `get_service_health_issues`, `get_message_center_messages`, `get_office365_active_user_detail`, `get_m365_app_user_detail`, `list_adoption_reports`, `get_adoption_report`.
- App-only-Tools funktionieren: McpServer holt mit dem lokalen Client Secret ein App-only-Token für den ApiServer, der ApiServer ebenso für Graph (`UseManagedIdentity=false`). Beispiel `get_tasks_app_only` → `{"callerType":"app-only",…}`.
- `get_tasks_delegated` wurde entfernt (Token als LLM-Parameter, in Produktion nicht lauffähig; siehe `docs/security/SECURITY-DECISIONS.md`, D-2).
- Breakpoints z. B. in `Tools/M365StatusTool.cs`, `Services/ApiServerClient.cs`, `ApiServer/Services/GraphApiClient.cs` greifen beim Aufruf im Inspector.

## 6. Sicherheit des lokalen Debug-Modus
- `McpAuth__RequireAuthentication=false` steht **ausschließlich** in der Konfiguration „Debug McpServer (Inspector, no prep)“. Terraform, App Settings und `appsettings*.json` enthalten es nicht (Azure setzt `true`).
- **Development-Guard** (`src/McpServer/Program.cs`): `false` außerhalb `Development` → Startfehler `InvalidOperationException: McpAuth:RequireAuthentication=false ist nur in der Development-Umgebung erlaubt.`
- **Loopback-Schutz** (`Program.cs`, nur bei abgeschalteter Auth): jede Anfrage braucht eine Loopback-Quelladresse **und** einen Host-Header `localhost`/`127.0.0.1`/`[::1]`, sonst `403`. Ein Tunnel (z. B. `mydevice-mcp.brors.date`), der auf `localhost:5143` zeigt, behält den öffentlichen Host-Header und wird abgewiesen; zugleich Schutz vor DNS-Rebinding. Beim Start loggt der Server eine Warnung, dass `/mcp` unauthentisiert ist.
- Kestrel bindet `localhost` (nur Loopback). Den Inspector-Port nicht per `HOST=0.0.0.0` öffnen (der Inspector verweigert das ohne Zusatz-Flag ohnehin).
- Für Tests mit echtem OAuth über den Tunnel die Konfiguration **„Debug API + MCP“** (Auth an) verwenden (Abschnitt 9).

## 7. Prozessverwaltung (`scripts/Stop-LocalDebugServers.ps1`)
Beendet nur Prozesse, die zu diesem Repo gehören: Kommandozeile bzw. geladene DLL unter `src/ApiServer/bin/`, `src/McpServer/bin/` oder `tools/mcp-inspector/` (Prozesse `dotnet`/`node`). Alles andere, das nur einen der überwachten Ports (5043, 7043, 5143, 7143, 6274, 6275) belegt, bleibt unberührt; es erscheint die Warnung „Port N wird von einem fremden Prozess belegt … wird NICHT beendet“ (der folgende Start scheitert dann sichtbar mit „address in use“). Das Skript endet erst, wenn die eigenen Ports frei sind (max. 10 s). Manuell: `pwsh ./scripts/Stop-LocalDebugServers.ps1`.

## 8. Weitere Abläufe
### A. ApiServer allein mit Swagger
„Debug ApiServer“ (F5) öffnet `http://localhost:5043/swagger/index.html?localDebug=1`. **Authorize** (OAuth2 Auth-Code + PKCE gegen Entra, Scope `api://api-server-dev/Tasks.ReadWrite`); Redirect-URIs `http://localhost:5043/swagger/oauth2-redirect.html` und `https://localhost:7043/…` sind per Terraform registriert. Swagger ist nur in `Development` aktiv.

### B. Inspector gegen das Dev-System in Azure
Inspector lokal starten (Abschnitt 4 startet auch die Server; für reines Azure-Ziel den Inspector separat starten: `pwsh ./scripts/Start-McpInspector.ps1 -McpUrl https://entramcp-dev-mcp.azurewebsites.net/mcp -HealthUrls https://entramcp-dev-mcp.azurewebsites.net/healthz`).
| Variante | Voraussetzung | Einstellung |
|---|---|---|
| API-Key | App Setting `McpAuth__ApiKey` in Azure gesetzt (derzeit leer = deaktiviert, ADR-0014) | im Inspector Header `X-MCP-API-Key: <Key>` ergänzen |
| OAuth | Redirect-URI des Inspectors bei einem Entra-Client registrieren (derzeit **nicht** vorhanden) | Client-ID und Scope `api://mcp-server-dev/Mcp.Access offline_access openid profile` |
Erwartet: ohne Token `401` mit `WWW-Authenticate: Bearer resource_metadata=…`. Cold-Start (F1): erster Request bis 30 s.

### C. Komponenten gezielt prüfen
```powershell
Invoke-RestMethod http://localhost:5043/healthz ; Invoke-RestMethod http://localhost:5143/healthz
Invoke-RestMethod http://localhost:5143/.well-known/oauth-protected-resource      # nur mit Auth an sinnvoll
# Inspector-CLI (gepinnt) gegen den lokalen Server, ohne Browser:
$env:MCP_CATALOG_PATH = "$env:TEMP\insp-catalog.json"
node tools/mcp-inspector/node_modules/@modelcontextprotocol/inspector/clients/launcher/build/index.js --cli http://localhost:5143/mcp --method tools/list
node tools/mcp-inspector/node_modules/@modelcontextprotocol/inspector/clients/launcher/build/index.js --cli http://localhost:5143/mcp --method tools/call --tool-name get_tasks_app_only
```

## 9. Echte Clients gegen den lokalen Server (Tunnel)
Nicht mit der Inspector-Konfiguration (Loopback-Schutz blockt den Tunnel absichtlich). Stattdessen **„Debug API + MCP“** (Auth an; `McpAuth__ExternalBaseUrl=https://mydevice-mcp.brors.date`), Tunnel auf `http://localhost:5143` schalten, Connector auf `https://mydevice-mcp.brors.date/mcp`. Die Redirect-URI des Connectors muss in Entra stehen (ADR-0005). Tunnel danach schließen.

## 10. Logs und Telemetrie
| Wo | Wie |
|---|---|
| Lokal | Debug-Konsole/Terminal (Console + Debug, `Information`). Der OAuth-Proxy loggt `/token`-Felder ohne `code`/`code_verifier`. Startskript-Ausgabe im Terminal-Panel „mcp-inspector“ (Präfix `[inspector]`, Inspector-Zeilen `[inspector:node]`). |
| Azure Log Stream | `az webapp log tail --name entramcp-dev-mcp --resource-group rg-entramcp-dev` (bzw. `-api`). Dateisystem-Logging ist in Terraform nicht konfiguriert; ggf. `az webapp log config … --application-logging filesystem --level information`. |
| Application Insights | Connection String ist gesetzt; die csproj enthalten kein Application-Insights-SDK – ob Telemetrie ankommt, ist **nicht verifiziert**. |
| Log Analytics | Tageslimit 1 GB; danach keine Logs bis zur UTC-Tagesgrenze. |

## 11. Fehlerbilder

| Symptom | Ursache | Maßnahme |
|---|---|---|
| F5 hängt bei Task `mcp-inspector` | Inspector meldet kein `[inspector] ready` | Terminal „mcp-inspector“ lesen: Node-Version, fehlende Settings, `npm ci`-Fehler |
| `[inspector] ERROR Node … benötigt wird >= 22.19.0` | Node zu alt | Node ≥ 22.19.0 installieren |
| `[inspector] ERROR …appsettings.Development.json fehlt` | lokale Settings fehlen | `Export-TerraformLocalSettings.ps1 -Environment dev` |
| `[inspector] ERROR Health-Check nach 180 s nicht erfolgreich` | API/MCP nicht gestartet (Build-/Startfehler) | Debug-Konsole der beiden Server prüfen |
| Browser öffnet, Inspector verbindet nicht automatisch | `autoConnect` ≠ Token (alte Browser-Registerkarte/Lesezeichen) | Tab schließen, F5 erneut (neuer Token pro Start) |
| Inspector zeigt „Unauthorized“ | Seite ohne `MCP_INSPECTOR_API_TOKEN` geöffnet | nur den vom Skript geöffneten Deep-Link verwenden |
| `address already in use` | fremder Prozess auf dem Port | Warnung des Stop-Skripts lesen; fremden Prozess selbst beenden |
| Build: `MSB3021`/`MSB3027` (DLL gesperrt) | alte Debug-Session | `Stop-LocalDebugServers.ps1` |
| McpServer: `McpAuth:RequireAuthentication=false ist nur in der Development-Umgebung erlaubt.` | `ASPNETCORE_ENVIRONMENT` ≠ `Development` | Launch-Konfiguration prüfen |
| `403 Unauthenticated local debug mode: loopback access only.` | Aufruf über Tunnel/fremden Host-Header | beabsichtigt; lokal `http://localhost:5143` verwenden |
| ApiServer: `GraphAuth ist nicht konfiguriert …` | Development-Settings fehlen/leer | `Export-TerraformLocalSettings.ps1` |
| `HttpRequestException … 401` MCP→API | Audience/Rolle/Secret | `AzureAd:ApiAppIdUri`, Rollenzuweisung `mcp_to_api_task_readwrite`, Secret aktuell? |
| `… 403` von Graph | Graph-Rolle am ApiServer fehlt | Terraform `api_to_graph_*` / `api_managed_identity_to_graph_*` |
| `get_service_health_detail` liefert Fallback-JSON | direkter Graph-Endpoint 403/404 | erwartet (ADR-0011) |
| `AADSTS9010010` / `7000218` / `9002325` / `65001` | OAuth-Konfiguration | ADR-0004 und ADR-0005 |
| Claude/ChatGPT: Verbindung nach Pause fehlgeschlagen | Cold-Start (F1) | erneut versuchen (ADR-0013) |
| Breakpoint nicht getroffen | Release-Build/alte DLL | `build` neu, Debug-Konfiguration prüfen |

## 12. Checkliste vor dem Merge
- [ ] `dotnet build EntraMcpMvp.sln` grün
- [ ] F5 „Debug API + MCP + Inspector“: Browser öffnet, Inspector verbunden, 17 Tools sichtbar
- [ ] Bei Auth-/OAuth-Änderungen: Claude, ChatGPT und Copilot geprüft (Konfiguration „Debug API + MCP“ + Tunnel oder Azure)
- [ ] `McpAuth__RequireAuthentication` nur in der Inspector-Launch-Konfiguration `false`
- [ ] Keine Secrets im Diff
