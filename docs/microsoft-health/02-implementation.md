# 02 – Implementierung

## 1. Überblick und Datenfluss

```
LLM ── MCP (/mcp) ──► McpServer: Tools/MicrosoftHealthTools.cs     (Validierung der Eingaben, nur lesend)
                          │  GET /api/microsoft-health/…   (App-only-Token, ApiServerClient)
                          ▼
                     ApiServer: Controllers/MicrosoftHealthController.cs   (verbindliche Validierung → 400)
                          ├─► Health/MicrosoftServiceHealthClient ──► https://graph.microsoft.com/v1.0/admin/serviceAnnouncement/…
                          ├─► Health/AzureResourceHealthClient    ──► https://management.azure.com/…/Microsoft.ResourceHealth/… (api-version 2025-05-01)
                          └─► Health/MicrosoftHealthSummaryService (aggregiert, isoliert Teilfehler)
                               beide Clients nutzen Health/HealthRequestRunner (Token, Timeout, Retry, Pagination, Host-Allowlist)
```

Kein zweiter MCP-Server, Transport und bestehende Tools unverändert (11 → 18 Tools).

## 2. Neue Dateien

| Datei | Zweck |
|---|---|
| `src/McpServer/Tools/MicrosoftHealthTools.cs` | 7 MCP-Tools (`[McpServerToolType]`), Eingabevalidierung, Aufruf des ApiServers; registriert in `src/McpServer/Program.cs` (`.WithTools<MicrosoftHealthTools>()`) |
| `src/McpServer/Utils/HealthInputValidator.cs` | **eine** Validierungs-Implementierung (Subscription-GUID, Resource-ID, `sinceUtc`, `maxResults`); vom ApiServer per Compile-Link genutzt (Muster aus ADR-0009) |
| `src/ApiServer/Controllers/MicrosoftHealthController.cs` | Routen `api/microsoft-health/*`, Policy `TasksReadWrite` (wie alle Controller) |
| `src/ApiServer/Health/MicrosoftServiceHealthClient.cs` | Graph: `healthOverviews`, `issues`, `messages` |
| `src/ApiServer/Health/AzureResourceHealthClient.cs` | ARM: Events, Availability (Ressource/Subscription) |
| `src/ApiServer/Health/HealthRequestRunner.cs` | gemeinsame HTTP-Ausführung (siehe 4.) |
| `src/ApiServer/Health/MicrosoftHealthSummaryService.cs` | Aggregation `microsoft_health_summary` |
| `src/ApiServer/Health/MicrosoftHealthOptions.cs` | Optionen + Startvalidierung (`IValidateOptions`) |
| `src/ApiServer/Health/Models/HealthModels.cs` | einheitliche Antwort-DTOs |
| `src/ApiServer/Auth/ArmTokenService.cs` | Token für `https://management.azure.com/.default`, **gleiche Identitätskonfiguration** wie `GraphTokenService` |
| `tests/ApiServer.Tests/Health/*` | 97 neue Tests |

Geänderte Dateien (Auszug): `src/ApiServer/Program.cs` (DI, benannter HttpClient `MicrosoftHealth` **ohne Redirects**, Options mit `ValidateOnStart`), `src/ApiServer/ApiServer.csproj` (Compile-Link Validator), `.github/workflows/deploy.yml` (zusätzlich `dotnet test`), Terraform siehe [04-terraform.md](04-terraform.md).

## 3. MCP-Tools

Alle Tools sind schreibgeschützt (nur HTTP GET). Namen sind explizit gesetzt (`[McpServerTool(Name = "…")]`).

| Tool | Parameter | Upstream-API | Besonderheiten |
|---|---|---|---|
| `intune_service_health` | – | Graph `GET /v1.0/admin/serviceAnnouncement/healthOverviews` | Intune-Erkennung exakt: `id == "Intune"` oder `service == "Microsoft Intune"` (kein Substring). Kein Treffer → `no_data` + Fehler `intune_service_not_found`; **nie** „gesund“ behauptet. |
| `intune_service_incidents` | `activeOnly` (false), `maxResults` (1–1000, Std. 100) | Graph `GET …/issues` | nur `service == "Microsoft Intune"`; aktiv = `isResolved == false`; Sortierung nach `lastModifiedDateTime` absteigend; vollständige Pagination |
| `microsoft_service_messages` | `onlyIntune` (true), `sinceUtc`, `serviceName`, `maxResults` | Graph `GET …/messages` | `services` enthält exakt „Microsoft Intune“; `sinceUtc` gegen `lastModifiedDateTime`; `actionRequired` nur wenn die API `actionRequiredByDateTime` liefert; `bodyPreview` = Text ohne HTML (max. 500 Zeichen) |
| `azure_service_health` | `subscriptionId`, `sinceUtc`, `maxResults` | ARM `GET /subscriptions/{id}/providers/Microsoft.ResourceHealth/events?api-version=2025-05-01` | `sinceUtc` → `queryStartTime` im dokumentierten Format `M/d/yyyy` (Tagesgranularität; API-Standard: 3 Tage); Allowlist-Pflicht |
| `azure_resource_health` | `resourceId` | ARM `GET {resourceId}/providers/Microsoft.ResourceHealth/availabilityStatuses/current?api-version=2025-05-01` | Resource-ID wird strikt validiert; Host ist fest; 404 → `no_data`/`resource_not_found`; fehlender Status → `Unknown` |
| `azure_subscription_resource_health` | `subscriptionId`, `maxResults` | ARM `GET /subscriptions/{id}/providers/Microsoft.ResourceHealth/availabilityStatuses?api-version=2025-05-01` | `aggregates` (Available/Unavailable/Degraded/Unknown/Other) nur aus gelieferten Daten; `basedOnCompleteData=false` bei Abschneiden |
| `microsoft_health_summary` | – | alle obigen | Teilfehler isoliert; `sources[]` mit Status und Abfragezeit; Incident, Announcement und Availability getrennt |

