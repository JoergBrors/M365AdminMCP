# Q-API-2 – Antwortgrößen, Paging und verlustfreier Issue-Abruf

Arbeitspaket, umgesetzt in PR #36 (Merge-Commit `897a14a`, **in Dev deployt**; GitHub-Actions-Lauf `39113383973`). Fasst Q-API-2 (Report-Paging/Antwortgrößen) und den Fehler
`SSE stream failed: Server-sent event exceeded the 1048576 byte limit` (Tool `get_service_health_issues`) zusammen.
**Status: PARTIALLY_IMPLEMENTED / offen** (siehe „Weiter offen“ und „Offene Befunde“).

## Ursache (nachgewiesen)

- `GET /admin/serviceAnnouncement/issues` liefert pro Issue die vollständige `posts`-Historie (HTML). Die Rohantwort wurde bis dahin unverändert, ohne Paging und ohne Größenlimit durchgereicht.
- Das 1-MiB-Limit setzt der **Python-MCP-Client** durch (`mcp` 2.3.0 → `httpx2` 2.13.1, `DEFAULT_MAX_SSE_EVENT_SIZE = 1024*1024`), den auch das FastMCP-Gateway für das Backend nutzt (`StreamableHttpTransport`, `ProxyClient`). Weder der .NET-Server (`ModelContextProtocol.AspNetCore` 2.2.0) noch der Inspector 2.11.0 setzen diese Grenze.
- Negativkontrolle (reproduzierbar, `tests/gateway_e2e`): Mit abgeschaltetem Wächter liefert derselbe Client-Aufbau exakt den Originalfehler; mit Wächter läuft der Aufruf durch.
- Dass der Fehler (und kein Graph-403) auftrat, zeigt: Der ApiServer konnte Graph lesen (App-only über die Managed Identity, `ServiceHealth.Read.All`).

## Lösung

| Baustein | Datei |
|---|---|
| Größenwächter (Status, Messages, Reports): markierte Teilantwort statt Fehler | `src/ApiServer/Services/ResponseSizeGuard.cs`, Einbau in `M365StatusController`, `M365MessagesController`, `M365ReportsController` |
| Suche, Einzelabruf, Content in Teilen | `src/ApiServer/Health/ServiceHealthIssueService.cs`, Endpunkte `issues/search`, `issues/{id}`, `issues/content` |
| Signierte, kurzlebige Fortsetzungs-Tokens | `src/ApiServer/Services/ContinuationTokenService.cs` |
| Eingabevalidierung (kein OData-Passthrough) | `src/McpServer/Utils/IssueInputValidator.cs` (Compile-Link, ADR-0009) |
| MCP-Tools `search_service_health_issues`, `get_service_health_issue`, `get_service_health_issue_content` | `src/McpServer/Tools/ServiceHealthIssueTools.cs` |
| Schema ohne Null-Typ-Arrays (nur diese Tools) | `src/McpServer/Tools/SchemaCompatibleTools.cs`, Katalog `McpToolCatalog.cs` |
| Signaturschlüssel (Terraform, Key Vault) | `terraform/modules/infra/main.tf` (`random_password.continuation_signing_key`, `azurerm_key_vault_secret.continuation_signing_key`) |

Bestehende Tool-Namen und -Parameter sind unverändert. Das Alt-Tool `get_service_health_issues` liefert bei zu großen Antworten statt des Fehlers eine
markierte Teilantwort (`truncated`, `isPartial`, `returnedItems`/`totalItems`, `moreGraphPages`, `omittedIds`/`omittedIdsTruncated`, `hint`); die Inhalte bleiben über die drei neuen Tools erreichbar.

### Konfiguration (`ResponseLimits`, App Settings `ResponseLimits__…`)

| Schlüssel | Default | Bedeutung |
|---|---|---|
| `MaxResponseBytes` | 700000 | Obergrenze einer Tool-Antwort, gemessen als JSON-escaptes UTF-8 (konservativ; Nicht-ASCII zählt als `\uXXXX`). Startvalidierung: 10000–900000 |
| `MaxChunkBytes` | 400000 | Obergrenze eines Content-Chunks, muss unter `MaxResponseBytes` liegen |
| `ContinuationTokenMinutes` | 15 | Gültigkeit eines Fortsetzungs-Tokens |
| `ContinuationSigningKey` | – | **Pflicht außerhalb von Development**, mind. 32 Zeichen (Azure: Key-Vault-Referenz) |

