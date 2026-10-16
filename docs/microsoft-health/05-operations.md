# 05 – Betrieb

## 1. Tool-Aufrufe (Beispiele)

Mit dem MCP Inspector (Debug-Konfiguration „Debug API + MCP + Inspector“, siehe `docs/DEBUGGING.md`) oder per CLI:

```powershell
$cli = "tools/mcp-inspector/node_modules/@modelcontextprotocol/inspector/clients/launcher/build/index.js"
$env:MCP_CATALOG_PATH = "$env:TEMP\insp-catalog.json"
node $cli --cli http://localhost:5143/mcp --method tools/call --tool-name intune_service_health
node $cli --cli http://localhost:5143/mcp --method tools/call --tool-name intune_service_incidents --tool-arg activeOnly=true
node $cli --cli http://localhost:5143/mcp --method tools/call --tool-name microsoft_service_messages --tool-arg onlyIntune=false --tool-arg maxResults=20
node $cli --cli http://localhost:5143/mcp --method tools/call --tool-name azure_service_health --tool-arg subscriptionId=<guid> --tool-arg sinceUtc=2026-10-10T00:00:00Z
node $cli --cli http://localhost:5143/mcp --method tools/call --tool-name azure_resource_health --tool-arg "resourceId=/subscriptions/<guid>/resourceGroups/<rg>/providers/Microsoft.Web/sites/<name>"
node $cli --cli http://localhost:5143/mcp --method tools/call --tool-name microsoft_health_summary
```
(Hinweis Git Bash unter Windows: `MSYS_NO_PATHCONV=1` setzen, sonst wird `resourceId=/subscriptions/…` in einen Windows-Pfad umgeschrieben.)

Beispielanfragen an ein LLM: „Gibt es aktuell Intune-Störungen?“ → `intune_service_incidents(activeOnly=true)`; „Welche Intune-Ankündigungen mit Handlungsbedarf gibt es seit Oktober?“ → `microsoft_service_messages(sinceUtc=…)` und `actionRequired` auswerten; „Ist die Web-App X verfügbar laut Azure?“ → `azure_resource_health`.

## 2. Antworten richtig lesen

- `status=ok` heißt nur: Daten wurden abgerufen. `no_data` heißt: keine Daten – **nicht** „alles gut“ (bei `intune_service_health` zusätzlich Fehler `intune_service_not_found`).
- `isPartial`/`truncated`: Ergebnis unvollständig (`truncationReason` lesen; ggf. `maxResults`/`sinceUtc` anpassen).
- `microsoft_health_summary`: `sources[]` prüfen; `partial` = mindestens eine Quelle fehlgeschlagen, die übrigen sind gültig. `disabled`-Quellen zählen nicht als Fehler.

## 3. Diagnose HTTP 401 / 403 / 429 / 5xx

| Symptom (`errors[].code`) | Ursache | Maßnahme |
|---|---|---|
| `forbidden` (Graph) | Graph-Rolle fehlt/Zustimmung fehlt | Rollen der **Managed Identity der API** prüfen (`ServiceHealth.Read.All`, `ServiceMessage.Read.All`, [03-permissions.md](03-permissions.md) §6); nicht die App Registration prüfen |
| `forbidden` (`management.azure.com`) | RBAC-Rolle fehlt auf der Subscription | Variable `azure_health_subscription_ids` gesetzt und Rollenzuweisung vorhanden? Dauer bis zur Wirksamkeit 1–2 min. Lokal erwartet (keine Rolle). |
| `unauthorized` | Token abgelehnt (Audience/Identität) | Managed Identity aktiv (`GraphAuth__UseManagedIdentity=true`), App Settings prüfen |
| `token_acquisition_failed` | Identität/Konfiguration | Log-Eintrag mit Exception-Typ prüfen; lokal `AzureAd:ClientSecret` vorhanden? |
| `throttled` | HTTP 429 nach 3 Wiederholungen | später erneut; `maxResults`/Abfragehäufigkeit senken |
| `upstream_unavailable`/`network_error`/`timeout` | 502/503/504, Netz, Zeitlimit | automatisch wiederholt; bei Dauer `RequestTimeoutSeconds` prüfen |
| `subscription_not_allowed` | Subscription nicht in der Allowlist | `azure_health_subscription_ids` ergänzen (und RBAC) |
| `disabled` | Schalter/Allowlist | `MicrosoftHealth__Enabled/EnableIntune/EnableAzure`, `AllowedSubscriptionIds` |
| `invalid_response` | unerwartete Antwortstruktur | Log prüfen; API-Änderung möglich |
| `resource_not_found` | Resource ID falsch oder von Resource Health nicht unterstützt | ID prüfen |
| `intune_service_not_found` | Tenant liefert keinen Intune-Eintrag | kein Gesundheitsurteil möglich |

