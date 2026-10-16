# ADR-0015: Lokales Debugging ohne Auth mit MCP Inspector
- Status: Akzeptiert · Datum: 2026-10-16

## Kontext
Tools sollen lokal im VS-Code-Debugger mit dem [MCP Inspector](https://github.com/modelcontextprotocol/inspector) getestet werden. Der volle OAuth-Flow gegen Entra ist dafür unpraktisch (Inspector-Redirect-URI ist nicht registriert; öffentliche Callback-Erreichbarkeit fehlt).
Das Deployment (Auth an, `Production`) darf dadurch nicht beeinflusst werden.

## Entscheidung
- Neue Launch-Konfiguration „Debug API + MCP + Inspector“ setzt `McpAuth__RequireAuthentication=false` **nur** für den lokalen McpServer-Prozess und startet per Task `mcp-inspector` (`npx -y @modelcontextprotocol/inspector`).
- Sicherheitsnetz im Code: Ist `RequireAuthentication=false` und die Umgebung nicht `Development`, bricht der Start mit `InvalidOperationException` ab. Azure setzt `ASPNETCORE_ENVIRONMENT=Production` und `McpAuth__RequireAuthentication=true`.
- Deployment/Feature-Isolation: Arbeit auf Feature-/Docs-Branches; nur der Merge nach `main` deployt (ADR-0012).

## Konsequenzen
+ Breakpoints in Tools; schneller Feedbackzyklus ohne Tunnel/OAuth.
− Ohne Nutzer-Token funktionieren delegierte Tools (`GetTasksDelegated`) lokal nicht; App-only-Tools laufen mit der lokalen Konfiguration (`appsettings.Development.json`).
− Lokal ist `/mcp` unauthentisiert; Port 5143 nicht ins Netz exponieren (Tunnel `mydevice-mcp.brors.date` nur bewusst nutzen).
− Benötigt Node.js (`npx`).

## Umsetzung
`.vscode/launch.json` (Compound „Debug API + MCP + Inspector“, Konfiguration „Debug McpServer (Inspector, no prep)“), `.vscode/tasks.json` (`mcp-inspector`, `prepare-inspector-debug`),
`src/McpServer/Program.cs` (Guard direkt nach dem Lesen von `McpAuth:RequireAuthentication`), `scripts/Stop-LocalDebugServers.ps1` (Ports 6274/6277), `README.md` (Abschnitt „Lokal testen mit dem MCP Inspector“).

## Änderungs-Leitfaden
Inspector mit Auth testen: `RequireAuthentication` auf `true` lassen und im Inspector den Header `X-MCP-API-Key` setzen (lokal `McpAuth__ApiKey` setzen, ADR-0014). Anderen Port/Version: Task `mcp-inspector` anpassen.