Beispielaufrufe: [05-operations.md](05-operations.md).

## 4. Gemeinsame technische Anforderungen (umgesetzt in `HealthRequestRunner`)

- **Authentifizierung:** Graph-Token (`https://graph.microsoft.com/.default`) über den vorhandenen `GraphTokenService`; ARM-Token (`https://management.azure.com/.default`) über `ArmTokenService` – dieselbe Managed Identity. Token werden nie geloggt oder in Fehlertexte übernommen (Token-Fehler melden nur den Exception-Typ).
- **HTTP:** `IHttpClientFactory` (Name `MicrosoftHealth`), `AllowAutoRedirect=false`, `CancellationToken` durchgereicht, Timeout je Request (`RequestTimeoutSeconds`, Std. 30 s).
- **Retry:** höchstens 4 Versuche (3 Wiederholungen): 429 (mit `Retry-After`, gedeckelt auf 30 s), 502/503/504, Netzwerkfehler, Timeouts; sonst Exponential Backoff (500 ms, 1,5 s, 3,5 s) + 20 % Jitter. **Nicht** wiederholt: 400/401/403/404, ungültige Antworten, Abbruch durch den Aufrufer.
- **Pagination:** begrenzt durch `MaxPages` (20) und `maxResults`; `@odata.nextLink` (Graph) bzw. `nextLink` (ARM) nur bei `https`, Standardport, ohne UserInfo und Host exakt `graph.microsoft.com` bzw. `management.azure.com`; sonst wird abgeschnitten und `truncated`/`truncationReason` gesetzt (das Token wird nie an fremde Hosts gesendet).
- **Fehlercodes** (stabil): `unauthorized`, `forbidden`, `not_found`, `bad_request`, `throttled`, `upstream_unavailable`, `upstream_error`, `network_error`, `timeout`, `invalid_response`, `token_acquisition_failed`, `subscription_not_allowed`, `invalid_subscription_id`, `invalid_resource_id`, `intune_service_not_found`, `resource_not_found`, `disabled`.

## 5. Antwortschema (alle Tools außer `microsoft_health_summary`)

```json
{
  "source": "graph.issues",
  "queriedAtUtc": "2026-10-16T16:48:10Z",
  "tenantId": null,
  "subscriptionId": null,
  "status": "ok",                  // ok | no_data | error | disabled
  "items": [ … ],
  "itemCount": 5,
  "errors": [ { "code": "forbidden", "message": "…", "httpStatus": 403 } ],
  "isPartial": true,               // Daten unvollständig (z. B. abgeschnitten)
  "truncated": true,
  "truncationReason": "maxResults (5) erreicht; weitere Eintraege vorhanden.",
  "aggregates": null               // nur azure_subscription_resource_health
}
```
`status` beschreibt den **Datenabruf**, nicht die Gesundheit eines Dienstes (die steht in `items[].status` bzw. `availabilityState`). `microsoft_health_summary` liefert zusätzlich `sources[]` und den Gesamt-Abrufstatus `ok | partial | error | disabled`; es wird keine globale Gesundheitsbewertung berechnet.

Fehlerhafte Eingaben liefert das **Tool** als `{"status":"invalid_request","errors":[…]}`; der ApiServer prüft verbindlich erneut (HTTP 400).

## 6. Konfiguration (Abschnitt `MicrosoftHealth`, in Azure `MicrosoftHealth__…`)

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `Enabled` | `true` | Hauptschalter (aus = `disabled`) |
| `TenantId` | – | optional; muss `AzureAd:TenantId` entsprechen, sonst **Startfehler** |
| `EnableIntune` | `true` | Graph-Tools |
| `EnableAzure` | `true` | Azure-Tools (zusätzlich nur mit Allowlist) |
| `AllowedSubscriptionIds` | `[]` | Allowlist (GUIDs); leer = Azure-Tools `disabled` |
| `RequestTimeoutSeconds` | `30` | 1–300 |
| `DefaultMaxResults` / `MaxResults` | `100` / `1000` | Standard und harte Obergrenze |
| `MaxPages` | `20` | 1–100 |

Startvalidierung (`MicrosoftHealthOptionsValidator`, `ValidateOnStart`): ungültige GUIDs, Bereichsverletzungen oder abweichende Tenant-ID verhindern den Start mit einer Meldung, die den Wert nicht wiederholt. Es gibt keine neuen Secrets.

## 7. Bekannte Einschränkungen

- Azure-Events: `queryStartTime` hat Tagesgranularität; Events mit `isEventSensitive` enthalten laut Microsoft nur eingeschränkte Details (`fetchEventDetails` wird bewusst nicht aufgerufen).
- Graph-Filterung/Sortierung erfolgt clientseitig (nur dokumentierte Abfragen); bei sehr vielen Meldungen begrenzt `MaxPages` die Abdeckung (`truncated`).
- Erfolgspfad der Azure-Antworten wurde gegen die echte ARM-Antwortform eines anderen Tenants geprüft, nicht gegen die Dev-Subscription (siehe [05-operations.md](05-operations.md)).
- Auth-Design unverändert (ADR-0006): alle authentifizierten MCP-Nutzer sehen dieselben tenantweiten Health-Daten.
