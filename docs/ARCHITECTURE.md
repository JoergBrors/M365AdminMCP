# Architektur

## Überblick

```
┌────────────┐   OAuth2 Auth Code (delegated)   ┌──────────────┐
│  Client /  │ ───────────────────────────────► │  Entra ID    │
│  Nutzer    │ ◄─────────────────────────────── │  (Tenant)    │
└─────┬──────┘        ID-/Access-Token           └──────┬───────┘
      │                                                  │ app-only token (Client Credentials)
      │ Access Token (delegated, scp=...)                │
      ▼                                                  ▼
┌───────────────────┐   OBO oder App-only Token   ┌──────────────────┐
│    MCP Server      │ ───────────────────────────►│    API Server    │
│ (ASP.NET Core,     │                              │ (ASP.NET Core,   │
│  ModelContextProtocol) │◄────────────────────────│  Microsoft.Identity.Web)│
└───────────────────┘        JSON Antwort           └──────────────────┘
```

Zwei Entra-ID-App-Registrierungen:

1. **`api-server` (Resource App)**
   - Exposed API mit App-ID-URI `api://<app-id>`
   - **App Role** (für App-only-Zugriff, z. B. von Batch-Jobs): `Tasks.ReadWrite.All`
   - **Delegated Scope** (für Nutzer-Kontext): `Tasks.ReadWrite`
   - Validiert Tokens über `Microsoft.Identity.Web` – ein `AuthorizationPolicy` akzeptiert entweder `roles`-Claim (App-only) **oder** `scp`-Claim (delegated)

2. **`mcp-server` (Client App)**
   - **Application Permission** auf `api-server` → `Tasks.ReadWrite.All` (App-Role-Assignment, per Admin Consent freigegeben – das übernimmt `terraform/modules/entra-id`)
   - **Delegated Permission** auf `api-server` → `Tasks.ReadWrite`
   - Redirect URI für den Auth-Code-Flow (z. B. `https://<mcp-app>.azurewebsites.net/signin-oidc`)
   - Client Secret (aktuell), Zertifikat wäre die härtere Alternative für Produktion, abgelegt im Key Vault
   - Führt bei delegated Anfragen einen **On-Behalf-Of (OBO)** Flow aus, um im Namen des Nutzers gegen den API Server zu rufen; bei App-only-Anfragen nutzt er **Client Credentials**

3. **Externe MCP-OAuth-Clients** (`chatgpt-mcp-client-<env>`, `claude-mcp-client-<env>`, `copilot-mcp-client-<env>`)
   - Eigene App-Registrierungen, die es einem externen Chat-Client (ChatGPT, Claude, Copilot Studio) erlauben, sich per OAuth Authorization Code + PKCE gegen den MCP-Server zu authentifizieren, ohne dass der Client selbst Zugriff auf die `mcp-server`-App-Registrierung oder deren Secret hat
   - ChatGPT/Claude sind **Public Clients** (kein Secret, PKCE-only), Copilot Studio ist ein **Confidential Client** (mit Secret, da die Power-Platform-OAuth-UI ein Secret-Feld zwingend verlangt) — siehe [`docs/ENTRA-ID-SETUP.md`](ENTRA-ID-SETUP.md) für die genaue Begründung
   - Details zur OAuth-Proxy-Fassade, die diese Clients vor den Entra-ID-Eigenheiten (kein RFC 8707, kein DCR/CIMD) abschirmt, siehe unten

## Komponente: OAuth-Proxy-Fassade (`src/McpServer/Auth/OAuthProxy.cs`)

Der MCP-Autorisierungs-Standard (RFC 8707/9728) verlangt, dass Clients einen `resource`-Parameter mit der kanonischen MCP-Server-URL an `/authorize` und `/token` senden. **Entra ID implementiert RFC 8707 nicht** und lehnt jeden `resource`-Wert, der nicht die eigene App-ID-URI ist, mit `AADSTS9010010` ab. Claude sendet `resource` (ChatGPT nicht) – ohne Fassade schlägt der Verbindungsaufbau für Claude deshalb fehl.

