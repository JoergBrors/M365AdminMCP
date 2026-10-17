# FastMCP-OAuth-Gateway: Machbarkeit (aktualisiert nach PoC)

> **Historischer Bericht (PoC-Stand, `poc/` ist inzwischen entfernt und durch `src/OAuthGateway` ersetzt).** Den aktuellen Stand und alle offenen Punkte enthält [STATUS.md](STATUS.md).

Stand: 2026-10-17 · Branch `feature/fastmcp-gateway-poc` · Status: **bedingtes GO für einen Dev-Tenant-Test, noch kein GO für Produktion** (siehe §9)

Alle Aussagen sind entweder durch einen Test im PoC (`poc/fastmcp-gateway/tests` (inzwischen ersetzt durch `src/OAuthGateway/tests`), 21 Tests grün), durch Quellcode-Lektüre (Pfad genannt) oder durch eine Microsoft/FastMCP-Doku-URL belegt. Alles andere ist ausdrücklich als **nicht verifiziert** markiert. Die erste Fassung dieses Audits (Chat-Bericht) wird in drei Punkten korrigiert: siehe §8.

## 1. Was der PoC beweist und was nicht

| Aussage | Status | Beleg |
|---|---|---|
| FastMCP 4.1.0 (`pip`-Stand 2026-10-17) enthält `OAuthProxy`, `AzureProvider`, DCR, CIMD, Consent, `client_storage` | verifiziert | `pip install fastmcp` → 4.1.0; Signatur von `OAuthProxy.__init__` |
| DCR stellt je Registrierung eine **eigene** `client_id` aus, **ohne** Secret; zentrale Upstream-Credentials werden nicht veröffentlicht | getestet | `test_dcr_issues_separate_identities_and_no_upstream_secret`; Code `oauth_proxy/proxy.py` `register_client` (`client_secret=None`, `token_endpoint_auth_method="none"`) |
| Upstream (IdP) sieht nur die zentrale `client_id` und die feste Callback-URL `<gateway>/auth/callback`, nie die Downstream-Redirect-URI | getestet | `test_upstream_uses_central_client_and_fixed_callback` |
| Consent ist Pflicht, Browser-Bindung und CSRF greifen | getestet | `test_authorize_requires_consent_before_upstream`, `test_consent_forged_csrf_rejected`, `test_consent_from_other_browser_rejected` |
| Manipulierte/nicht registrierte Redirect-URI, falsche PKCE-Verifier werden abgelehnt | getestet | `test_redirect_manipulation_rejected`, `test_pkce_wrong_verifier_rejected` |
| Client erhält ein FastMCP-JWT (HS256), nie das Entra-Token | getestet | `test_client_receives_fastmcp_jwt_not_entra_token` |
| Gateway tauscht beim Backend-Aufruf das FastMCP-JWT gegen das **Upstream-Entra-Token** und reicht dieses weiter (Modell B) | getestet gegen Mock-IdP | `test_initialize_list_call_through_gateway` (Backend sieht `sub=user-1`); Code `fastmcp/server/auth/oauth_proxy/proxy.py` `load_access_token` |
| `initialize`, `tools/list`, `tools/call`, Fehlerweitergabe, Abbruch (Client-Timeout, Gateway bleibt gesund), PUT/PATCH → 404/405 | getestet | `tests/test_mcp_e2e.py` |
| Unauthentifizierte/Garbage-Tokens am Gateway → 401 mit `resource_metadata` | getestet | `test_unauthenticated_and_garbage_tokens_rejected_at_gateway`, `test_discovery_documents` |
| Backend-Regeln (falsche Audience/Issuer/Signatur, abgelaufen, falscher Scope, kein Token, `X-User`-Header) → 401 | getestet, **aber gegen einen Python-Stub**, nicht gegen .NET | `test_backend_rejects_*` |
| **Das echte .NET-Backend akzeptiert das weitergereichte Entra-Token** | **NICHT bewiesen** | siehe §3 |
| Verhalten gegen **echtes Entra** (Scope `api://mcp-server-*/Mcp.Access`, v2-Tokens, `AzureProvider`) | **NICHT bewiesen** | Mock-IdP ersetzt Entra |
| Streaming langer SSE-Antworten, GET-Stream, DELETE im stateless-Modus | **nicht geprüft** (DELETE liefert 400, GET-Stream bewusst übersprungen) | `test_http_methods` |
| Refresh-Token-Rotation, Neustart-Persistenz der Registrierungen | **nicht geprüft** | – |

