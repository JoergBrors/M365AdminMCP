# Code-Referenz – alle Segmente in Design und Implementierung

Zweck: Jedes Code-Segment mit **Verantwortung**, **Schnittstellen**, **Konfiguration**, **Entscheidungen (ADR)** und **Erweiterungspunkten**.
Grundlage für präzise Prompts: siehe [`PROMPTING-GUIDE.md`](PROMPTING-GUIDE.md). Entscheidung → Code: [`DESIGN-DECISION-MAP.md`](DESIGN-DECISION-MAP.md).
Stand: 2026-10-16, .NET 10, `ModelContextProtocol.AspNetCore` 2.2.0, `Microsoft.Identity.Web` 4.14.2, Terraform ≥ 1.7.

## 1. Systemüberblick

```
LLM-Client (ChatGPT | Claude | Copilot Studio | Inspector)
   │  HTTPS, OAuth 2.0 Auth-Code + PKCE (Bearer-JWT)  oder  X-MCP-API-Key
   ▼
McpServer  (/mcp, /authorize, /token, /.well-known/*, /healthz)           src/McpServer
   │  HTTPS, Bearer-Token (App-only)
   ▼
ApiServer  (/api/tasks, /api/m365/*, /healthz, /swagger nur Development)  src/ApiServer
   │  HTTPS, Graph-Token (Managed Identity | Client Credentials)
   ▼
Microsoft Graph  v1.0: /admin/serviceAnnouncement/*   beta: /reports/*
```

Lokale Ports: ApiServer `5043/7043`, McpServer `5143/7143`, Inspector v2 `6274` (UI + Backend) und `6275` (MCP-Apps-Sandbox), beide nur `127.0.0.1`.
Azure-Hosts: `https://entramcp-<env>-api.azurewebsites.net`, `https://entramcp-<env>-mcp.azurewebsites.net`.

## 2. McpServer (`src/McpServer`)

### 2.1 `Program.cs` – Komposition und Pipeline
Reihenfolge (wichtig, nicht umstellen ohne Test):
1. Logging (nur Development: Console + Debug).
2. DI: `Configure<AzureAdOptions>("AzureAd")`, `Configure<McpAuthOptions>("McpAuth")`, Singletons `ApiTokenService`, `ApiServerClient`, `AddHttpClient`, `AddMemoryCache`.
3. CORS-Policy `McpDiscovery` (AnyOrigin; GET/POST/OPTIONS; Header Content-Type/Authorization) – für browserseitige Discovery (Copilot Studio).
4. `McpAuth:RequireAuthentication` (Default `true`). **Guard:** `false` nur in `Development` erlaubt (ADR-0015).
5. Falls Auth an: JWT-Bearer über `AddMicrosoftIdentityWebApi("AzureAd")`; `ValidAudiences` = `AzureAd:Audience` + `AzureAd:ClientId`; `OnChallenge` → 401 + `WWW-Authenticate` mit `resource_metadata` und `scope`; Policy `McpAccess` (Scope **oder** Rolle `Mcp.Access`).
6. MCP: `AddMcpServer().WithHttpTransport().WithTools<…>()` (5 Tool-Klassen).
7. Middleware: `UseCors` → `UseAuthentication` → API-Key-Middleware (`X-MCP-API-Key`, ADR-0014) → `UseAuthorization`.
8. Endpunkte: `MapOAuthProtectedResourceMetadata` (`/.well-known/oauth-protected-resource[/mcp]`), `MapOAuthProxyEndpoints`, `MapMcp("/mcp")` (+ `RequireAuthorization("McpAccess")`), `/healthz`.
9. Hilfsfunktionen am Dateiende: `GetExternalBaseUrl`, `GetMcpScope`, `SetNoStoreHeaders` (in `OAuthProxy.cs` dupliziert).

Konfiguration (Env-Var-Schreibweise `A__B`): `AzureAd:{Instance,TenantId,ClientId,ClientSecret,Audience,ApiAppIdUri,UseManagedIdentity,ManagedIdentityClientId}`,
`McpAuth:{ExternalBaseUrl,Scope,ApiKey,RequireAuthentication,AllowedRedirectHosts[]}`, `ApiServer:BaseUrl`.