Der MCP-Server serviert deshalb eine eigene, dünne Authorization-Server-Fassade auf seiner eigenen Origin (Endpunkte per `app.MapOAuthProxyEndpoints()` in `Program.cs` registriert):

```text
GET  /.well-known/oauth-authorization-server   -> synthetisiertes RFC-8414-Dokument (issuer/jwks_uri
                                                    von Entra übernommen, authorization_endpoint/
                                                    token_endpoint zeigen auf sich selbst)
GET  /authorize   -> entfernt "resource" aus der Query, 302-Redirect zu Entras echtem /authorize
GET  /.well-known/oauth-protected-resource      -> RFC-9728-Dokument, "resource" = eigene /mcp-URL,
                                                    "authorization_servers" = eigene Origin (nicht Entra direkt)
POST /token       -> entfernt "resource" aus dem Form-Body, leitet serverseitig an Entras echtes
                      /token weiter, gibt Status/Body 1:1 zurück (inkl. Fehler-JSON)
```

Die Fassade terminiert PKCE **nicht** selbst – `code_challenge`/`code_verifier` laufen unverändert Ende-zu-Ende zwischen Client und Entra durch, es wird kein eigenes Secret/State gehalten oder geloggt. Ausführliche Herleitung inkl. der drei durchlaufenen Entra-Fallstricke (AADSTS7000218, AADSTS9002325, AADSTS65001) siehe [`docs/DEPLOYMENT.md`](DEPLOYMENT.md), Abschnitt "OAuth-Proxy-Fassade".

## MCP-Tool-Katalog (`src/McpServer/Tools/`)

Fünf `[McpServerToolType]`-Klassen, registriert in `Program.cs` über `.AddMcpServer().WithHttpTransport().WithTools<...>()`:

| Klasse | Tool-Methoden | Zweck |
|---|---|---|
| `TasksTool` | `GetTasksAppOnly()`, `GetTasksDelegated(incomingUserAccessToken)` | Demo-/Referenz-Endpoint für beide Auth-Flows (App-only vs. delegated/OBO) |
| `M365StatusTool` | `GetServiceHealthOverview()`, `GetServiceHealthDetail(serviceHealthId, ...)`, `GetDegradedServiceHealthDetails()`, `GetServiceHealthIssues()` | Office 365 Service Health (tenant-weit, app-only) |
| `M365MessagesTool` | `GetMessageCenterMessages(odataFilter?)` | Message-Center-Nachrichten (tenant-weit, app-only) |
| `M365AdoptionTool` | `GetOffice365ActiveUserDetail(period?)`, `GetM365AppUserDetail(period?)` | Die zwei am häufigsten gebrauchten Adoption-Reports als eigene, stark-typisierte Tools |
| `M365AdoptionCatalogTool` | `ListAdoptionReports(category?)`, `GetAdoptionReport(reportName, period?, date?, serviceArea?, appId?)` | Generischer Zugriff auf **alle 92** Adoption-/Usage-Report-Endpunkte, siehe unten |

Jede Tool-Methode ist mit `[McpServerTool, Description("...")]` annotiert; Parameter tragen eigene `[Description("...")]`-Attribute, die vom MCP-Protokoll als Tool-Schema an den Client (LLM) exponiert werden. Neue Tools folgen exakt diesem Muster – siehe [`AGENTS.md`](../AGENTS.md), Abschnitt "Neues MCP-Tool hinzufügen", für die Schritt-für-Schritt-Anleitung.

## Offene Architektur-Fragen (bitte vor dem produktiven Rollout klären)

Diese Punkte sind im Scaffold mit sinnvollen Standardwerten vorbelegt, sollten aber bewusst bestätigt/angepasst werden:

1. **Wer ruft den MCP Server auf?** Ein Chat-Client (z. B. Claude Desktop/Code) direkt per stdio, oder ein Frontend/Gateway per HTTP/SSE? → Das Scaffold nutzt HTTP/SSE, passend zu "Deployment auf App Service".
2. **Woher kommt bei delegated Calls das initiale User-Token?** Meldet sich der Nutzer direkt beim MCP Server an (eigener Auth-Code-Flow), oder wird ein bereits vorhandenes Token (z. B. von einem vorgelagerten Gateway) durchgereicht? Das bestimmt, ob der MCP Server selbst OIDC-Sign-in braucht oder nur OBO gegen ein eingehendes Bearer-Token macht.
3. **Multi-Tenant oder Single-Tenant?** Aktuell `AzureADMyOrg` (Single-Tenant). Für SaaS-Szenarien wäre `AzureADMultipleOrgs` nötig – ändert Consent- und Sign-in-Audience-Konfiguration.
4. **Zertifikat statt Client Secret?** Für Produktion empfohlen (weniger Angriffsfläche, kein Ablaufdatum-Vergessen). Im MVP der Einfachheit halber ein Secret aus dem Key Vault.
5. **Netzwerksegmentierung:** Sollen API Server und MCP Server im selben App Service Plan / VNet laufen, oder getrennt mit Private Endpoints? MVP: gemeinsamer Plan, kein VNet.
6. **Rollenmodell:** Reicht eine einzelne App Role (`Tasks.ReadWrite.All`), oder werden feingranulare Rollen (Read/Write getrennt, pro Ressourcentyp) benötigt?
7. **Skalierung/Persistenz:** Der API Server im Scaffold ist zustandslos ohne Datenbank (Demo-Endpoint). Für echte Daten wird zusätzliche Infrastruktur (z. B. Azure SQL/Cosmos DB) benötigt – bewusst nicht Teil dieses MVP-Scopes.

## Sequenz: Delegated Call (OBO)

1. Nutzer meldet sich beim MCP Server per Auth-Code-Flow an → Access Token mit `scp=Tasks.ReadWrite`, Audience = `mcp-server`
2. MCP Server tauscht dieses Token per **On-Behalf-Of** gegen ein neues Token mit Audience = `api-server` (`scp=Tasks.ReadWrite`)
3. MCP Server ruft API Server mit diesem Token auf → API Server sieht `scp`-Claim, lässt delegierten Zugriff zu, kennt den Nutzer (`oid`/`upn`)

## Sequenz: App-only Call (Client Credentials)

1. MCP Server holt sich per Client Credentials Flow ein Token mit `roles=[Tasks.ReadWrite.All]`, Audience = `api-server`
2. MCP Server ruft API Server auf → API Server sieht `roles`-Claim statt `scp`, lässt App-only-Zugriff zu (kein Nutzerkontext)

## Erweiterung: Office 365 Status, Message Center, Adoption (Tenant-weit)

Zusätzlich zu den eigenen `api-server`-Endpunkten ruft der MCP Server für drei tenant-weite Auskünfte
direkt die **Microsoft Graph Service-Communications- und Reports-APIs** auf (App-only, da diese Daten
i. d. R. keinem einzelnen Nutzer, sondern dem Tenant als Ganzes zugeordnet sind):

| Tool | Graph-Endpunkt | Benötigte Application Permission |
|---|---|---|
| `GetServiceHealthOverview` / `GetServiceHealthIssues` (**Status**) | `/admin/serviceAnnouncement/healthOverviews`, `/issues` | `ServiceHealth.Read.All` |
| `GetMessageCenterMessages` (**Nachrichten**) | `/admin/serviceAnnouncement/messages` | `ServiceMessage.Read.All` |
| `GetOffice365ActiveUserDetail` / `GetM365AppUserDetail` (**Adoption**) | `/reports/getOffice365ActiveUserDetail`, `/getM365AppUserDetail` | `Reports.Read.All` |

Alle drei Permissions werden auf der `mcp-server`-App-Registrierung vergeben und benötigen **Admin Consent**
(einmalig durch einen Global-/Application-Administrator, wird von `Set-EntraIdApps.ps1` bzw. dem Terraform-Modul
`terraform/modules/entra-id` automatisiert versucht, siehe `docs/ENTRA-ID-SETUP.md`).