## 2. Gateway-zu-.NET-Authentifizierung (Sicherheitsbewertung)

**Entwurf (Modell B):** Der Client hält nur das FastMCP-JWT. Das Gateway verifiziert es, lädt das verschlüsselte Upstream-Entra-Token (Audience = MCP-App, Scope `Mcp.Access`, vom Nutzer per Authorization Code + PKCE erworben) und sendet genau dieses als `Authorization: Bearer` an `/mcp` (`poc/fastmcp-gateway/gateway/server.py`, `_UpstreamBearer`). Das .NET-Backend validiert es mit unveränderter Logik (`src/McpServer/Program.cs:44-57`).

Vorteile gegenüber Modell A (eigene Backend-Identität des Gateways):
- Keine Änderung an der .NET-JWT-Validierung, keine neue App-Rolle, keine neue Managed-Identity-Berechtigung.
- Der Nutzerkontext bleibt im Token (`sub`, `scp`), also Audit-Fähigkeit im Backend.
- Kein neues Secret zwischen Gateway und Backend, kein Header-Vertrauen.

Risiken/Grenzen:
- Das Backend bleibt direkt per Entra-Token erreichbar (öffentlicher Hostname). Ein Nutzer mit gültigem Token kann das Gateway umgehen; das ist heute schon so und wird durch das Gateway **nicht** schlechter, aber auch nicht besser. Echte Isolation braucht Zugriffsbeschränkung (IP-/Service-Tag-Restriktion auf den Gateway-Ausgang) oder Private Networking (kostenpflichtiger Plan, nicht F1).
- Das Gateway ist ein Hochwert-Ziel: es speichert Entra-Tokens (verschlüsselt, Schlüssel aus `jwt_signing_key`). Der Signierschlüssel gehört in Key Vault.
- `forward_incoming_headers` ist für diese Architektur irrelevant (Transport setzt `Authorization` selbst); trotzdem darf das FastMCP-JWT nie zum Backend gelangen. Test: `test_backend_receives_entra_token_not_fastmcp_jwt` beweist nur, dass das Gateway-JWT am Gateway nicht ohne Token akzeptiert wird; ein expliziter Backend-seitiger Negativtest gegen das FastMCP-JWT fehlt noch (Vorschlag im Folge-PR).
- Delegierte/OBO-Funktionen: `ApiTokenService.GetOnBehalfOfTokenAsync` wirft in Produktion ohnehin (`UseManagedIdentity=true`, `src/McpServer/Auth/ApiTokenService.cs:75-78`, `terraform/modules/infra/main.tf:188`). Die vorhandenen Tools laufen also schon heute app-only über die Managed Identity des MCP-Servers. Das Gateway ändert daran nichts. Kein delegiertes Weiterreichen an den ApiServer wurde geändert oder getestet.

## 3. Warum der .NET-Teil nicht lokal bewiesen werden konnte

`src/OAuthGateway/tools/dotnet_leg.py` startet den **unveränderten** `src/McpServer` mit reiner Umgebungs-Konfiguration gegen einen Mock-Issuer. Ergebnis (Log des echten Servers):
- Signatur und Audience wurden erfolgreich geprüft (`IDX10239`, `IDX10234`), Zugriffe mit falscher Audience/Issuer/Signatur, abgelaufen oder ohne Token wurden korrekt abgelehnt.
- Das gültige Token wurde trotzdem abgelehnt: Microsoft.Identity.Web verlangt HTTPS-Metadaten und prüft den Issuer gegen bekannte Microsoft-Autoritäten (`IDX40001`). Ein lokaler Mock-IdP kann das nicht erfüllen, ohne den Produktionscode zu ändern. Das wäre eine Aufweichung und ist ausgeschlossen.

