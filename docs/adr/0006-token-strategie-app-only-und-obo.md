# ADR-0006: Token-Strategie – App-only als Standard, OBO optional
- Status: Akzeptiert (mit bewusst dokumentierter Schuld) · Datum: 2026-10-16 (rückwirkend)

## Kontext
Der McpServer muss den ApiServer aufrufen. Zwei Möglichkeiten: Identität der **Anwendung** (Client Credentials / Managed Identity) oder
Identität des **Nutzers** (On-Behalf-Of, OBO). Der ApiServer akzeptiert beides (Claim `roles` oder `scp`).

## Entscheidung
- **Standard: App-only.** Alle Daten-Tools (Status, Messages, Reports, Tasks app-only) rufen `ApiServerClient.GetAsync(path)` auf, das ein App-only-Token holt
  (`ApiTokenService.GetAppOnlyTokenAsync`, Scope `{ApiAppIdUri}/.default`). In Azure über Managed Identity, lokal über Client Secret.
- **Optional: OBO** nur im Tool `GetTasksDelegated`: Der Aufrufer übergibt das Nutzer-Token als Tool-Parameter; `GetOnBehalfOfTokenAsync` tauscht es
  (Scope `{ApiAppIdUri}/Tasks.ReadWrite`). OBO geht nur mit Client Secret, nicht mit Managed Identity.
- Der ApiServer prüft für **alle** Controller dieselbe Policy `TasksReadWrite` (App-Rolle `Tasks.ReadWrite.All` ODER Scope `Tasks.ReadWrite`).

## Konsequenzen
+ Einfach, kein Nutzer-Token-Handling in den Daten-Tools; Graph-Daten sind ohnehin tenantweit (App-Permissions).
− **Keine nutzerbezogene Autorisierung:** Jeder Nutzer mit `Mcp.Access` sieht dieselben tenantweiten Admin-Daten. Der Nutzerkontext geht an der MCP→API-Grenze verloren.
− Policy-Name `TasksReadWrite` ist historisch (Task-Demo) und schützt jetzt auch Status/Messages/Reports (irreführend).
− `GetTasksDelegated` erwartet ein Token als LLM-Parameter – ein LLM sollte nie Tokens handhaben; praktisch nur zu Demonstrationszwecken geeignet.
− `TasksController` nutzt eine statische In-Memory-Liste (Demo, bewusst ohne Datenbank).

## Umsetzung
- `src/McpServer/Services/ApiServerClient.cs` (`GetAsync` ohne/mit Token), `src/McpServer/Auth/ApiTokenService.cs` (`GetAppOnlyTokenAsync`, `GetOnBehalfOfTokenAsync`).
- `src/McpServer/Tools/TasksTool.cs` (`GetTasksAppOnly`, `GetTasksDelegated`).
- `src/ApiServer/Program.cs` (Policy `PolicyNames.TasksReadWrite`), `src/ApiServer/Auth/PolicyNames.cs`, alle `Controllers/*` mit `[Authorize(Policy = …)]`.
- Terraform: `azuread_app_role_assignment.mcp_to_api_task_readwrite` (Entra, lokal), `…mcp_managed_identity_to_api_task_readwrite` (Infra, Azure),
  Delegated Grant `mcp_delegated_to_api`.

## Änderungs-Leitfaden
Echte Nutzer-Autorisierung gewünscht (z. B. Rollen pro Nutzer, Audit)? → Eingehendes Token im MCP-Tool aus dem `HttpContext` nehmen (nicht als Parameter) und per OBO
weiterreichen, im ApiServer eine eigene Policy je Datenbereich einführen (z. B. `M365Read`) und Terraform-Scopes/Rollen ergänzen. Bei Umbenennung der Policy alle Controller
und `Program.cs` gemeinsam ändern. Managed Identity unterstützt kein OBO – dann Client Secret/Zertifikat im MCP verwenden (ADR-0007).
