# Prompting-Leitfaden für KI-Coding-Agenten

Ziel: Aufgaben so formulieren, dass der Agent die richtigen Dateien findet, die Design-Entscheidungen respektiert und nichts Wichtiges vergisst.
Grundlage: [`DEPLOYMENT-RUNBOOK.md`](DEPLOYMENT-RUNBOOK.md), [`DEBUGGING.md`](DEBUGGING.md), [`CODE-REFERENCE.md`](CODE-REFERENCE.md), [`DESIGN-DECISION-MAP.md`](DESIGN-DECISION-MAP.md), [`adr/`](adr/README.md), [`../AGENTS.md`](../AGENTS.md).

## 1. Aufbau eines guten Prompts
1. **Ziel** in einem Satz (Verhalten, nicht Implementierung).
2. **Betroffene Komponente** (McpServer | ApiServer | Terraform | CI | Skripte) und, wenn bekannt, Datei/Symbol aus `CODE-REFERENCE.md`.
3. **Relevante ADRs** nennen („beachte ADR-0006, ADR-0008“).
4. **Akzeptanzkriterien** (prüfbar: Build grün, Tool erscheint im Inspector, `terraform plan` ohne Löschungen).
5. **Grenzen** (z. B. „keine neuen Pakete“, „Terraform nicht anwenden“, „nur Feature-Branch, kein Push auf main“).
6. **Verifikation**: wie der Agent es prüft (`dotnet build EntraMcpMvp.sln`, Inspector-Aufruf, `Invoke-TerraformPlan.ps1 -Environment dev`).

## 2. Festregeln, die jeder Prompt voraussetzen darf
- Immer Feature-Branch (`feature/…`, `docs/…`, `fix/…`) und PR nach `main`; kein Direct-Push (ADR-0012).
- Deployment nur über Terraform (ADR-0001); `-Force` bei Terraform nie dauerhaft.
- Keine Secrets in Dateien/Commits (`appsettings.Development.json` ist git-ignoriert).
- Neue Graph-Permission → beide Terraform-Stellen (ADR-0002).
- Neue externe OAuth-Clients nur per Pre-Registration (ADR-0005).

## 3. Prompt-Vorlagen

### 3.1 Neues MCP-Tool für vorhandenen API-Endpoint
```
Ziel: Neues MCP-Tool "<Name>" soll <Verhalten> liefern.
Komponente: McpServer. API-Endpoint existiert: GET /api/<pfad> (Controller <X>).
Beachte ADR-0002 (Tools rufen nur ApiServerClient) und ADR-0006 (App-only-Token).
Schritte: Methode in src/McpServer/Tools/<Klasse>.cs (oder neue Klasse mit [McpServerToolType]), deutsche [Description] für Tool und Parameter,
in Program.cs bei .WithTools<…>() registrieren.
Akzeptanz: Build grün; Tool im MCP Inspector (Debug API + MCP + Inspector) sichtbar und liefert Daten.
Grenzen: keine Änderung an Auth/Terraform.
```

### 3.2 Neuer Graph-Endpoint End-to-End
```
Ziel: <Daten> aus Microsoft Graph (<URL/Doku>) als MCP-Tool bereitstellen.
Reihenfolge: (1) Benötigte Graph-App-Permission ermitteln und nennen (Rückfrage vor Terraform-Änderung!).
(2) Terraform: entra-id/main.tf required_resource_access + azuread_app_role_assignment UND infra/main.tf api_managed_identity_to_graph_* (ADR-0002).
(3) GraphApiClient-Methode (v1.0 über GetGraphV1Async; beta nur falls nötig, ADR-0010). (4) Controller-Action mit [Authorize(Policy = TasksReadWrite)] (ADR-0006).
(5) Tool + WithTools-Registrierung. (6) Doku: ARCHITECTURE.md Tool-Katalog, ggf. neue ADR.
Akzeptanz: dotnet build; terraform plan ohne Löschungen (Invoke-TerraformPlan.ps1); lokal im Inspector getestet.
```

### 3.3 Neuer Graph-Report
```
Ziel: Report <getXyz> aufnehmen. Doku: <Learn-URL>. Nur Eintrag in src/McpServer/Reporting/ReportCatalog.cs (ADR-0008).
ParamMode laut Doku wählen (PeriodOrDate | PeriodOnly | None | Special). Kategorie wie bestehende Einträge schreiben.
Falls neue Parameterform: neuen ReportParamMode + case in GraphApiClient.GetCatalogReportAsJsonAsync + Text im Controller-catalog.
Akzeptanz: ListAdoptionReports zeigt den Eintrag; GetAdoptionReport liefert JSON (oder dokumentierter Fehler).
```

