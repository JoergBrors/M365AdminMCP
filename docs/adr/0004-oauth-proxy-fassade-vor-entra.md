# ADR-0004: OAuth-Proxy-Fassade vor Entra ID
- Status: Akzeptiert · Datum: 2026-10-16 (rückwirkend)

## Kontext
Der MCP-Auth-Standard (RFC 9728 Protected Resource Metadata, RFC 8414 AS-Metadata, RFC 8707 `resource`) erwartet, dass der MCP-Server
auf seiner eigenen Origin Discovery anbietet. Entra ID implementiert RFC 8707 nicht und lehnt abweichende `resource`-Werte mit
`AADSTS9010010` ab. Claude sendet `resource`, ChatGPT nicht. Strikte Clients verwerfen AS-Metadaten, deren `issuer` nicht der Origin entspricht.

## Entscheidung
Eine dünne Fassade im McpServer:
- synthetisiert RFC-8414-Metadaten (Endpoints zeigen auf sich selbst, `issuer` = eigene Origin),
- `/authorize` entfernt `resource` und leitet per 302 zu Entra,
- `/token` entfernt `resource`, reicht Form-Body und `Authorization`-Header durch und gibt Status/Body 1:1 zurück.
**Kein eigener Token-Aussteller.** PKCE läuft Ende-zu-Ende, es wird kein Secret oder State gehalten.

## Alternativen
- Direkt auf Entra zeigen: scheitert bei Claude (`resource`).
- Eigener vollständiger Authorization Server (DCR/CIMD): mehr Angriffsfläche und Betrieb, bewusst verworfen (siehe `docs/DEPLOYMENT.md`).

## Konsequenzen
+ Claude, ChatGPT und Copilot funktionieren gegen Entra ohne Entra-Änderung.
− Eigener Code im Auth-Pfad; bei Spezifikationsänderungen zu pflegen.
− `/token` loggt Entras Fehler-Bodies (enthalten keine Tokens; `code`/`code_verifier` werden nicht geloggt).
− `token_endpoint_auth_methods_supported` meldet `["none"]`, obwohl Copilot ein Secret sendet (wird im Body bzw. Basic-Header durchgereicht).

## Umsetzung
- `src/McpServer/Auth/OAuthProxy.cs`:
  `MapOAuthProxyEndpoints` (`/authorize`, `/token`, `/.well-known/oauth-authorization-server[/mcp]`), `HandleAuthorize`, `HandleToken`,
  `HandleAuthorizationServerMetadata` (Entras OIDC-Config 1 h im `IMemoryCache`), `IsRedirectUriAllowed`
  (nur `https`; optionale Host-Allowlist `McpAuth:AllowedRedirectHosts`, ohne Eintrag erlaubt – Entra validiert strikt).
- `src/McpServer/Program.cs`: `MapOAuthProtectedResourceMetadata` (`resource` = `{baseUrl}/mcp`, `authorization_servers` = eigene Origin),
  `JwtBearerEvents.OnChallenge` (401 mit `WWW-Authenticate: Bearer resource_metadata=…, scope=…`), CORS-Policy `McpDiscovery`
  (Copilot Studio führt die Discovery im Browser aus).
- `McpAuth:ExternalBaseUrl` bestimmt die öffentliche Origin (wichtig hinter Proxy oder Tunnel).

## Änderungs-Leitfaden
Weitere zu entfernende Parameter → `ResourceParamNames`. Neue Discovery-Felder → `HandleAuthorizationServerMetadata`.
Allowlist aktivieren → App Setting `McpAuth__AllowedRedirectHosts__0=…`. Nach jeder Änderung mit Claude **und** ChatGPT **und** Copilot testen,
sie verhalten sich unterschiedlich. Hinweis: `GetMcpScope`, `GetExternalBaseUrl` und `SetNoStoreHeaders` sind in `OAuthProxy.cs` und `Program.cs` dupliziert.
