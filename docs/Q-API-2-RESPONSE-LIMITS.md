# Q-API-2 – Antwortgrößen, Paging und verlustfreier Issue-Abruf

Arbeitspaket auf Branch `feature/q-api-2-large-responses` (**nicht gemergt, nicht deployt**). Fasst Q-API-2 (Report-Paging/Antwortgrößen) und den Fehler
`SSE stream failed: Server-sent event exceeded the 1048576 byte limit` (Tool `get_service_health_issues`) zusammen.
**Q-API-2 ist nicht abgeschlossen** (siehe „Offen“).

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
markierte Teilantwort (`truncated`, `isPartial`, `returnedItems`/`totalItems`, `availableIds`, `hint`); die Inhalte bleiben über die drei neuen Tools erreichbar.

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

**Implementiert und automatisiert getestet**
- `ApiServer.Tests` (237): Größenwächter (Unicode/Escaping, Einzel-Item, Nicht-Collections), Token (Manipulation, Ablauf, Bindung, Schlüsselwechsel, Neustart), Validatoren inkl. Injektion, Suche (0/1/25/100/130 Items, Mehrseiten, Filter, Rückfall bei Graph-400, manipulierte `nextLink`, `MaxPages`, 403/404/429/503, Cancellation), Einzelabruf, Content (verlustfrei, Unicode/Emoji, Änderungserkennung, fremde Tokens/IDs), Startvalidierung.
- `McpServer.Tests` (19): veröffentlichte `tools/list`-Schemas (keine Null-Typ-Arrays in den neuen Tools, Namen/`required` der Alt-Tools unverändert) und E2E über echtes MCP (HTTP/SSE) mit echten ApiServer-Controllern und simuliertem Graph: größtes gemessenes SSE-Event **700 091 Bytes** (Client-Limit 1 048 576), Größenmatrix um das Limit (300 KB–1,5 MB, ASCII und Unicode), einzelnes 3-MB-Item, 1,8 Mio. Zeichen Content in 9 Chunks byte-identisch, Token über „Neustart“, Änderung, Rotation.
- Gateway-Strecke (`tests/gateway_e2e/gateway_leg_e2e.py`, Client-Aufbau wie `make_backend_client`): bestanden, inkl. Negativkontrolle. Bestehende Gateway-Tests: 59 bestanden.
- Inspector 2.11.0 (CLI): `tools/list` und `tools/call` gegen den E2E-Host ohne Meldung.

**Implementiert, nicht live getestet**
- Aufruf gegen den echten Tenant/Graph und das deployte Gateway (Graph-Filterverhalten: `service`/`isResolved` werden serverseitig als `$filter` gesendet, so wie schon im bestehenden Fallback-Pfad – dort live nicht belegt; `classification`, `status`, Datum, `searchText` laufen **lokal** im ApiServer, ein Graph-Filter wird dafür nicht gesendet).
- Key-Vault-Auflösung des Schlüssels in Azure (Plan geprüft, nicht angewendet).
- Warnungen der Inspector-**Web-Oberfläche** (CLI zeigt keine; die UI wurde nicht bedient).

**Offen**
- Paging für Messages und Reports (nur Größenwächter mit markierter Teilantwort; kein Folgeabruf).
- Sieben bestehende Tools veröffentlichen weiterhin Null-Typ-Arrays (`azure_service_health`, `azure_subscription_resource_health`, `get_adoption_report`, `get_message_center_messages`, `intune_service_incidents`, `list_adoption_reports`, `microsoft_service_messages`); bewusst unverändert, per Test dokumentiert. Angleichen wäre eine sichtbare Schemaänderung bestehender Tools.
- Gateway-Limit unverändert: FastMCP 4.1.0 reicht `max_sse_event_size` nicht durch; die Begrenzung liegt serverseitig.

**Bekannte Einschränkungen**
- Listen-Pagination ist offsetbasiert (`$skip` von Graph) und nur „best effort“ (`listConsistency`); Einträge können sich zwischen Seiten verschieben. Content ist über `contentVersion` konsistent (Änderung wird erkannt, nicht verhindert; kein Snapshot-Speicher).
- `searchText` und die lokalen Filter durchsuchen höchstens `MicrosoftHealth:MaxPages` Seiten je Aufruf; bei Erreichen: `truncated`, `scanComplete=false`, Fortsetzung über Token.
- Ein einzelner Datensatz wird nie gekürzt: Er ist in der Liste nicht enthalten, aber per `availableIds` und Content-Abruf erreichbar. Das Escaping-Maß ist konservativ; die tatsächliche Event-Größe liegt rund 100 Bytes über dem gemessenen Text (MCP-Rahmen).

## Berechtigungen: warum die Entwickler-Anmeldung ein 403 bekommt

Die Graph-Doku erlaubt für `issues` sowohl delegierte als auch Application-Berechtigung `ServiceHealth.Read.All`. Der ApiServer nutzt in Azure **App-only** über seine Managed Identity (Terraform: `azuread_app_role_assignment.api_managed_identity_to_graph_service_health`). Das `az`-Token eines angemeldeten Global Admins ist dagegen ein **delegiertes** Token der Azure-CLI-App ohne den delegierten Scope `ServiceHealth.Read.All` – daher 403. Das ist kein Defekt des ApiServers; es wurde keine Berechtigung vergeben oder umgangen. Live-Tests laufen über das deployte Gateway/den Connector.
