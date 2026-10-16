# ADR-0011: Service-Health-Detail mit Fallback über Overview + Issues
- Status: Akzeptiert · Datum: 2026-10-16 (rückwirkend)

## Kontext
`GET /admin/serviceAnnouncement/healthOverviews/{id}?$expand=issues` liefert je nach Service-ID/Tenant `403` oder `404`. Der Wunsch ist trotzdem ein Detailabruf inklusive Issues.

## Entscheidung
`GraphApiClient.GetServiceHealthDetailAsync` versucht den direkten Endpoint. Bei `403`/`404` greift `GetServiceHealthDetailFallbackAsync`: Overview laden, Service per ID suchen (sonst `KeyNotFoundException`), Issues per `$filter=service eq '<Name>'` (und `isResolved eq false`, außer `includeResolvedIssues`) laden und beides in einem JSON zusammenfügen
(inkl. Metafeldern `source`, `directMicrosoftGraphEndpoint`, `directMicrosoftGraphStatus`, `issueCount`). `GetDegradedServiceHealthDetailsAsync` filtert die Overview auf Status ≠ `ServiceOperational` und holt die Details je Service.

## Konsequenzen
+ Robust gegenüber Graph-Eigenheiten; Antwort enthält die Herkunft (nachvollziehbar für das LLM).
− Antwortschema unterscheidet sich zwischen direktem Pfad und Fallback.
− `GetDegradedServiceHealthDetailsAsync` ruft sequenziell je degradiertem Service auf (N+1 Aufrufe).
− `KeyNotFoundException` wird nicht auf `404` gemappt (Controller liefert dann 500).
− OData-Filterwerte werden mit `EscapeODataString` (Verdopplung von `'`) abgesichert.

## Umsetzung
`src/ApiServer/Services/GraphApiClient.cs`, `src/ApiServer/Controllers/M365StatusController.cs`, `src/McpServer/Tools/M365StatusTool.cs`.

## Änderungs-Leitfaden
Einheitliches Schema gewünscht: Fallback-Antwort im Format des direkten Pfads ausgeben. Parallelisieren: `Task.WhenAll` in `GetDegradedServiceHealthDetailsAsync` (Graph-Throttling beachten). Fehlermapping: globalen Exception-Handler oder `ProblemDetails` im ApiServer ergänzen.
