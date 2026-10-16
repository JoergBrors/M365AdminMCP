# ADR-0003: Streamable HTTP, stateless, nur `/mcp`
- Status: Akzeptiert · Datum: 2026-10-16 (rückwirkend)

## Kontext
ChatGPT, Claude und Copilot Studio sprechen Streamable HTTP. `ModelContextProtocol.AspNetCore` v2 (Paket 2.2.0) erlaubt
`EnableLegacySse` nicht zusammen mit `Stateless` (Default `true`) – es wirft beim Start eine `InvalidOperationException`.

## Entscheidung
Genau ein MCP-Endpunkt `/mcp`, Streamable HTTP, **stateless** (keine `Mcp-Session-Id`). Kein `/sse`, kein `/message`.

## Konsequenzen
+ Keine Session-Affinität nötig → mehrere App-Service-Instanzen möglich, übersteht Neustarts und Cold-Starts.
− Keine Features, die Session-Zustand brauchen (server-initiierte Requests, Subscriptions).
− Reine SSE-Clients werden nicht unterstützt.

## Umsetzung
- `src/McpServer/Program.cs`: `AddMcpServer().WithHttpTransport()` + `.WithTools<T>()`-Kette; `app.MapMcp("/mcp")` (Variable `mcpEndpoint`).
- Die Begründung steht als Kommentar direkt über der Registrierung.

## Änderungs-Leitfaden
Session-Zustand nötig? Erst `Stateless=false` samt Session-Affinität bewerten – das verändert die Skalierung (ADR-0013).
`EnableLegacySse` nie zusammen mit Stateless aktivieren.