## Signaturschlüssel: Verwaltung und Rotation

- Terraform erzeugt einen **dedizierten** 64-Zeichen-Schlüssel (`random_password`), legt ihn als Secret `api-continuation-signing-key` im Key Vault ab und setzt `ResponseLimits__ContinuationSigningKey` am ApiServer als Key-Vault-Referenz (`versionless_id`). Es wird kein bestehendes OAuth-Secret verwendet; es gibt keinen Fallback.
- Der ApiServer **startet nicht** (`OptionsValidationException`, ohne den Wert zu nennen), wenn der Schlüssel fehlt, kürzer als 32 Zeichen ist, wie ein Platzhalter (`__…`) aussieht oder eine **unaufgelöste** Key-Vault-Referenz (`@Microsoft.KeyVault…`) ist (z. B. fehlende Rolle *Key Vault Secrets User*). Nachweis: manueller Start in Production und `ServiceHealthIssueServiceTests`.
- Development: ohne Schlüssel erzeugt der Prozess einen zufälligen Schlüssel (nur lokal; Tokens gelten bis zum Neustart). Tests setzen einen festen Testschlüssel.
- Der Wert erscheint nicht in Logs, Fehlermeldungen, Terraform-Outputs oder der Doku; im Plan und im State ist er `sensitive` (State: Azure Storage, wie die übrigen Secrets).
- **Rotation:** `terraform apply -replace="module.infra.random_password.continuation_signing_key"` (über die Skripte, mit Rückfrage bei Löschungen). Folge: Alle bis dahin ausgegebenen `continuationToken` werden ungültig (`invalid_continuation_token`); Clients wiederholen die Suche bzw. beginnen den Content-Abruf ohne Token. Die App übernimmt die neue Version über die versionslose Referenz spätestens nach Neustart (Key-Vault-Referenzen werden periodisch aktualisiert). Tokens sind ohnehin nur 15 Minuten gültig. Bei App-Neustarts mit unverändertem Schlüssel bleiben Tokens gültig (getestet).

## Teststand

