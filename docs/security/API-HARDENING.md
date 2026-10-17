# API-Hardening (ASP.NET Core)

Status je Maßnahme: ✅ vorhanden und belegt, ⚠️ teilweise, ❌ fehlt, ❓ nicht geprüft.

| Maßnahme | Status | Fundstelle / Befund |
|---|---|---|
| JWT-Validierung (Signatur, Issuer, Audience) | ✅ | `ApiServer/Program.cs:22-36`, `McpServer/Program.cs:44-57` (`ValidAudiences` = App-ID und Identifier-URI) |
| Autorisierung | ⚠️ | eine Policy `TasksReadWrite` für alle Controller (`ApiServer/Auth/PolicyNames.cs`, F-005) |
| Eingabevalidierung Reports | ✅ | `ApiServer/Services/ReportParameterValidator.cs`: `date` = `yyyy-MM-dd`, `appId` = GUID, `serviceArea` = `[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}`; `period` gegen Whitelist (`ValidatePeriod`); unbekannte Reports abgewiesen; Tests `ReportParameterValidatorTests`, `GraphApiClientReportTests` |
| Eingabevalidierung Health | ✅ | `McpServer/Utils/HealthInputValidator.cs` (per Compile-Link im API), Tests `HealthInputValidatorTests` |
| SSRF-Schutz | ✅ | feste Hosts (`graph.microsoft.com`, `management.azure.com`), `nextLink` nur https auf diese Hosts, HTTP-Clients ohne Auto-Redirect für Health (`ApiServer/Program.cs:58-67`) |
| Download-URLs vorautorisiert | ✅ | CSV-Download ohne Graph-Token, URL nicht geloggt (ADR-0010) |
| HTTPS-Erzwingung | ⚠️ | `UseHttpsRedirection` nur im API und nur außerhalb Development (`Program.cs:155-157`); App Services setzen `https_only = true` (Terraform) |
| HSTS | ✅ | `UseRequestHardening` → `UseHsts()` außerhalb Development (`McpServer/Hardening/RequestHardening.cs`); live nicht geprüft |
| Forwarded Headers / Host-Validierung | ❓ | `UseForwardedHeaders` nicht gefunden; Metadaten nutzen `McpAuth:ExternalBaseUrl` statt Request-Host |
| Rate Limiting | ✅ | .NET: `AddRequestHardening`/`UseRequestHardening` (global, je Nutzer/IP, strenger für `/authorize` `/token` `/register`, `/healthz` ausgenommen), vor `UseAuthorization`, damit auch 401-Anfragen zählen; Gateway: `gateway/limits.py`. Konfig: `Hardening:*` bzw. `GATEWAY_LIMIT_*`. Tests: `RequestHardeningTests` (10), `test_limits.py` (7) |
| Request-Größenlimit | ✅ | Kestrel `MaxRequestBodySize` 1 MiB (`Hardening:MaxRequestBodyBytes`), Gateway 64 KiB (`/mcp`: 1 MiB) |
| Response-Größenlimit | ❌ | nicht umgesetzt; Reports unbegrenzt im Speicher (ADR-0010) |
| CORS | ⚠️ | McpServer `AllowAnyOrigin` (`Program.cs:28-34`, F-007) |
| Swagger/OpenAPI | ✅ | nur in Development (`ApiServer/Program.cs:112-131`) |
| Fehlerbehandlung / ProblemDetails | ❓ | keine eigene Middleware gefunden (`UseExceptionHandler`/`AddProblemDetails` ohne Treffer); Verhalten in Production nicht getestet |
| Health-Endpunkte | ✅ | Gateway/McpServer `/healthz` ohne Daten |
| API-Key | ⚠️ | `FixedTimeEquals`; in Azure nicht gesetzt (F-003) |
| NuGet-Schwachstellen | ✅ | `System.Security.Cryptography.Pkcs` auf 10.0.13; CI-Schritt `dotnet list package --vulnerable` (F-002) |

## Nächste Schritte (nach Priorität)
1. ~~P1: Rate Limiter, Request-Limits, HSTS~~ umgesetzt (Live-Retest und Last-Verhalten mit echten Clients offen).
2. P1: getrennte Policies je Datenbereich (Migration siehe [SECURITY-DECISIONS.md](SECURITY-DECISIONS.md)).
3. P2: Einheitliche `ProblemDetails`-Fehlerantworten ohne Interna; Test dazu.
4. P2: Paging/Antwortgröße für Reports.