### 3.4 Neuer LLM-Client anbinden
```
Ziel: Client <Name> per OAuth anbinden. Frage zuerst: verlangt die Connector-UI ein Secret? (ja → confidential/web{}, nein → public_client{}; ADR-0005).
Dateien: entra-id/main.tf (local.mcp_oauth_clients bzw. eigener Block), variables.tf (Root + Modul), terraform.dev.tfvars, outputs/Key Vault (infra/main.tf),
scripts/Add-McpOauthRedirectUri.ps1 (Client-Parameter), docs/DEPLOYMENT.md.
Teste mit Invoke-TerraformPlan.ps1; kein apply ohne Rückfrage.
```

### 3.5 Sicherheit / Härtung (Backlog Abschnitt 7 in CODE-REFERENCE)
```
Ziel: Eingabevalidierung in GraphApiClient.GetCatalogReportAsJsonAsync: date (yyyy-MM-dd), appId (GUID), serviceArea (Whitelist).
Fehler als ArgumentException; im ApiServer auf HTTP 400 mappen (ProblemDetails). Tests ergänzen (neues Testprojekt tests/ApiServer.Tests, in EntraMcpMvp.sln aufnehmen).
Beachte ADR-0008.
```

### 3.6 Refactoring
```
Ziel: Gemeinsame Typen in src/Shared auslagern (ADR-0009 Änderungs-Leitfaden). Per git mv verschieben, Namespaces ändern, beide csproj + Solution anpassen,
Build grün, keine Verhaltensänderung. Kein weiteres Aufräumen.
```

### 3.7 Fehlersuche
```
Symptom: <exakte Fehlermeldung/AADSTS-Code/HTTP-Status>, Client: <ChatGPT|Claude|Copilot|Inspector>, Umgebung: <dev Azure | lokal>.
Hypothesen aus ADRs prüfen (ADR-0004/0005 für AADSTS*, ADR-0013 für Cold-Start-Timeouts). Erst Logs/Code lesen, dann ändern. Keine Rechte umgehen.
```

### 3.8 Doku/ADR pflegen
```
Ziel: Neue Entscheidung <X> als ADR-00NN dokumentieren (Vorlage in docs/adr/README.md): Kontext, Entscheidung, Alternativen, Konsequenzen, Umsetzung (Datei · Symbol),
Änderungs-Leitfaden. Zeile in docs/adr/README.md und docs/DESIGN-DECISION-MAP.md ergänzen. Code-Fundstellen vor dem Schreiben im Code verifizieren.
```

## 4. Typische Fehlerquellen (Prompts sollten sie ausschließen)
| Fehler | Gegenmaßnahme im Prompt |
|---|---|
| Nur eine der zwei Terraform-Stellen für Graph-Rechte geändert | „ADR-0002: Entra-Modul **und** infra-Modul“ |
| Legacy `infra/` oder `Set-EntraIdApps.ps1` als Vorlage benutzt | „Nur `terraform/`; `infra/` ignorieren“ |
| `SSE`/Session-Features eingebaut | „Stateless Streamable HTTP, ADR-0003“ |
| Tokens/Secrets in Tool-Parametern oder Logs | „Nie Tokens als Tool-Parameter; keine Secrets loggen“ |
| `terraform apply` direkt ausgeführt | „Nur plan; apply nur nach meiner Freigabe“ |
| Direct-Push auf `main` | „Feature-Branch + PR“ |
| Neues Tool ohne Registrierung | „In Program.cs bei `.WithTools<…>()` eintragen“ |
| LLM-lesbare Beschreibungen vergessen | „Deutsche `[Description]` für Tool und jeden Parameter“ |

## 5. Definition of Done (zum Einfügen in Prompts)
- [ ] `dotnet build EntraMcpMvp.sln` ohne Fehler/Warnungen
- [ ] Verhalten lokal im MCP Inspector geprüft (Debug API + MCP + Inspector)
- [ ] Terraform: `Invoke-TerraformPlan.ps1 -Environment dev` ohne unerwartete Löschungen
- [ ] Doku aktualisiert (CODE-REFERENCE / ADR / DESIGN-DECISION-MAP / ARCHITECTURE Tool-Katalog)
- [ ] Änderung auf Feature-Branch, PR nach `main` erstellt, keine Secrets im Diff