## 4. Konfigurationsprüfung

```powershell
az webapp config appsettings list -g rg-entramcp-dev -n entramcp-dev-api --query "[?starts_with(name,'MicrosoftHealth')].{n:name,v:value}" -o table
```
Beim Start validiert die API die Optionen; eine Fehlkonfiguration verhindert den Start (Log-Stream: `OptionsValidationException`, Meldung ohne Wertwiederholung).

## 5. Monitoring und Logs

- Warnungen je transientem Fehler und ein Fehlereintrag nach erschöpften Versuchen (`HealthRequestRunner`): Felder `Source`, `Host`, `Code`, `HTTP`, `Attempt`; **keine** Tokens, keine URLs mit Query.
- Log Stream: `az webapp log tail -g rg-entramcp-dev -n entramcp-dev-api` (Dateisystem-Logging ist nicht über Terraform aktiviert; ggf. temporär einschalten).
- Application Insights: Connection String ist gesetzt; ob das SDK Telemetrie sendet, ist nicht verifiziert (kein SDK in den csproj).

## 6. Funktionskontrolle nach Deployment

1. `GET https://entramcp-dev-api.azurewebsites.net/healthz` und `…-mcp…/healthz` → 200.
2. Tool-Discovery: 18 Tools, darunter die 7 neuen (Namen oben).
3. Mit einem authentifizierten Client `microsoft_health_summary` aufrufen: Intune-Quellen `ok`; Azure `disabled` solange keine Subscription freigegeben ist, danach `ok`/`no_data`.
4. Nach Aktivierung von Azure: `azure_subscription_resource_health` mit der freigegebenen Subscription → `aggregates.basedOnCompleteData` prüfen.

## 7. Verifikationsstand dieser Implementierung

| Prüfung | Ergebnis |
|---|---|
| `dotnet build EntraMcpMvp.sln` | 0 Fehler (4 bekannte NuGet-Warnungen NU1902/NU1903, vorhanden) |
| `dotnet test` | 119/119 bestanden (22 vorhanden, 97 neu) |
| Graph-Tools live (Dev-Tenant, App-only wie in Azure) | `intune_service_health` (ok), `intune_service_incidents` (aktiv: `no_data`; alle: Daten), `microsoft_service_messages` (Intune: 7 Meldungen; alle Dienste), `microsoft_health_summary` |
| Azure-Tools lokal | Eingabevalidierung, Allowlist, Host-/Traversal-Ablehnung bestätigt; ARM-Aufruf erreicht Azure und wird lokal erwartungsgemäß mit `forbidden` beantwortet (lokale App-Registrierung ohne RBAC) |
| Azure-Erfolgspfad | Antwortform (Events/Availability, `nextLink`, `occuredTime`) gegen echte ARM-Antworten **einer anderen Subscription** geprüft; nicht gegen die Dev-Subscription (dort fehlt bis zur Freigabe die Rolle) |
| Terraform | fmt/init/validate ok (Kopie ohne Backend); Plan nur über CI-Job des PR |
| Bestehende Tools | alle 11 weiterhin in der Tool-Discovery |