### 2.2 `Auth/ApiTokenService.cs` (+ `AzureAdOptions`)
- Aufgabe: Tokens für den ApiServer beschaffen.
- `GetAppOnlyTokenAsync()`: Scope `{ApiAppIdUri}/.default`; Managed Identity (`ManagedIdentityCredential`, system- oder user-assigned) **oder** MSAL `AcquireTokenForClient`.
- `GetOnBehalfOfTokenAsync(userToken)`: Scope `{ApiAppIdUri}/Tasks.ReadWrite`; nur mit Client Secret, sonst `InvalidOperationException`.
- Entscheidungen: ADR-0006, ADR-0007. Keine Token-Caching-Schicht außer MSAL/Azure.Identity-internem Cache.

### 2.3 `Auth/McpAuthOptions.cs`
POCO für `McpAuth` (`ExternalBaseUrl`, `Scope`, `ApiKey`, `RequireAuthentication`). Hinweis: `AllowedRedirectHosts` wird direkt über `IConfiguration` gelesen, nicht über dieses POCO.

### 2.4 `Auth/OAuthProxy.cs`
Siehe ADR-0004. Endpunkte: `GET /authorize`, `POST /token`, `GET /.well-known/oauth-authorization-server` (+ `/mcp`-Variante). Alle `AllowAnonymous`.
Details: `IsRedirectUriAllowed` (nur https; optionale Host-Allowlist), Header `Authorization` wird durchgereicht, Fehler von Entra werden mit Status/Body zurückgegeben und geloggt.

### 2.5 `Services/ApiServerClient.cs`
Dünner HTTP-Client auf `ApiServer:BaseUrl`. `GetAsync(path)` = App-only; `GetAsync(path, bearer)` = vorgegebenes Token. `EnsureSuccessStatusCode()` → Fehler werden als `HttpRequestException` an das MCP-SDK gegeben (Tool-Fehler beim Client). Nur GET.

### 2.6 Tools (`Tools/*.cs`) – das sind die für das LLM sichtbaren Funktionen
Jede Klasse: `[McpServerToolType]`, Konstruktor-Injektion, Methoden `[McpServerTool, Description(…)]`, Parameter-`[Description]` bilden das **Tool-Schema für das LLM** (Qualität dieser Texte bestimmt die Treffsicherheit).

| Klasse | Tool-Methode | ApiServer-Pfad | Parameter |
|---|---|---|---|
| `M365StatusTool` | `GetServiceHealthOverview` | `GET /api/m365/status/health-overviews` | – |
| | `GetServiceHealthDetail` | `GET …/health-overviews/{id}` | `serviceHealthId`, `expandIssues`=true, `includeResolvedIssues`=false |
| | `GetDegradedServiceHealthDetails` | `GET …/health-overviews/degraded` | – |
| | `GetServiceHealthIssues` | `GET /api/m365/status/issues` | – |
| `M365MessagesTool` | `GetMessageCenterMessages` | `GET /api/m365/messages` | `odataFilter?` |
| `M365AdoptionTool` | `GetOffice365ActiveUserDetail`, `GetM365AppUserDetail` | `GET /api/m365/reports/{name}` | `period`=D30 |
| `M365AdoptionCatalogTool` | `ListAdoptionReports` | `GET /api/m365/reports/catalog` | `category?` |
| | `GetAdoptionReport` | `GET /api/m365/reports/{reportName}` | `reportName`, `period?`, `date?`, `serviceArea?`, `appId?` |
| `MicrosoftHealthTools` | `intune_service_health`, `intune_service_incidents`, `microsoft_service_messages`, `azure_service_health`, `azure_resource_health`, `azure_subscription_resource_health`, `microsoft_health_summary` | `GET /api/microsoft-health/*` | siehe `docs/microsoft-health/02-implementation.md` (ADR-0016) |
| `TasksTool` | `GetTasksAppOnly` | `GET /api/tasks` | – |

Neues Tool: Klasse anlegen + in `Program.cs` mit `.WithTools<…>()` registrieren; ApiServer-Endpoint zuerst bauen (Abschnitt 3).

