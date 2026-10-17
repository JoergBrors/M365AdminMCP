# FastMCP OAuth Gateway – Entra ID

## Upstream-App `oauth-gateway-<env>` (`terraform/modules/entra-id/gateway.tf`)
- Single-Tenant (`AzureADMyOrg`), Plattform `web{}` (Confidential Client), **eine** Redirect-URI `https://entramcp-dev-oauth-gateway.azurewebsites.net/auth/callback`.
- Delegierte Berechtigung `Mcp.Access` auf die bestehende MCP-App; KEIN Admin-Consent-Grant (CI-Principal hat dafür kein Recht; Nutzer-Consent beim ersten Login). `openid`/`profile` sind nicht Teil des Grants → normaler Nutzer-Consent (**nicht getestet**).
- Keine Graph-Application-Permissions, keine Azure-Rollen, keine Rechte auf den ApiServer.
- Bestehende App-Registrierungen (`mcp`, `api`, ChatGPT, Claude, Copilot) bleiben unverändert (siehe Plan in [FASTMCP-GATEWAY-TERRAFORM.md](FASTMCP-GATEWAY-TERRAFORM.md)).

## Gateway-Parameter (`gateway/app.py`)
`AzureProvider(client_id, client_secret, tenant_id=<GUID>, identifier_uri=api://mcp-server-<env>, required_scopes=["Mcp.Access"], base_url=<öffentliche URL>, jwt_signing_key, client_storage, forward_resource=False, require_authorization_consent=True)`.
- `forward_resource=False`: Entra lehnt RFC-8707-`resource` ab (AADSTS9010010), wie schon in `src/McpServer/Auth/OAuthProxy.cs` dokumentiert.
- Tenant-GUID wird beim Start erzwungen (`common`/`organizations` abgelehnt, Test `test_invalid_setting_rejected`).

## Token-Fluss
Client erhält FastMCP-JWT; das Gateway hält das Entra-Token (Audience = MCP-App) verschlüsselt und reicht es an den .NET-MCP-Server weiter. Dass der **echte** .NET-Server dieses echte Token akzeptiert (G1) und der Entra-Flow über `AzureProvider` funktioniert (G2), ist **nicht nachgewiesen** – es fehlt ein deployter Dev-Test (Freigabe nötig).

## Offene Entra-Punkte (nicht verifiziert)
- Ob die MCP-App v2-Tokens mit `scp=Mcp.Access` für diese Gateway-App ausstellt (Terraform setzt `requested_access_token_version = 2` auf der MCP-App, Verhalten gegen Entra nicht getestet).
- Ob `offline_access`-Refresh-Tokens über den Grant wie erwartet ausgestellt werden.
