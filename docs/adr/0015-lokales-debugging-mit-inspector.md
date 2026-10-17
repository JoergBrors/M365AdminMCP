# ADR-0015: Lokales Debugging ohne Auth mit MCP Inspector (v2, Auto-Connect)
- Status: Akzeptiert · Datum: 2026-10-16 (überarbeitet nach Prüfung der tatsächlichen Inspector-v2-Version)

## Kontext
Tools sollen lokal im VS-Code-Debugger mit dem [MCP Inspector](https://github.com/modelcontextprotocol/inspector) getestet werden – mit **einem** F5-Start, ohne manuelle Connect-Schritte.
Der volle OAuth-Flow gegen Entra ist dafür unpraktisch (Inspector-Redirect-URI nicht registriert). Das Deployment (Auth an, `Production`) darf nicht beeinflusst werden.

Die erste Fassung (ungepinntes `npx`, v1-Annahmen) war fehlerhaft: Der Marker `MCP Inspector is up and running` existiert in v2 nicht (v2: `MCP Inspector Web is up and running at:`), der `preLaunchTask` wurde daher nie „bereit“; Port 6277, `MCP_PROXY_AUTH_TOKEN`, `transport=streamable-http` und „Via Proxy“ gelten nur für v1; der Inspector startete vor den Servern, öffnete keinen Browser, wurde beim Beenden nicht gestoppt, und `Stop-LocalDebugServers.ps1` beendete jeden Prozess auf den Ports.

## Entscheidung
1. **Inspector v2 gepinnt:** `tools/mcp-inspector/package.json` (exakt `2.11.0`) + committed `package-lock.json`; Installation per `npm ci --ignore-scripts` ins git-ignorierte `node_modules`. Start direkt mit Node (≥ 22.19.0), nicht per `npx`.
2. **Ein Startskript** `scripts/Start-McpInspector.ps1`: Voraussetzungen prüfen → Zufallstoken (nur im Speicher) → Inspector an `127.0.0.1:6274` mit `MCP_INSPECTOR_API_TOKEN` → `[inspector] ready` → auf `/healthz` von API und MCP warten → Browser mit Deep-Link `?MCP_INSPECTOR_API_TOKEN=…&serverUrl=…&transport=http&autoConnect=<Token>` öffnen. Token wird in der Ausgabe maskiert; nie in Git/Dateien.
3. **VS Code:** Compound „Debug API + MCP + Inspector“ mit `preLaunchTask: prepare-inspector-debug` (Stop → Build → Inspector-Hintergrundtask), eigener API-Konfiguration ohne Swagger-Browser und MCP-Konfiguration mit `McpAuth__RequireAuthentication=false` und `postDebugTask: stop-local-debug-servers`.
4. **Sicherheitsnetze im Code** (`McpServer/Program.cs`): Development-Guard (Startfehler außerhalb `Development`) **und** Loopback-Schutz bei abgeschalteter Auth (Loopback-Quelladresse + Loopback-Host-Header, sonst 403; blockt Tunnel und DNS-Rebinding).
5. **Prozessbeendigung nach Zuordnung** (`Stop-LocalDebugServers.ps1`): nur Prozesse mit Repo-Komponenten-Pfad (Kommandozeile oder geladene DLL); fremde Port-Besitzer werden nur gemeldet.

## Alternativen
- `npx @latest` zur Laufzeit: nicht reproduzierbar, v1/v2-Bruch. Verworfen.
- `DANGEROUSLY_OMIT_AUTH` für den Inspector: unnötig, da der lokale Token Auto-Connect ermöglicht. Verworfen.
- API-Key (`X-MCP-API-Key`, ADR-0014) auch lokal: erfordert Secret-Verwaltung und Header-Konfiguration im Inspector. Verworfen für den Standardfall.
- Statisches Token im Repo: verboten.

## Konsequenzen
+ Ein F5, reproduzierbare Inspector-Version, Breakpoints in MCP und API, kein Eingriff in Azure-Auth oder Terraform.
+ Mehrfaches F5 räumt kontrolliert nur eigene Prozesse ab.
− Benötigt Node ≥ 22.19.0 und `pwsh`; erster Start installiert ~118 npm-Pakete (Netz nötig).
− Lokal ist `/mcp` unauthentisiert (nur Loopback). Delegierte Tools (`get_tasks_delegated`, inzwischen entfernt) sind ohne Nutzer-Token nicht nutzbar.
− Der Token steht in der Kommandozeile des Browser-Aufrufs (lokale Sitzung) und im Inspector-Prozess.
− Nicht von der Konfiguration abgedeckt: Inspector-Auto-Connect gegen Azure mit OAuth.

## Umsetzung
`tools/mcp-inspector/{package.json,package-lock.json}`, `scripts/Start-McpInspector.ps1`, `scripts/Stop-LocalDebugServers.ps1`, `.vscode/launch.json` (Compound, `Debug ApiServer (Inspector, no prep)`, `Debug McpServer (Inspector, no prep)`), `.vscode/tasks.json` (`mcp-inspector`, `prepare-inspector-debug`),
`src/McpServer/Program.cs` (Guard + Loopback-Middleware), `.gitignore` (`tools/mcp-inspector/node_modules/`), `docs/DEBUGGING.md`, `README.md`.

## Änderungs-Leitfaden
Inspector-Version ändern: Pin in `tools/mcp-inspector/package.json`, im Ordner `npm install --ignore-scripts`, Lockfile committen, Marker/Parameter gegen die neue Version prüfen (Banner `MCP Inspector Web is up and running`, `autoConnect`, `MCP_INSPECTOR_API_TOKEN`). Anderer MCP-Port: `-McpUrl`/`-HealthUrls` des Skripts und `ASPNETCORE_URLS` anpassen. Niemals `McpAuth__RequireAuthentication=false` in Terraform, App Settings oder `appsettings.json` setzen.
