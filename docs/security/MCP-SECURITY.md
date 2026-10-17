# MCP-spezifische Sicherheit

| Thema | Befund |
|---|---|
| Tool-Oberfläche | 18 Tools (Live-`tools/list` über das Gateway): Task-Demo, Service-Health, Message Center, Adoption-Reports, Intune/Azure/Microsoft-Health. Alle lesend. |
| Transport | Streamable HTTP, stateless (`/mcp`), kein SSE (ADR-0003). Nicht unterstützte Methoden: `PUT`/`PATCH` → 404/405 (Test); `DELETE` → 400 (live); `GET`-Stream nicht geprüft |
| Protokollstand | .NET-Server und Gateway-Backend-Verbindung sprechen den Handshake-Stand (Protokoll `2025-11-25`). Die Backend-Verbindung ist **auf `legacy` festgelegt**, weil der .NET-Server moderne Per-Request-Metadaten ablehnt (Live-Fehler und Fix, Test `test_backend_client_is_pinned_to_handshake_era`). Neuere Spezifikationen (`2026-07-28`) sind **nicht** nachgewiesen unterstützt |
| Token im Tool-Parameter | `GetTasksDelegated` erwartet ein Zugriffstoken als LLM-Parameter (F-006). Das ist ein Risiko: Tokens gehören nie in den LLM-Kontext. Alternative: OBO ausschließlich aus dem Request-Token im Serverkontext (benötigt ein Secret statt Managed Identity, siehe ADR-0006) oder das Tool entfernen |
| Prompt Injection | Tool-Antworten enthalten Daten aus Microsoft 365 (Texte von Message-Center-/Service-Health-Meldungen). Sie könnten Anweisungen enthalten. Maßnahmen heute: Tools sind lesend, keine Schreibaktionen, keine Weitergabe von Tokens. Nicht getestet |
| Autorisierung je Tool | keine; Zugriff = `Mcp.Access` (F-004) |
| Fehlerweitergabe | Backend-Fehler gelangen als MCP-Tool-Fehler zum Client; Debug-Details nur mit `GATEWAY_DEBUG_ERRORS=true` (Standard aus, Test: Token nicht enthalten) |
| Mehrere MCP-Server | Pro MCP eigene App-ID-URI/Audience und Scope; siehe [EXTENDING-MCP-PLATFORM.md](../EXTENDING-MCP-PLATFORM.md) |