**Live verifiziert (Dev, 2026-10-18)**
- Deployment: Lauf `39113383973` – `build`, `gateway-ci`, `deploy-dev` erfolgreich; Terraform Apply `2 added, 1 changed, 0 destroyed` (keine Löschung, keine Rollenänderung). Der ApiServer ist mit dem dedizierten Signaturschlüssel aus dem Key Vault gestartet (die Startvalidierung hätte sonst abgebrochen).
- Legacy-Tool `get_service_health_issues` funktioniert mit aktivem Größenwächter. Der ursprüngliche Fehler `SSE stream failed: Server-sent event exceeded the 1048576 byte limit` tritt bei diesem zuvor reproduzierten Aufruf nicht mehr auf. Gemessen: ungekürzte Antwort **1 194 971 Bytes**; Ergebnis 55 von 100 Einträgen, `truncated=true`, `isPartial=true`, `limitBytes=700000`, 50 `availableIds` (so hieß das Feld im damaligen Stand; Semantik und Korrektur siehe „Befunde F-1/F-2“).
- Fix F-1/F-2 (PR #38, `eaf7c35`, Lauf `39120403403`, Apply `0 added, 0 changed, 0 destroyed`): derselbe Aufruf liefert jetzt 55 von 100 Einträgen mit `moreGraphPages=true`, `omittedIds` (45 IDs, keine davon in `value`, `omittedIdsTruncated=false`), erweitertem `truncationReason` und dem Hinweis auf die Suche; größtes gemessenes SSE-Event **654 226 Bytes**.
- Neue Tools am echten Dev-Tenant, direkt gegen den deployten MCP-Server (Entwickler-Token per `az`, **nicht** über das Gateway): `tools/list` enthält 20 Tools inkl. der drei neuen. `search_service_health_issues`: 302 Issues (289 gelöst, 13 offen – alle `advisory`; 217 Advisories + 85 Incidents) über 4 Aufrufe mit `continuationToken`, `scanComplete=true`; Graph-Filter `service` und `isResolved` (`filterMode=graph`) liefern passende Ergebnisse, die lokalen Filter `classification`, `searchText`, `startFrom` laufen. `get_service_health_issue` (Issue mit 56 Posts, `contentRefs`, `contentVersion`) und `get_service_health_issue_content` (`impact`, `details`, `post:0`): Offsets lückenlos, `contentVersion` stabil, Länge = `totalChars`. Ein manipuliertes `continuationToken` liefert `invalid_continuation_token` ohne Inhalt. Größtes SSE-Event über alle Aufrufe 654 226 Bytes.

**Implementiert und automatisiert getestet**
- `ApiServer.Tests` (237): Größenwächter (Unicode/Escaping, Einzel-Item, Nicht-Collections), Token (Manipulation, Ablauf, Bindung, Schlüsselwechsel, Neustart), Validatoren inkl. Injektion, Suche (0/1/25/100/130 Items, Mehrseiten, Filter, Rückfall bei Graph-400, manipulierte `nextLink`, `MaxPages`, 403/404/429/503, Cancellation), Einzelabruf, Content (verlustfrei, Unicode/Emoji, Änderungserkennung, fremde Tokens/IDs), Startvalidierung.
- `McpServer.Tests` (19): veröffentlichte `tools/list`-Schemas (keine Null-Typ-Arrays in den neuen Tools, Namen/`required` der Alt-Tools unverändert) und E2E über echtes MCP (HTTP/SSE) mit echten ApiServer-Controllern und simuliertem Graph: größtes gemessenes SSE-Event **700 091 Bytes** (Client-Limit 1 048 576), Größenmatrix um das Limit (300 KB–1,5 MB, ASCII und Unicode), einzelnes 3-MB-Item, 1,8 Mio. Zeichen Content in 9 Chunks byte-identisch, Token über „Neustart“, Änderung, Rotation.
- Gateway-Strecke (`tests/gateway_e2e/gateway_leg_e2e.py`, Client-Aufbau wie `make_backend_client`): bestanden, inkl. Negativkontrolle. Bestehende Gateway-Tests: 59 bestanden.
- Inspector 2.11.0 (CLI): `tools/list` und `tools/call` gegen den E2E-Host ohne Meldung.

**Implementiert, noch nicht live verifiziert**
- Mehrteiliger Content-Abruf am echten Tenant: der größte gefundene Post (unter den ersten 40 Issues) hat nur 2 490 Zeichen, sodass jeder Live-Abruf in **einem** Chunk endete; mehrere Chunks, `content_changed` und Token-Gültigkeit über einen API-Neustart sind nur automatisiert getestet.
- Inhaltliche Gegenprüfung der lokalen Filter `searchText` und `startFrom` gegen eine unabhängige Quelle (live nur auf Plausibilität/Summen geprüft).
- Live-End-to-End-Prüfung der neuen Tools über das FastMCP-Gateway (der Live-Test lief direkt gegen den MCP-Server). Der Eigentümer meldete zuvor einen erfolgreichen ChatGPT-Test; Umfang und Protokoll sind nicht dokumentiert und werden daher **nicht** als Nachweis gewertet.
- Warnungen der Inspector-**Web-Oberfläche** (CLI zeigt keine; die UI wurde nicht bedient).
- Hinweis: `classification`, `status`, Datum und `searchText` laufen **lokal** im ApiServer (kein Graph-Filter); `service`/`isResolved` werden als Graph-`$filter` gesendet (live bestätigt).

**Weiter offen**
- Vollständiges Paging für Message-Center-Nachrichten und Reports (nur Größenwächter mit markierter Teilantwort; kein Folgeabruf).
- Legacy-Schema-Warnungen: sieben bestehende Tools veröffentlichen weiterhin Null-Typ-Arrays (`azure_service_health`, `azure_subscription_resource_health`, `get_adoption_report`, `get_message_center_messages`, `intune_service_incidents`, `list_adoption_reports`, `microsoft_service_messages`); bewusst unverändert, per Test dokumentiert. Angleichen wäre eine sichtbare Schemaänderung bestehender Tools.
- Gateway-Limit unverändert: FastMCP 4.1.0 reicht `max_sse_event_size` nicht durch; die Begrenzung liegt serverseitig.

**Bekannte Einschränkungen**
- Listen-Pagination ist offsetbasiert (`$skip` von Graph) und nur „best effort“ (`listConsistency`); Einträge können sich zwischen Seiten verschieben. Content ist über `contentVersion` konsistent (Änderung wird erkannt, nicht verhindert; kein Snapshot-Speicher).
- `searchText` und die lokalen Filter durchsuchen höchstens `MicrosoftHealth:MaxPages` Seiten je Aufruf; bei Erreichen: `truncated`, `scanComplete=false`, Fortsetzung über Token.
- Ein einzelner Datensatz wird nie gekürzt: Ist er für die Teilantwort zu groß, fehlt er dort und ist über `search_service_health_issues`, `get_service_health_issue` und den Content-Abruf erreichbar (über `omittedIds` lassen sich die ausgelassenen IDs erkennen, siehe „Befunde F-1/F-2“). Das Escaping-Maß ist konservativ; die tatsächliche Event-Größe liegt rund 100 Bytes über dem gemessenen Text (MCP-Rahmen).

## Befunde F-1/F-2 (Analyse 2026-10-18) – behoben (PR #38, in Dev deployt, live bestätigt)

Beide Befunde betreffen die Teilantwort des Größenwächters (`ResponseSizeGuard`). Es gingen keine Daten verloren; alle Issues blieben über `search_service_health_issues` und `get_service_health_issue` erreichbar (automatisiert getestet, live nicht verifiziert).

**F-1 (deployter Stand): `availableIds` war keine Liste der ausgelassenen Einträge.** Das Feld enthielt die IDs der **ersten 50 Einträge der gesamten Antwort** (hartes `Take(50)`), unabhängig davon, welche Einträge in `value` standen. Live: 100 Einträge, 55 zurückgegeben, 50 IDs – alle gehörten zu den gelieferten Einträgen; die 45 ausgelassenen IDs wurden nicht genannt.
*Fix (PR #38, `eaf7c35`):* Das Feld heißt jetzt `omittedIds` und enthält ausschließlich die IDs der **nicht** in `value` enthaltenen Einträge, höchstens 50 (`ResponseSizeGuard.MaxOmittedIds`); `omittedIdsTruncated=true` zeigt an, dass es noch mehr ausgelassene Einträge gibt (diese liefert die Suche). `availableIds` entfällt (Umbenennung; das Feld existierte nur im Dev-Stand seit PR #36).

**F-2 (deployter Stand): Die Teilantwort verwarf `@odata.nextLink`.** Das Legacy-Tool liest nur die erste Graph-Seite (Standardgröße 100); `totalItems` zählt nur diese Seite. Live bestätigt: Der Dev-Tenant hat 302 Issues, die erste Graph-Seite enthält 100; `moreGraphPages=true` ist korrekt.
*Fix:* Gibt es einen `@odata.nextLink`, setzt die Teilantwort `moreGraphPages=true` und ergänzt `truncationReason`; der Link selbst wird bewusst nicht weitergereicht. Der Hinweistext nennt die Suche für vollständige Listen. Eine Antwort, die ohne Kürzung passt, bleibt das unveränderte Graph-JSON (inklusive `@odata.nextLink`).

## Berechtigungen: warum die Entwickler-Anmeldung ein 403 bekommt

Die Graph-Doku erlaubt für `issues` sowohl delegierte als auch Application-Berechtigung `ServiceHealth.Read.All`. Der ApiServer nutzt in Azure **App-only** über seine Managed Identity (Terraform: `azuread_app_role_assignment.api_managed_identity_to_graph_service_health`). Das `az`-Token eines angemeldeten Global Admins ist dagegen ein **delegiertes** Token der Azure-CLI-App ohne den delegierten Scope `ServiceHealth.Read.All` – daher 403. Das ist kein Defekt des ApiServers; es wurde keine Berechtigung vergeben oder umgangen. Live-Tests laufen über das deployte Gateway/den Connector.
