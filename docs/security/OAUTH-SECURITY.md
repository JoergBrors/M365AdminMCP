# OAuth-Sicherheit

## Zuständigkeiten
| Komponente | Rolle | Token |
|---|---|---|
| Python-Gateway (`src/OAuthGateway`) | Authorization Server für MCP-Clients (DCR, CIMD, Consent), Resource Server für Clients | stellt FastMCP-JWT (HS256) aus, hält das Entra-Token verschlüsselt |
| Entra ID | Identitätsquelle | Access-Token, Audience = MCP-App |
| McpServer | Resource Server für Entra-Tokens; zusätzlich die OAuth-Fassade (ADR-0004) | validiert Entra-JWT |

Zwei Wege zum selben MCP-Server existieren bewusst parallel: (1) Clients über das Gateway, (2) Clients direkt über die .NET-Fassade (ChatGPT/Claude/Copilot mit vorregistrierten Apps, Foundry). Pro Client darf nur **ein** Weg konfiguriert sein.

## Belegte Kontrollen
| Kontrolle | Beleg |
|---|---|
| PKCE S256 erzwungen, falscher Verifier abgewiesen | Test `test_pkce_wrong_verifier_rejected`; Metadaten `code_challenge_methods_supported: ["S256"]` |
| Redirect-URI nur registriert, `https://evil…` abgewiesen | Test `test_redirect_manipulation_rejected`; live `400` |
| Consent Pflicht, CSRF, Browser-Bindung | Tests `…consent…`; `require_authorization_consent=True` (`gateway/app.py`) |
| Downstream-Client bekommt kein Upstream-Secret | Test `test_dcr_issues_separate_identities_and_no_upstream_secret`; live `client_secret: None` |
| Client erhält nie das Entra-Token | Test `test_client_receives_fastmcp_jwt_not_entra_token` |
| Audience des Entra-Tokens = MCP-App (nicht Gateway-App) | `gateway/app.py` setzt `verifier.audience = [MCP_APP_CLIENT_ID, MCP_APP_ID_URI]`; Test `test_token_audience_is_the_mcp_app_not_the_gateway_app`; live bestätigt (`tools/list`, `tools/call`) |
| FastMCP-JWT erreicht das Backend nicht | `_UpstreamBearer` setzt das Entra-Token (`gateway/server.py`) |
| `resource`-Parameter an Entra nicht weitergegeben | `forward_resource=False` (Entra lehnt RFC 8707 ab, AADSTS9010010) |
| Issuer ohne abschließenden `/` (RFC 8414/9207) | `gateway/issuer.py`, Test `test_issuer_has_no_trailing_slash_everywhere` |
| Codes/State im Zugriffsprotokoll maskiert | `MaskSensitiveQuery`, Test `test_access_log_masks_oauth_codes_state_and_tokens` |

## Nicht belegt / offen
- **ChatGPT** bricht nach dem Rücksprung mit „issuer does not match the expected issuer“ ab, obwohl `iss` in Redirect (Browser-Beleg) und Metadaten übereinstimmen; Hypothese: Fehler im Codex-/ChatGPT-Client (openai/codex#31573). Mit `authorization_response_iss_parameter_supported=false` unverändert. Nicht gelöst.
- **Claude**: Anmeldung läuft bis zur Consent-Seite (CIMD); vollständiger Durchlauf nicht berichtet.
- **Copilot Studio, M365 Copilot, Foundry** nicht gegen das Gateway getestet; Foundry nutzt den .NET-Weg ([FOUNDRY-OAUTH.md](../FOUNDRY-OAUTH.md)).
- Token-Replay, Refresh-Rotation, Widerruf, Registrierungs-TTL: nicht geprüft.
- Confidential-DCR mit Secret wird von FastMCP 4.1.0 nicht unterstützt.
- Offene DCR ohne Rate Limit (F-011).