### 2.7 `Reporting/ReportCatalog.cs`
Katalog der 92 Graph-Beta-Reports (`ReportDefinition(FunctionName, Category, Description, ParamMode, DelegatedOnly)`; `getApiUsage` ist `DelegatedOnly` und nicht abrufbar, 91 App-only nutzbar, davon in der Praxis `getOffice365ActiveUserCounts` mit Microsoft-seitigem Defekt), Dictionary case-insensitiv, `AllowedPeriods = D7/D30/D90/D180`. Wird auch vom ApiServer kompiliert (ADR-0009). Siehe ADR-0008.

### 2.8 `Utils/CsvJsonConverter.cs`
Eigener CSV-Parser (Quotes, `""`, Zeilenumbrüche in Feldern) → JSON-Array (alle Werte als String). Erste Zeile = Header. Auch im ApiServer genutzt.

### 2.9 Projektdateien
`McpServer.csproj`: `net10.0`, `Nullable`, `ImplicitUsings`, `InvariantGlobalization`, `UserSecretsId`; Pakete Azure.Identity, ModelContextProtocol.AspNetCore, Microsoft.Identity.Client, Microsoft.Identity.Web; `appsettings.Development.json` wird kopiert, aber **nie publiziert**.
`Properties/launchSettings.json`: Profil `McpServer` → `http://localhost:5143;https://localhost:7143`, `ApiServer__BaseUrl=http://localhost:5043`.

## 3. ApiServer (`src/ApiServer`)

### 3.1 `Program.cs`
1. Auth: JWT-Bearer (`AddMicrosoftIdentityWebApi("AzureAd")`), `ValidAudiences` = Audience + ClientId. Akzeptiert App-only (`roles`) und Delegated (`scp`).
2. Policy `TasksReadWrite` (Rolle `Tasks.ReadWrite.All` **oder** Scope `Tasks.ReadWrite`) – von allen Controllern verwendet (ADR-0006).
3. DI: `Configure<GraphAuthOptions>("GraphAuth")`, Singletons `GraphTokenService`, `GraphApiClient`, `AddHttpClient`, benannter Client `GraphNoRedirect` (`AllowAutoRedirect=false`).
4. Swagger (Swashbuckle): OAuth2 Auth-Code + PKCE gegen Entra, Scope `SwaggerOAuth:Scope`; **nur Development** aktiv, no-store-Header für `/swagger`.
5. `UseHttpsRedirection` nur außerhalb Development; `UseAuthentication/Authorization`; `MapControllers`; `/healthz` anonym.

### 3.2 `Auth/GraphTokenService.cs`
Singleton. Konstruktor wählt Pfad: `GraphAuth:UseManagedIdentity=true` → `ManagedIdentityCredential`; sonst MSAL Confidential Client (`AzureAd:TenantId/ClientId/ClientSecret`, sonst Startfehler `InvalidOperationException`). `GetGraphTokenAsync` → Scope `https://graph.microsoft.com/.default`.
`Auth/GraphAuthOptions.cs`: `UseManagedIdentity`, `ManagedIdentityClientId`. `Auth/PolicyNames.cs`: Konstante `TasksReadWrite`.

### 3.3 `Services/GraphApiClient.cs` – gesamte Graph-Logik
| Methode | Graph | Besonderheit |
|---|---|---|
| `GetServiceHealthOverviewAsync` | v1.0 `/admin/serviceAnnouncement/healthOverviews` | – |
| `GetServiceHealthDetailAsync` | v1.0 `…/healthOverviews/{id}[?$expand=issues]` | Fallback bei 403/404 (ADR-0011) |
| `GetDegradedServiceHealthDetailsAsync` | Overview + je Service Detail | N+1, sequenziell |
| `GetServiceHealthIssuesAsync` | v1.0 `/admin/serviceAnnouncement/issues` | kein Paging |
| `GetMessageCenterMessagesAsync` | v1.0 `/admin/serviceAnnouncement/messages[?$filter]` | `odataFilter` URL-escaped |
| `GetCatalogReportAsJsonAsync` | baut Pfad aus `ReportCatalog` | validiert `period`; `date`/`serviceArea`/`appId` **nicht** (ADR-0008) |
| `GetReportAsJsonAsync` | beta `/reports/…?$format=application/json` | No-Redirect, Retry ohne `$format` bei 400, CSV-Download ohne Auth-Header, begrenzte Retries bei transienten Fehlern (max. 3 Versuche), Diagnose-Logging (ADR-0010) |
| privat: `GetGraphV1Async`, `TrimLeadingSlash`, `EscapeODataString`, `ValidatePeriod` | | |

