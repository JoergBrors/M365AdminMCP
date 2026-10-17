# FastMCP OAuth Gateway – Sicherheit

## Durch automatisierte Tests belegt (`src/OAuthGateway/tests`, 39 Tests, Mock-IdP)
| Risiko | Beleg |
|---|---|
| Redirect-Manipulation / unregistrierter Client | `test_redirect_manipulation_rejected` |
| Consent-Umgehung, CSRF, fremder Browser | `test_authorize_requires_consent_before_upstream`, `test_consent_forged_csrf_rejected`, `test_consent_from_other_browser_rejected` |
| PKCE-Bypass | `test_pkce_wrong_verifier_rejected` |
| Upstream-Secret-Leak an Downstream-Clients | `test_dcr_issues_separate_identities_and_no_upstream_secret` |
| Client sieht nie das Entra-Token | `test_client_receives_fastmcp_jwt_not_entra_token` |
| Unauthentifizierte/Garbage-Tokens | `test_unauthenticated_and_garbage_tokens_rejected_at_gateway` |
| Secrets in `repr`/Fehlern/Logs | `test_repr_does_not_leak_secrets`, `test_config_error_does_not_leak_secret_values`, `test_app_health_endpoints_and_auth_required` |
| Fehlkonfiguration | `test_missing_setting_fails_fast`, `test_invalid_setting_rejected` (HTTPS, GUID, Schlüssellänge, Pfad `/mcp`, Persistenz Pflicht) |
| Backend-Regeln (Audience/Issuer/Signatur/abgelaufen/Scope/`X-User`) | `test_backend_rejects_*` – **gegen Python-Stub, nicht .NET** |

## Nicht belegt
- Backend-Isolation: der .NET-`/mcp` bleibt öffentlich erreichbar. **F1 bietet keine private Netzwerkisolation.** Wer ein gültiges Entra-Token besitzt, kann das Gateway umgehen (wie heute).
- Token-Replay, Refresh-Rotation, Registrierungsraten-Limit: FastMCP bietet kein dokumentiertes Rate-Limit für `/register`; **DCR-Missbrauch** (Massenregistrierung) ist nicht abgesichert. Registrierung allein gewährt keinen Tool-Zugriff (Consent + Entra-Login nötig), kann aber den Table-Store füllen. Empfehlung: Rate-Limit-Middleware vor dem Release, Tabelle überwachen.
- Session-Fixation: Browser-Bindung per Cookie laut FastMCP; nur der Cross-Browser-Fall getestet. Hinweis: lokal unter HTTP warnt FastMCP „non-secure cookies“; in Azure läuft HTTPS.

## Persistenz-Verschlüsselung
`FernetEncryptionWrapper` (Bibliothek `py-key-value-aio`, keine eigene Kryptografie) mit Schlüssel aus Key Vault. Bei eigenem `client_storage` verschlüsselt FastMCP nicht selbst (Code `proxy.py`: Fernet nur im Default-Zweig) – die App-Schicht tut es. Rotation: ein Schlüsselwechsel macht Einträge unlesbar (`raise_on_decryption_error=False` → Clients registrieren sich neu); MultiFernet-Rotation wird vom verwendeten Wrapper-Konstruktor nur über `fernet=` unterstützt, nicht umgesetzt.

## Bestehende Schwächen (dokumentiert, bewusst nicht geändert – keine Änderung am Verhalten bestehender Clients)
- Statischer `X-MCP-API-Key`, nicht zeitkonstanter Vergleich (`src/McpServer/Program.cs:145-164`, ADR-0014).
- CORS `AllowAnyOrigin` (`Program.cs:28-34`).
- Redirect-Allowlist optional (`src/McpServer/Auth/OAuthProxy.cs:235-243`).
- OAuth-Fehler-Bodies werden geloggt (`OAuthProxy.cs:133`, enthalten laut Kommentar keine Secrets).
- App-only-MCP-Tools: keine nutzerbezogene Autorisierung gegenüber Graph (alle Nutzer mit `Mcp.Access` sehen dieselben Daten).
Härtung gehört in einen eigenen PR mit Regressionstests der Bestandsclients.

## Supply Chain / Artefakt
`requirements.txt` fixiert alle Transitiven (FastMCP 4.1.0); `pip-audit`: *No known vulnerabilities found* (Lauf vom 2026-10-17, lokal). Artefakt per Allowlist (`build_artifact.sh`: `gateway/{__init__,app,config,server}.py`, `requirements.txt`, `VERSION`, Linux-Wheels), 37 MB, Secret-Mustersuche vor dem Packen; CI zusätzlich Gitleaks. Kein Publish-Profil, Deployment über bestehenden OIDC-Login.
