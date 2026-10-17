# ADR-0014: Zweiter Auth-Weg – `X-MCP-API-Key`
- Status: Akzeptiert (mit Risiko) · Datum: 2026-10-16 (rückwirkend)

## Kontext
Einfache Test-Clients (curl, Inspector, Skripte) sollen den MCP-Endpunkt ohne vollen OAuth-Flow nutzen können.

## Entscheidung
Neben JWT-Bearer akzeptiert der McpServer den Header `X-MCP-API-Key`. Ist `McpAuth:ApiKey` gesetzt und stimmt der Header, wird ein Principal mit Rolle `Mcp.Access` erzeugt. Ohne konfigurierten Key (Standard) ist der Weg deaktiviert. Die Autorisierungs-Policy `McpAccess` akzeptiert Scope `Mcp.Access` (`scp`) **oder** Rolle `Mcp.Access`. OAuth bleibt der bevorzugte Weg.

## Konsequenzen
+ Einfaches Testen gegen das Dev-System.
− Statisches, geteiltes Secret ohne Ablauf, ohne Nutzerbezug, ohne Rotation; ein gültiger Key = voller Zugriff auf alle Tools.
− Der Vergleich nutzt `string.Equals(..., Ordinal)` (nicht zeitkonstant).
− Der Key darf nie in Logs/Doku landen; in Azure nur als Key-Vault-Referenz setzen.

## Umsetzung
`src/McpServer/Program.cs`: Middleware nach `UseAuthentication()` (Header-Prüfung, Claims `NameIdentifier`/`Role`), Policy `McpAccess` in `AddAuthorization`; `src/McpServer/Auth/McpAuthOptions.cs` (`ApiKey`); `appsettings*.json` (`McpAuth:ApiKey`).

## Änderungs-Leitfaden
Härtung: `CryptographicOperations.FixedTimeEquals` verwenden, Key nur für Development erlauben oder per Key Vault + Rotation, pro Client eigene Keys mit Namen im Claim (Audit). Abschaffen: Middleware entfernen, `McpAuth__ApiKey` aus den App Settings streichen.

## Aktualisierung 2026-10-17
Der Vergleich ist jetzt zeitkonstant (`CryptographicOperations.FixedTimeEquals`; Längenunterschied bleibt erkennbar). In Azure ist **kein** `McpAuth__ApiKey` gesetzt, der Weg ist dort deaktiviert. Eine Verbindung über den Key trägt im Audit `authMethod=apikey` ohne Nutzer-ID (UserContext, ADR-0006). Empfehlung weiterhin: abschaffen (Entscheidung D-4 offen), siehe [STATUS.md](../STATUS.md).