### 3.4 Controller
| Controller | Route | Aktionen |
|---|---|---|
| `TasksController` | `api/tasks` | `GET` (callerType app-only/delegated + statische Liste), `POST` (fügt String hinzu; nicht persistent, nicht threadsicher – Demo) |
| `M365StatusController` | `api/m365/status` | `health-overviews`, `health-overviews/degraded`, `health-overviews/{id}?expandIssues&includeResolvedIssues`, `issues` |
| `M365MessagesController` | `api/m365/messages` | `GET ?odataFilter` |
| `MicrosoftHealthController` | `api/microsoft-health` | `intune/health`, `intune/incidents`, `messages`, `azure/events`, `azure/resource`, `azure/subscription-resources`, `summary` (Clients in `Health/`, ADR-0016) |
| `M365ReportsController` | `api/m365/reports` | `GET catalog?category`, `GET {reportName}?period&date&serviceArea&appId` |
Alle: `[Authorize(Policy = PolicyNames.TasksReadWrite)]`, Antworten als `ContentResult` mit `application/json` (Rohdurchreichung von Graph-JSON).

### 3.5 Projektdatei
`ApiServer.csproj`: zusätzlich `Swashbuckle.AspNetCore`; verlinkt `ReportCatalog.cs` und `CsvJsonConverter.cs` aus dem McpServer (ADR-0009).

## 4. Terraform (`terraform/`)

| Datei | Inhalt |
|---|---|
| `main.tf` | Locals (`name_prefix = entramcp-<env>`, Tags), Resource Group `rg-entramcp-<env>`, Module `entra_id` und `infra`; Swagger-Redirects (Azure + localhost) |
| `providers.tf` | Provider-Versionen (azurerm ~>4.54, azuread ~>3.0, random ~>3.6), Backend `azurerm`, `use_oidc=true` |
| `variables.tf` / `outputs.tf` / `terraform.dev.tfvars` | Root-Variablen und -Outputs, dev-Werte (Redirect-URIs von ChatGPT/Claude/Copilot) |
| `modules/entra-id/main.tf` | Apps: `api-server`, `swagger-client`, `mcp-server`, `chatgpt-/claude-mcp-client` (public), `copilot-mcp-client` (confidential); Scopes `Tasks.ReadWrite`, `Mcp.Access`; App-Rolle `Tasks.ReadWrite.All`; Graph-Rechte (`ServiceHealth.Read.All`, `ServiceMessage.Read.All`, `Reports.Read.All`); Rollen-Zuweisungen und Delegated Grants; Secrets (8760 h) |
| `modules/entra-id/outputs.tf` | App-IDs, Identifier-URIs, Secrets, `mcp_oauth_client_ids` |
| `modules/infra/main.tf` | Log Analytics + App Insights, Key Vault (+ Secrets, RBAC), Service Plan, Linux Web Apps `-api`/`-mcp` (Managed Identity, App Settings, Key-Vault-Referenzen), Rollenzuweisungen für Managed Identities |

Namenskonventionen: Apps `api-server-<env>`, `mcp-server-<env>`, Identifier-URIs `api://api-server-<env>`, `api://mcp-server-<env>`; Key Vault `entramcp-<env>-kv-<4 Zeichen>` (max. 24 Zeichen).

