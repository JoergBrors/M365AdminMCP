# ADR-0008: Datengetriebener Report-Katalog statt 92 Tools
- Status: Akzeptiert · Datum: 2026-10-16 (rückwirkend)

## Kontext
Microsoft Graph Beta stellt unter `reportRoot` über 90 Adoption-/Usage-Report-Funktionen bereit. Pro Funktion ein eigenes MCP-Tool würde den Tool-Kontext des LLM aufblähen und jede Ergänzung zu Code machen.

## Entscheidung
Ein deklarativer Katalog (`ReportCatalog.Reports`: Name, Kategorie, Beschreibung, `ReportParamMode`) und **zwei generische Tools**: `ListAdoptionReports` (Katalog, optional nach Kategorie) und `GetAdoptionReport` (beliebiger Report).
Der ApiServer baut den Graph-Pfad aus dem Katalogeintrag (`GraphApiClient.GetCatalogReportAsJsonAsync`). Zwei Convenience-Tools (`GetOffice365ActiveUserDetail`, `GetM365AppUserDetail`) bleiben als feste Abkürzungen.

`ReportParamMode`: `PeriodOrDate` (entweder `period` ODER `date`), `PeriodOnly` (D7/D30/D90/D180), `None`, `Special` (nur `getApiUsage`).

## Konsequenzen
+ Neuer Report = eine Zeile im Katalog; LLM entdeckt Reports zur Laufzeit.
+ `period` wird gegen `AllowedPeriods` validiert; unbekannte Reports werden abgelehnt (Whitelist).
− LLM benötigt zwei Schritte (erst Katalog, dann Abruf).
− **Bekannte Lücke:** `date`, `serviceArea` und `appId` werden nicht validiert und direkt in den Graph-Pfad interpoliert (`GraphApiClient`, Branches `PeriodOrDate` und `Special`). Das sollte durch Format-Prüfung (`yyyy-MM-dd`, GUID, Whitelist für `serviceArea`) geschlossen werden.
− Beta-Endpunkte sind laut Microsoft „subject to change“.
− **`getApiUsage` ist nur delegiert abrufbar** (Doku: Application „Not supported“; Nutzer mit Global Reader/Global Administrator). Mit dem App-only-Token des ApiServers antwortet Graph immer `401 Application is not authorized` – eine Terraform-Berechtigung behebt das nicht. Der Eintrag trägt `ReportDefinition.DelegatedOnly = true`: `GetCatalogReportAsJsonAsync` weist ihn mit `NotSupportedException` ab (kein Graph-Aufruf), der Katalog meldet `available: false` und die Beschreibung beginnt mit „NICHT VERFUEGBAR“. Erlaubte Perioden laut Doku: D7, D30, D60. Ein Delegated-Pfad (OBO mit Nutzer-Token) wäre eine Architekturentscheidung (siehe ADR-0006).
− Copilot-Reports (`getMicrosoft365Copilot*`) akzeptieren D7/D28/D90/D180 (Standard D28), nicht D30.

## Umsetzung
- `src/McpServer/Reporting/ReportCatalog.cs` (`ReportParamMode`, `ReportDefinition`, `ReportCatalog.Reports`, `AllowedPeriods`).
- `src/ApiServer/Controllers/M365ReportsController.cs` (`GET catalog`, `GET {reportName}`).
- `src/ApiServer/Services/GraphApiClient.cs` (`GetCatalogReportAsJsonAsync`, `ValidatePeriod`).
- `src/McpServer/Tools/M365AdoptionCatalogTool.cs`, `M365AdoptionTool.cs`.

## Änderungs-Leitfaden
Neuer Graph-Report: nur Eintrag in `ReportCatalog.cs` (richtigen `ReportParamMode` wählen). Neue Parameterform: neuen `ReportParamMode` + `case` in `GetCatalogReportAsJsonAsync` + Beschreibung in `ListAdoptionReports` im Controller (Text der `parameters`-Spalte) + Tool-Parameter.

## Aktualisierung 2026-10-17
Die oben genannte **bekannte Lücke ist geschlossen**: `date`, `appId` und `serviceArea` werden von `ApiServer/Services/ReportParameterValidator.cs` strikt validiert (`yyyy-MM-dd`, GUID, `[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}`) und vor jedem Graph-Aufruf geprüft (Tests `ReportParameterValidatorTests`, `GraphApiClientReportTests`). `getApiUsage` bleibt delegiert-only und im App-only-Betrieb abgewiesen. Siehe Finding F-001.
