# Microsoft Foundry als OAuth-Client (Self-Service)

Stand 2026-10-17. **Nicht mit einem echten Foundry-Projekt getestet** – die Feldwerte folgen der Microsoft-Doku (Quellen unten) und dem vorhandenen Copilot-Studio-Muster.

## Empfehlung

**Foundry spricht direkt mit Entra und dem .NET-MCP-Server, nicht mit dem FastMCP-Gateway.** Die App dafür legt der Anwender selbst per Skript an ([`scripts/New-FoundryMcpClient.ps1`](../scripts/New-FoundryMcpClient.ps1)); sie wird bewusst **nicht** über Terraform verwaltet.

Begründung:
- Foundry („Custom MCP“ → „OAuth Identity Passthrough“) kennt weder DCR noch CIMD. Es verlangt eine vorab angelegte Entra-App mit Client-ID, Client-Secret (laut Doku „optional, abhängig von der OAuth-App“), Auth-, Token- und Refresh-URL und Scopes ([Doku](https://learn.microsoft.com/azure/foundry/agents/how-to/mcp-authentication#oauth-identity-passthrough)).
- FastMCP 4.1.0 kann **keinen Confidential Client mit eigenem Secret** registrieren: `register_client` speichert jeden Client als public (`token_endpoint_auth_method="none"`, `client_secret=None`; Code `fastmcp/server/auth/oauth_proxy/proxy.py`, bestätigt durch den Live-Test: DCR liefert `client_secret: None`). Das Secret der Gateway-Upstream-App darf nie an einen Downstream-Client gehen.
- Foundry trägt Auth-/Token-URL von Entra selbst ein und schickt das Entra-Access-Token (Audience = MCP-App) an den MCP-Endpunkt. Genau das validiert der .NET-Server unverändert. Das Gateway würde stattdessen ein FastMCP-JWT erwarten und das Entra-Token ablehnen.
- Die bestehende .NET-OAuth-Fassade und die Copilot-Studio-App bleiben unberührt.

## Was das Skript anlegt
`./scripts/New-FoundryMcpClient.ps1` (PowerShell 7, Azure CLI, angemeldet im richtigen Tenant):

| Objekt | Einstellung |
|---|---|
| App-Registrierung `foundry-mcp-client-<env>` | Single-Tenant, Plattform **Web** (Confidential Client) |
| Berechtigung | nur delegiert `Mcp.Access` auf die MCP-App `api://mcp-server-<env>` (kein Graph-, kein ARM-Recht) |
| Service Principal | im Tenant angelegt |
| Client-Secret | 6 Monate (`-SecretValidityMonths`), Wert **einmalig** auf der Konsole, nie in eine Datei geschrieben |
| Admin-Consent | nur mit `-AdminConsent`; sonst stimmt jeder Nutzer beim ersten Login selbst zu |

Nichts Bestehendes wird verändert oder gelöscht. Trockenlauf ohne Änderungen: `./scripts/New-FoundryMcpClient.ps1 -WhatIf`.

## Ablauf
1. App anlegen: `./scripts/New-FoundryMcpClient.ps1 -Environment dev` (Ausgabe mit allen Werten sicher ablegen; das Secret wird nicht erneut angezeigt).
2. Foundry-Portal → Projekt → **Tools → Connect a tool → Custom → Model Context Protocol (MCP) → Create**, Felder wie unten eintragen, **Connect**.
3. Foundry zeigt jetzt die **Redirect URL** (vorher unbekannt). Kopieren und eintragen:
   `./scripts/New-FoundryMcpClient.ps1 -AppId <Client-ID> -RedirectUri "<Redirect URL aus Foundry>"`
   (Alternativ im Entra-Portal: App → Authentication → Add a platform/redirect URI → **Web**.)
4. Zurück in Foundry: **Close → Save**. Beim ersten Tool-Aufruf liefert Foundry einen Consent-Link (`oauth_consent_request`); der Nutzer meldet sich an und stimmt zu. Der Consent gilt pro Tool-Verbindung und Projekt.

## Feld für Feld
Werte für Dev; `<tenantId>` = Verzeichnis-(Mandanten-)ID, das Skript gibt alles fertig aus.

| Foundry-Feld | Wert |
|---|---|
| Name | frei, eindeutig (z. B. `m365admin-mcp`) |
| Remote MCP Server endpoint | `https://entramcp-dev-mcp.azurewebsites.net/mcp` (**.NET-Server**, nicht das Gateway) |
| Authentication | `OAuth Identity Passthrough` |
| Client ID | Ausgabe des Skripts (`appId` der Foundry-App) |
| Client secret | Ausgabe des Skripts (einmalig sichtbar) |
| Auth URL | `https://login.microsoftonline.com/<tenantId>/oauth2/v2.0/authorize` |
| Token URL | `https://login.microsoftonline.com/<tenantId>/oauth2/v2.0/token` |
| Refresh URL | `https://login.microsoftonline.com/<tenantId>/oauth2/v2.0/token` |
| Scopes | `api://mcp-server-dev/Mcp.Access offline_access` (durch **ein** Leerzeichen getrennt) |
| Redirect URL | wird von Foundry nach „Connect“ angezeigt → mit `-AppId … -RedirectUri …` eintragen |

## Hinweise und Grenzen
- Das Foundry-Projekt muss im selben Tenant liegen wie die MCP-App.
- Der Server läuft auf Dev (F1): Der erste Aufruf nach Leerlauf kann 10–30 s dauern; Foundry-Timeouts können dabei auslaufen (nicht getestet).
- Secret regelmäßig rotieren: `az ad app credential reset --id <Client-ID> --append …` und in Foundry aktualisieren; alte Secrets im Entra-Portal löschen.
- Entfernen: `az ad app delete --id <Client-ID>` (löscht App und Service Principal; nur diese Foundry-App).
- Nicht abgedeckt: Foundry-Verhalten bei App-only-Tools (der Server nutzt weiterhin seine Managed Identity, nicht den Nutzer) und kein Test mit einem echten Foundry-Projekt.

## Quellen
- Foundry – OAuth Identity Passthrough: https://learn.microsoft.com/azure/foundry/agents/how-to/mcp-authentication
- Beispiel mit Entra-App und Redirect-URL: https://learn.microsoft.com/azure/azure-functions/functions-mcp-foundry-tools
- Repo: ADR-0004/0005 (OAuth-Fassade, Pre-Registration), `terraform/modules/entra-id/main.tf` (Copilot-Studio-Muster `copilot_mcp_oauth_client`)