## 5. CI/CD und Skripte
- `.github/workflows/deploy.yml`: ADR-0012 (build → plan im PR / apply + publish + zip-deploy auf `main`).
- Skripte in `scripts/` (PowerShell 7). **Aktiv:** `Connect-Azure`, `Install-Prerequisites`, `Initialize-TerraformBackend`, `Invoke-TerraformPlan`, `Invoke-TerraformApply`, `Export-TerraformLocalSettings` (erzeugt beide `appsettings.Development.json`),
  `Set-GitHubActionsSettings`, `Add-McpOauthRedirectUri`, `New-GitHubRepo`, `Stop-LocalDebugServers`, `Start-McpInspector`. **Legacy (nicht verwenden):** `Set-EntraIdApps`, `Get-EntraIdDiff`, `Invoke-BicepDeploy`, `Invoke-BicepWhatIf`, Ordner `infra/`.

## 6. Lokale Entwicklung
`.vscode/launch.json` + `tasks.json`: Konfigurationen „Debug ApiServer“, „Debug McpServer“, „Debug API + MCP“ (Compound), **„Debug API + MCP + Inspector“** (ADR-0015: Stop → Build → `Start-McpInspector.ps1` → API + MCP im Debugger; Browser öffnet nach `/healthz` mit Auto-Connect-Deep-Link). `Stop-LocalDebugServers.ps1` beendet nur Prozesse mit Repo-Komponentenpfad (`src/ApiServer/bin`, `src/McpServer/bin`, `tools/mcp-inspector`); fremde Port-Besitzer 5043/7043/5143/7143/6274/6275 werden nur gemeldet. Inspector gepinnt in `tools/mcp-inspector/` (v2.11.0, Lockfile). `McpServer/Program.cs`: Development-Guard + Loopback-Middleware bei `RequireAuthentication=false`.
`appsettings.Development.json` (git-ignoriert) kommt aus `Export-TerraformLocalSettings.ps1`.

## 7. Bekannte Lücken / technische Schuld (Backlog-Kandidaten)
1. `date`/`serviceArea`/`appId` nicht validiert (Pfad-Injection in Graph-URL) – ADR-0008.
2. Keine nutzerbezogene Autorisierung; Policy-Name `TasksReadWrite` irreführend – ADR-0006.
3. ~~`GetTasksDelegated` nimmt Token als LLM-Parameter~~ – Tool entfernt (ADR-0006, Aktualisierung 2026-10-17).
4. API-Key-Vergleich nicht zeitkonstant – ADR-0014.
5. Größenlimit: `ResponseSizeGuard` (ApiServer/Services) begrenzt Status-, Messages- und Report-Antworten auf `ResponseLimits:MaxResponseBytes` (Default 700000, gemessen als JSON-escaptes UTF-8) und liefert sonst `truncated`/`isPartial` mit Hinweis. Echtes Paging gibt es nur für Service-Health-Issues (`ServiceHealthIssueService`: `search_service_health_issues`, `get_service_health_issue`, `get_service_health_issue_content`; `ContinuationTokenService` = HMAC-signierte, 15 Minuten gültige Tokens; dedizierter Schlüssel `ResponseLimits:ContinuationSigningKey` aus Key Vault, Pflicht außerhalb von Development, kein Fallback). Messages/Reports ohne Paging – ADR-0010, Q-API-2. Details, Teststand, Rotation: [Q-API-2-RESPONSE-LIMITS.md](Q-API-2-RESPONSE-LIMITS.md).
6. `GetMcpScope`/`GetExternalBaseUrl`/`SetNoStoreHeaders` zweifach vorhanden.
7. Geteilte Quelldateien statt Bibliothek – ADR-0009.
8. Testabdeckung nur für `GraphApiClient.GetReportAsJsonAsync` (`tests/ApiServer.Tests`, in `EntraMcpMvp.sln`); übrige Komponenten ohne automatisierte Tests.
11. NuGet-Warnungen NU1902/NU1903: `System.Security.Cryptography.Pkcs` 10.0.10 (transitiv über `Microsoft.Identity.Web` 4.14.2) hat bekannte Schwachstellen – bei einem Update von `Microsoft.Identity.Web` prüfen.
9. `KeyNotFoundException`/`ArgumentException` aus `GraphApiClient` werden nicht auf 404/400 gemappt (Controller liefert 500).
10. Die `bin/obj`-Ordner enthalten alte `net8.0`-Artefakte; maßgeblich ist `net10.0`.