Folge: Der Beweis, dass das .NET-Backend das vom Gateway weitergereichte Token akzeptiert, **muss in einem Dev-Tenant** erbracht werden (§9, Gate G1).

## 4. APIM (separat bewertet)

Quellen: [Gateways-Überblick](https://learn.microsoft.com/en-us/azure/api-management/api-management-gateways-overview), [MCP-Überblick](https://learn.microsoft.com/en-us/azure/api-management/mcp-server-overview), [Tier-Matrix](https://learn.microsoft.com/en-us/azure/api-management/api-management-features).

- **Native MCP-Funktionen** (Pass-through-MCP-Server, REST-als-MCP): Consumption ❌; verfügbar in Classic und v2.
- **APIM Consumption als generischer HTTP-Reverse-Proxy** ist grundsätzlich möglich, mit harten Grenzen: **Gesamtdauer pro Request max. 30 s**, gepufferte Payload max. 2 MiB, kein WebSocket-Pass-through, kein Private Endpoint, keine VNet-Integration, keine Request-Logs in Log Analytics (nur Application Insights/Metrics), `rate-limit-by-key`/`quota-by-key` nicht verfügbar.
- Das 30-s-Limit ist für MCP-Streams mit langlebigen Antworten (SSE) ein Ausschlusskriterium für Tool-Aufrufe über 30 s; ob die vorhandenen Tools darunter bleiben, wurde nicht gemessen.
- APIM ersetzt weder OAuthProxy noch DCR noch den Authorization Server. Es bringt für das Ziel (automatische Client-Registrierung) **keinen** Nutzen.
- Nicht verifiziert: Preise (die Preisseite lieferte keine Zahlen), Streaming-Puffer-Verhalten im Detail.

**Empfehlung:** APIM weglassen. Falls später gewünscht, nur v2 (Standard v2 für Private Endpoint/VNet) oder Developer (ohne SLA), nicht Consumption.

## 5. Kostenvergleich Python-Hosting (Preise nicht verifiziert)

Die offiziellen Preisseiten lieferten in diesem Lauf keine USD-Zahlen. Es werden daher keine Preise genannt; nachprüfbar sind nur die Struktur und die freien Kontingente.

| Variante | Struktur | Offene Punkte |
|---|---|---|
| A: Python-Web-App im **bestehenden F1-Plan** | kein Aufpreis | F1 hat CPU-Minuten-Kontingent, kein Always On, Python-Linux-Unterstützung auf F1 und Quota-Passung **nicht verifiziert**; Cold Start bricht Sign-in-Flows, lokaler Disk-Store verliert Registrierungen ohne gemeinsamen Speicher; lokaler Disk-Store ist auf dem App Service nicht persistent genug |
| B: **Container Apps Consumption** | Abrechnung pro vCPU-s/GiB-s + Requests; freie Monatskontingente: 180.000 vCPU-s, 360.000 GiB-s, 2 Mio. Requests; Scale-to-Zero ([Preisseite](https://azure.microsoft.com/en-us/pricing/details/container-apps/)) | Bei Dauerbetrieb 0,25 vCPU sind ≈ 648.000 vCPU-s/Monat (eigene Rechnung, 30 Tage) → über Freikontingent; bei Scale-to-Zero mit seltener Nutzung plausibel im Freikontingent; Cold Start nicht gemessen; Container-Image-Build/Registry nötig |
| C: **eigener bezahlter App-Service-Plan** (z. B. Basic) | feste Monatskosten pro Instanz ([Plan-Doku](https://learn.microsoft.com/en-us/azure/app-service/overview-hosting-plans)) | Always On möglich; dasselbe Terraform-Muster wie bisher (`azurerm_linux_web_app`); private Netzwerk-Features erst in höheren Tiers (konkrete Mindest-SKU nicht verifiziert) |
| Persistenz | FastMCP-`client_storage`: Standard Disk; für Produktion gemeinsamer Store (Redis o. ä.) ([Doku](https://gofastmcp.com/servers/auth/oauth-proxy)) | Redis-Preis nicht verifiziert; ob die Registrierungen dank DCR-Neuanmeldung der Clients verzichtbar sind, wurde nicht getestet |

Pragmatische Lesart ohne Preise: B (bei seltener Nutzung) oder C (bei Bedarf an Always On/Isolation). A nur als Experiment.

## 6. Terraform-Plan (nur Entwurf, nichts angewendet)

Bestehender Weg bleibt: `scripts/Invoke-TerraformPlan.ps1` / `…Apply.ps1`, derselbe State (`entra-mcp-mvp.tfstate`), derselbe Workflow `deploy.yml`.

Neue Bausteine (Modul `terraform/modules/gateway`, im Root `terraform/main.tf` eingebunden, hinter einer Variable `enable_gateway = false`):

| Ressource | Zweck | Abhängigkeit |
|---|---|---|
| `azuread_application.gateway` (`web{}`, Redirect `https://<gw-host>/auth/callback`, `required_resource_access` → MCP-App-Scope `Mcp.Access`) | zentrale Upstream-Registrierung (Confidential Client) | `azuread_application.mcp` (Output der Entra-Module) |
| `azuread_service_principal.gateway`, `azuread_application_password.gateway` | Secret für Upstream-Token-Austausch | – |
| ggf. `azuread_service_principal_delegated_permission_grant` | Admin-Consent Gateway → `Mcp.Access` | benötigt für den CI-SP evtl. zusätzlich `DelegatedPermissionGrant.ReadWrite.All` (**nicht verifiziert**, ob die bestehenden Client-Apps Grants per Terraform nutzen; ohne Grant gilt User-Consent) |
| `azurerm_key_vault_secret.gateway_client_secret`, `…gateway_jwt_signing_key` (`random_password`) | Secrets | vorhandener Key Vault |
| Hosting: `azurerm_linux_web_app.gateway` (Python) im Plan **oder** `azurerm_container_app` + `azurerm_container_app_environment` | Gateway | Plan A/B/C aus §5 |
| `azurerm_role_assignment` „Key Vault Secrets User“ für die Identität des Gateways | Key-Vault-Referenzen | vorhandener Key Vault |
| ggf. `azurerm_redis_cache` | Persistenz | – |
| App Settings: `AZURE_TENANT_ID`, `GATEWAY_ENTRA_CLIENT_ID`, `GATEWAY_ENTRA_CLIENT_SECRET` (KV-Ref), `GATEWAY_JWT_SIGNING_KEY` (KV-Ref), `MCP_APP_ID_URI`, `GATEWAY_PUBLIC_URL`, `BACKEND_MCP_URL` | Laufzeit | vorhandene Outputs |

Ausdrücklich **nicht** enthalten: keine Graph-/ARM-Rollen für das Gateway, keine Änderung an `azuread_application.mcp`/`api`/Client-Apps, keine Löschung, keine neue Rolle auf dem ApiServer. Die .NET-OAuth-Fassade (`src/McpServer/Auth/OAuthProxy.cs`) bleibt als Rollback aktiv; Clients bleiben bis zum Umschalten auf den bisherigen Pfaden.

GitHub-OIDC: bestehende Rechte (Contributor auf Ziel-RG, `Application.ReadWrite.All`, RBAC-Admin auf den Key Vault, siehe `AGENTS.md`) decken neue Web-App/Key-Vault-Rollen/App-Registrierung ab. Für Container Apps oder Redis sind keine zusätzlichen Rollen erwartet (nicht geprüft). Python-Artefakt: zusätzlicher Zip-Deploy-Schritt oder Image-Build im bestehenden Workflow.

## 7. Client-Kompatibilität

| Client | Status |
|---|---|
| Copilot Studio | Doku nennt OAuth „Dynamic discovery“ (DCR + Discovery), „Dynamic“, „Manual“ ([Doku](https://learn.microsoft.com/en-us/microsoft-copilot-studio/mcp-add-existing-server-to-agent)). Ob Copilot Studio gegen FastMCP-DCR funktioniert: **nicht getestet** |
| ChatGPT, Claude, M365 Copilot, Foundry | **nicht verifiziert** (keine Doku gelesen, kein Test) |

## 8. Korrekturen am ersten Audit

1. Dort stand, das .NET-Backend könne das Gateway-Token nicht akzeptieren und Modell A sei „die einzige kompatible“ Variante. Das war zu weit gegriffen: FastMCP tauscht das eigene JWT am Gateway gegen das Upstream-Token; ein Weiterreichen an das Backend ist möglich (Modell B, per Mock getestet). Offen bleibt der Beweis gegen echtes Entra und echtes .NET.
2. „Preise/Kosten“ wurden nicht belegt; hier ebenfalls nicht (siehe §5).
3. APIM-Consumption-Limit (30 s Gesamtdauer, 2 MiB Puffer) war im ersten Audit nicht genannt.

## 9. GO/NO-GO (aktualisiert)

**Produktion: NO-GO**, bis alle Gates erfüllt sind. **Dev-Tenant-Test: GO** (Aufwand klein, reversibel, keine Änderung am produktiven OAuth-Endpunkt).

| Gate | Status |
|---|---|
| G1 Echtes Entra-Token (aus Gateway) wird vom echten .NET `/mcp` akzeptiert | offen (§3) |
| G2 Entra-Login über `AzureProvider` mit Scope `Mcp.Access`, `forward_resource=False`, feste Callback-URL | offen |
| G3 DCR/Discovery mit mindestens Copilot Studio und einem der Clients ChatGPT/Claude | offen |
| G4 Streaming/SSE und GET/DELETE im stateless-Modus über das Gateway | offen |
| G5 Persistenz der Registrierungen/Refresh nach Neustart | offen |
| G6 Direktzugriff auf das Backend eingeschränkt oder bewusst akzeptiert und dokumentiert | offen |
| G7 Kosten für gewählte Hosting-Variante mit echten Preisen geprüft | offen |
| Authentifizierungsgrenze Gateway→Backend (Stub-Ebene) | erfüllt |
| DCR/Consent/Redirect/PKCE-Sicherheit (Mock-IdP-Ebene) | erfüllt |

## 10. Empfehlungen für den Implementierungs-PR

1. Dev-Tenant-Test für G1/G2 mit einer temporär registrierten Gateway-App (manuell oder per Terraform-Plan-Review), ohne Änderung an bestehenden Registrierungen.
2. Test gegen das echte .NET: Gateway-Token direkt an `/mcp` muss 401 liefern; Entra-Token über Gateway 200.
3. Gemeinsamen `client_storage` wählen und Neustart-Test ergänzen.
4. Bestehende Schwächen unabhängig vom Gateway beheben: API-Key-Vergleich zeitkonstant oder abschaffen (ADR-0014), CORS einschränken, `AllowedRedirectHosts` verpflichtend.
5. Neue ADR (0017) erst nach bestandenem G1–G6 schreiben.

## 11. Reproduzierbarkeit

```bash
cd src/OAuthGateway
python -m venv .venv && ./.venv/Scripts/python -m pip install -r requirements.txt
./.venv/Scripts/python -m pytest tests -q        # 21 Tests, Mock-IdP + Python-Backend-Stub
./.venv/Scripts/python tools/dotnet_leg.py       # dokumentiert den .NET-Befund aus §3 (erwartet FAIL bei „valid“)
```
