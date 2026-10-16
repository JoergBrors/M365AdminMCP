# ADR-0010: Graph-Reports über Beta, JSON-first mit CSV-Fallback
- Status: Akzeptiert · Datum: 2026-10-16 (rückwirkend)

## Kontext
Die Report-Endpunkte unter `reportRoot` liefern je nach Funktion CSV (häufig per 302-Redirect auf eine Download-URL) oder JSON. Der vollständige Satz (u. a. Copilot-Usage) existiert nur in Graph **Beta**.

## Entscheidung
`GraphApiClient.GetReportAsJsonAsync`:
1. Aufruf `beta/reports/…` mit `$format=application/json` über einen HttpClient **ohne** Auto-Redirect (`GraphNoRedirect`).
2. Erfolg + JSON-Content-Type → Body unverändert.
3. Redirect (301/302/307/308) → CSV von der `Location` laden (anonymer Client ohne Graph-Token) und per `CsvJsonConverter.ConvertToJsonArray` in ein JSON-Array wandeln.
4. Sonstiger Erfolg → Body als CSV interpretieren; sonst `EnsureSuccessStatusCode()`.
Alle Service-Health/Message-Center-Aufrufe nutzen dagegen **v1.0** (`GetGraphV1Async`).

## Konsequenzen
+ Einheitliches JSON für das LLM, unabhängig vom Quellformat; das Graph-Token geht nicht an die Download-URL.
− Beta-Schema kann sich ändern; CSV-Spalten sind ungetypte Strings.
− Große Reports werden vollständig im Speicher gehalten und als einzelne Tool-Antwort zurückgegeben (Token-/Größenlimit des LLM beachten). `@odata.nextLink`-Paging wird in `GetGraphV1Async` nicht verfolgt.
− Der eigene CSV-Parser unterstützt Quotes, keine abweichenden Delimiter oder BOM-Behandlung.
− Einzelne Reports sind bei Microsoft für den Tenant dauerhaft defekt (gegen den Dev-Tenant gemessen, 2026-10-16): `getOffice365ActiveUserCounts` liefert mit `$format=application/json` einen Graph-400 (`Can not add property tfm …`) und der CSV-Download-Host (`reportsweu.office.com`) nimmt die Verbindung an, liest den Request und schließt **ohne Antwort** (TLS 1.2 und 1.3, unabhängig von User-Agent/Accept, `v1.0`/`beta`, `$format=text/csv`, mehrfache Wiederholung mit Backoff). Das ist serverseitig und im Code nicht behebbar. `getApiUsage` liefert 401 (fehlende Graph-Berechtigung, offen).

## Fehlerbehandlung (Stand Fix `fix/report-download-resilience`)
- Graph-Aufruf und CSV-Download werden bei **transienten** Fehlern begrenzt wiederholt: höchstens 3 Versuche (Wartezeiten 500 ms und 1,5 s, +0–20 % Jitter), nur bei Netzwerk-/Timeout-Fehlern (`SocketException`/`IOException`/`TaskCanceledException` ohne Aufrufer-Abbruch, Verbindungsfehlern) und den Statuscodes 408/429/502/503/504. 4xx, Validierungsfehler und Abbruch durch den Aufrufer werden nie wiederholt.
- Der Download sendet **keinen** `Authorization`-Header. Geloggt werden nur Host, Report-Pfad, Versuch, Phase ("Verbindungsaufbau/TLS", "nach Verbindungsaufbau: Verbindung ohne Antwort beendet", "Timeout", "HTTP-Status n"), `SocketError` und `HttpRequestError`; die vorautorisierte URL (Token in der Query) wird weder geloggt noch in Exceptions übernommen.
- Tests: `tests/ApiServer.Tests/GraphApiClientReportTests.cs` (simulierte Graph-/Download-Antworten, ohne Netzwerk).

## Umsetzung
`src/ApiServer/Services/GraphApiClient.cs` (`GetReportAsJsonAsync`, `GetGraphV1Async`), `src/ApiServer/Program.cs` (benannter HttpClient `GraphNoRedirect`), `src/McpServer/Utils/CsvJsonConverter.cs`.

## Änderungs-Leitfaden
Auf v1.0 umstellen, sobald ein Report GA ist: Pfadpräfix im Aufruf ändern (derzeit hartkodiertes `beta/`). Paging/Größenlimit: in `GetGraphV1Async` `@odata.nextLink` verfolgen bzw. in Controller und Tool `top`/Zeilenlimit einführen. Neues Format: in `GetReportAsJsonAsync` erweitern, nicht in den Controllern.
