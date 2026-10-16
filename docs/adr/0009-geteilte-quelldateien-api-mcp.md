# ADR-0009: Geteilte Quelldateien zwischen ApiServer und McpServer
- Status: Akzeptiert (mit bewusst dokumentierter Schuld) · Datum: 2026-10-16 (rückwirkend)

## Kontext
Der Report-Katalog (`McpServer/Reporting/ReportCatalog.cs`) und der CSV-Konverter (`McpServer/Utils/CsvJsonConverter.cs`) werden vom ApiServer gebraucht, liegen aber unter `src/McpServer`.

## Entscheidung
`ApiServer.csproj` bindet beide Dateien per `<Compile Include="..\McpServer\…" Link="…" />` ein, statt eine gemeinsame Klassenbibliothek anzulegen. Die Namespaces bleiben `McpServer.Reporting` / `McpServer.Utils` (sichtbar in `using McpServer.Reporting;` im ApiServer).

## Konsequenzen
+ Kein drittes Projekt, kein Paket-/Versionsmanagement; minimale Build-Konfiguration.
− Der ApiServer hängt per Quellpfad am McpServer; Verschieben/Umbenennen bricht den Build.
− Irreführende Namespaces im ApiServer; der Katalog „gehört“ semantisch zur API.
− `ReportCatalog` wird in beide Assemblies kompiliert.

## Umsetzung
`src/ApiServer/ApiServer.csproj` (ItemGroup mit `Compile Include`), Nutzer: `M365ReportsController`, `GraphApiClient`.

## Änderungs-Leitfaden
Beim Wachsen (weitere geteilte Typen) ein Projekt `src/Shared` (Namespace z. B. `M365Admin.Shared`) anlegen und beide Projekte referenzieren; dann `using`-Zeilen und die Solution `EntraMcpMvp.sln` anpassen und die Dateien per `git mv` verschieben. Ohne diesen Schritt keine Dateien aus `src/McpServer` in den ApiServer verlinken.