**Antwortformat der Reports-Endpunkte:** Die v1.0-Reports-API liefert grundsätzlich nur CSV. Der
**Beta**-Endpunkt unterstützt für `getOffice365ActiveUserDetail` offiziell `?$format=application/json`
(200 OK mit JSON-Body). Für `getM365AppUserDetail` ist das laut Praxisberichten nicht durchgängig
zuverlässig. Deshalb kapselt `Services/GraphReportsClient.cs` beides in einem Wrapper, der **immer JSON
zurückgibt**:
1. Beta-Endpunkt mit `$format=application/json` versuchen
2. Falls stattdessen ein 302-Redirect auf eine CSV-Download-URL oder direkt CSV/octet-stream zurückkommt,
   automatisch herunterladen und über `Utils/CsvJsonConverter.cs` in JSON konvertieren

Da Beta-Endpunkte laut Microsoft "subject to change" und nicht für Produktion supported sind, macht dieser
Wrapper das Verhalten der MCP-Tools davon unabhängig – Ausfall/Änderung des Beta-JSON-Formats führt nur zum
CSV-Fallback, nicht zu einem Fehler beim Aufrufer.

### Vollständiger Report-Katalog (92 Endpunkte)

Über die zwei oben genannten Tools hinaus deckt `Reporting/ReportCatalog.cs` **alle 92 Adoption-/Usage-Report-
Endpunkte** ab, die `reportRoot` im Microsoft-Graph-Beta-Namespace bereitstellt – u. a. Microsoft 365 Copilot
Usage, Forms, Teams (User/Device/Team Activity), Outlook (Activity/App Usage/Mailbox Usage), Microsoft 365
Activations/Active Users/Apps Usage/Browser Usage/Groups Activity, Graph API Usage, OneDrive, SharePoint,
Skype for Business (Activity/Device Usage/Organizer/Participant/Peer-to-Peer) und Viva Engage.

**Design-Entscheidung:** Statt 92 nahezu identischer MCP-Tool-Methoden zu generieren, gibt es zwei generische
Tools:
- `ListAdoptionReports(category?)` – listet alle Endpunkte mit Kategorie, Beschreibung und den für den
  jeweiligen Endpunkt gültigen Parametern
- `GetAdoptionReport(reportName, period?, date?, serviceArea?, appId?)` – ruft einen beliebigen Endpunkt
  korrekt parametrisiert ab

Jeder Katalogeintrag kennt seinen `ReportParamMode` (siehe `Reporting/ReportCatalog.cs`), der die von
Microsoft dokumentierten Parameterregeln abbildet:

| ParamMode | Bedeutung | Beispiel-Endpunkte |
|---|---|---|
| `PeriodOrDate` | genau **einer** von `period` (D7/D30/D90/D180) ODER `date` (YYYY-MM-DD) | alle `...UserDetail`/`...Detail`-Reports |
| `PeriodOnly` | nur `period` | alle `...Counts`-Zeitreihen-Reports |
| `None` | kein Parameter, aktueller Zustand | `getOffice365Activation*` |
| `Special` | eigene optionale Parameter (`period`, `serviceArea`, `appId`) | `getApiUsage` |

`GraphReportsClient.GetCatalogReportAsJsonAsync(...)` validiert diese Regeln zur Laufzeit (z. B. Fehler, wenn
sowohl `period` als auch `date` angegeben werden, oder ein ungültiger `period`-Wert übergeben wird) und baut
daraus den korrekten OData-Funktionsaufruf, bevor der gemeinsame JSON/CSV-Wrapper greift.

Falls stattdessen lieber 92 einzelne, stark typisierte MCP-Tool-Methoden gewünscht sind (z. B. für IDE-
Autovervollständigung pro Endpunkt), kann das aus diesem Katalog heraus generiert werden – bei Bedarf einfach
Bescheid geben.
