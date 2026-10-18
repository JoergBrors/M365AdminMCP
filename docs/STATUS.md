# Abschlussstand und offene Punkte

Stand **2026-10-17**, Umgebung **Dev** (Subscription BRORS.NET, Resource Group `rg-entramcp-dev`). Dies ist der Einstiegspunkt für „Was ist fertig, was ist belegt, was ist offen?“. Details stehen in den verlinkten Dokumenten.

Kennzeichnung: **belegt** = von mir im Code gelesen, getestet oder live gemessen; **gemeldet** = vom Eigentümer berichtet, nicht von mir gemessen; **ungeprüft** = weder noch.

## 1. Was existiert

| Komponente | Zweck | Status |
|---|---|---|
| `src/ApiServer` (.NET 10) | geschützte API, hält Graph-/ARM-Rechte (Managed Identity) | in Betrieb |
| `src/McpServer` (.NET 10) | MCP-Endpunkt `/mcp` mit **17** lesenden Tools, OAuth-Fassade (ADR-0004) | in Betrieb |
| `src/OAuthGateway` (Python, FastMCP 4.1.0) | OAuth-Gateway: Discovery, DCR, CIMD, Consent; reicht das Entra-Token an den .NET-Server weiter | in Betrieb (Dev, B1), **ADR-0017 akzeptiert für Dev** |
| `terraform/` | einziger Deployment-Weg (Azure + Entra) | in Betrieb; Gateway über `enable_gateway=true` |
| `scripts/New-FoundryMcpClient.ps1` | Self-Service-Entra-App für Microsoft Foundry (nicht Terraform) | vorhanden, `-WhatIf` getestet, App nicht angelegt |
| `.github/workflows/deploy.yml` | Build, Tests, Scans, Plan (PR), Apply + Deploy (`main`) | in Betrieb |

Architektur: [ARCHITECTURE.md](ARCHITECTURE.md), [FASTMCP-GATEWAY-ARCHITECTURE.md](FASTMCP-GATEWAY-ARCHITECTURE.md), [security/SECURITY-ARCHITECTURE.md](security/SECURITY-ARCHITECTURE.md). Entscheidungen: [adr/](adr/README.md) und [adr/IMPLEMENTATION-AUDIT.md](adr/IMPLEMENTATION-AUDIT.md).

## 2. Clients

| Client | Weg | Status |
|---|---|---|
| ChatGPT | Gateway | **belegt**: Gateway-Log zeigt nach dem Rücksprung `POST /token` 200 und `POST /mcp` 200 (2026-10-17 ~17:05) |
| Claude | Gateway | **gemeldet** funktioniert |
| Grok | Gateway | **gemeldet** funktioniert |
| Microsoft Foundry | direkt: OAuth-Passthrough gegen die MCP-Adresse, **nicht** über das Gateway | Vorgabe vom Eigentümer bestätigt; **ungeprüft** gegen ein echtes Foundry-Projekt; Anleitung [FOUNDRY-OAUTH.md](FOUNDRY-OAUTH.md) |
| Copilot Studio, M365 Copilot | Fassade (vorregistriert) bzw. „Dynamic discovery“ am Gateway | **ungeprüft** |
| FastMCP-Python-Client | Gateway | **belegt** (`src/OAuthGateway/tools/gateway_e2e.py`: `tools/list` 17 Tools, `tools/call` mit Graph-Daten) |

## 3. Live belegt (2026-10-17)

- Gateway: Entra-Login, Consent, PKCE, DCR, CIMD (URL-`client_id`), `tools/list`, `tools/call` bis Graph; Container-Neustart erhält Registrierungen (Azure Tables).
- Rate Limit Gateway `/register`: 13 Anfragen → 10× `201`, danach `429`; `/healthz` und Discovery unberührt.
- McpServer sendet `Strict-Transport-Security`.
- Container-Log des Gateways: kein unmaskierter `code=`; `code`, `state`, `session_state`, `txn_id` als `***`.
- API-Log: `MCP caller authMethod=entra oid=… tid=… upn=…` (UserContext, nur Audit).
- Tenant: `appRoleAssignmentRequired=true` an der API-App; Daten-Controller akzeptieren nur die App-Rolle.
- Terraform-Plan zeigt keine ungeplanten Löschungen; Azure-Einstellungen des Gateways entsprechen Terraform (keine manuelle Abweichung).

## 4. Umgesetzte Sicherheitsmaßnahmen (Code, getestet)

Siehe [security/SECURITY-FINDINGS.md](security/SECURITY-FINDINGS.md). Kurz: Parametervalidierung der Reports (F-001), NuGet-Patch 10.0.13 (F-002), zeitkonstanter API-Key-Vergleich (F-003), Rate Limits/Request-Limits/HSTS (F-009, F-011 teilweise), Maskierung des Zugriffsprotokolls (F-010), Policy `DataAccess` + Zuweisungspflicht (F-016), `GetTasksDelegated` entfernt (F-006), UserContext (Audit). Tests: `dotnet test` 180, `pytest` 59.

## 5. Entscheidungen

| ID | Stand |
|---|---|
| D-1 Autorisierung | **entschieden**: nur `Mcp.Access`, keine weiteren Rollen; Nutzeridentität nur für Audit |
| D-2 `GetTasksDelegated` | **entschieden und umgesetzt**: entfernt |
| D-3 Netzwerkisolation | **offen** (G6) |
| D-4 `X-MCP-API-Key` abschaffen | **offen** (in Azure nicht gesetzt) |
| D-5 Rate Limits | **umgesetzt** |
| D-6 OAuth-Weg je Client | für Dev gewählt: Gateway für ChatGPT/Claude/Grok, direkter Weg für Foundry; ADR-0017 akzeptiert für Dev mit offenen Gates |

## 6. Offene Punkte

Priorität P0 kritisch … P3 niedrig. „Eigentümer“ = Entscheidung/Freigabe nötig.

| # | Thema | Bezug | Prio | Zuständig | Nächster Schritt |
|---|---|---|---|---|---|
| 1 | **Netzwerkisolation** (G6): API und MCP über öffentliche Hostnamen erreichbar, Gateway umgehbar | F-008, D-3, Q-AZ-1 | P2 | Eigentümer (Kosten) | IP-Restriktion auf den Gateway-Ausgang prüfen; Private Endpoint nur mit Freigabe |
| 2 | **Kosten** (G7): B1-Plan, Storage, ggf. Telemetrie nicht verifiziert | Q-OPS-1 | P2 | Eigentümer | nach einer Woche in Azure Cost Management ablesen |
| 3 | **Persistente Telemetrie/Audit-Logs**: App Insights erhält keine Daten (kein SDK), Container-Logs nur 1 Tag/35 MB | F-017, Q-AZ-2 | P2 | Eigentümer (Kosten, UPN-Datenschutz) | SDK/OpenTelemetry in API, MCP, Gateway anbinden oder bewusst verzichten |
| 4 | **State-Account**: RBAC/Netzwerk von `stgeuwmcpo365dev` nicht geprüft; Secrets liegen im State | F-013, Q-SEC-5 | **P1** | Eigentümer | Rollen und Firewall prüfen |
| 5 | **F-016 Live-Retest**: Nicht zugewiesener Nutzer bekommt kein API-Token – nie getestet | F-016 | P2 | Eigentümer (Testnutzer) | zweiten Testnutzer anlegen, Token-Anforderung versuchen |
| 6 | **Copilot Studio, M365 Copilot** gegen Fassade/Gateway testen | Q-GW-3 | P1 | Eigentümer | echte Clients, Log sichern |
| 7 | **Foundry** gegen ein echtes Projekt testen (App per `New-FoundryMcpClient.ps1`) | FOUNDRY-OAUTH | P2 | Eigentümer | Skript ausführen, Feldwerte eintragen |
| 8 | **G5**: Refresh-Token, Re-Deploy mit bestehenden Registrierungen, Gateway-Neustart mit echten Clients | G5 | P2 | Eigentümer/Entwicklung | Test mit ChatGPT/Claude über einen Neustart |
| 9 | **DCR-Missbrauch**: Gesamtquote/TTL für Registrierungen fehlt (nur Rate Limit je IP) | F-011 | P2 | Entwicklung | Quote und Ablauf einführen |
| 10 | **CORS** `AllowAnyOrigin` am McpServer | F-007 | P2 | Entwicklung | Herkunftsliste (Copilot-Studio-Discovery beachten) |
| 11 | **API-Key** abschaffen oder pro Client scopen | F-003, D-4, Q-SEC-3 | P2 | Eigentümer | abschaffen (ungenutzt) |
| 12 | **Report-Paging und Antwortgrößen / SSE-Limit** – **teilweise umgesetzt** (PR #36, `897a14a`, in Dev deployt; Legacy-Tool `get_service_health_issues` live verifiziert: SSE-Fehler behoben, 55 von 100 Einträgen markiert gekürzt; neue Issue-Tools automatisiert getestet, live nicht verifiziert) | Q-API-2, ADR-0010 | P2 | Entwicklung | Paging für Messages/Reports, Live-Prüfung der neuen Tools (inkl. Gateway), Fix für Befunde F-1/F-2 (`fix/q-api-2-guard-envelope`, nicht deployt), sieben Legacy-Tools mit Null-Typ-Schemas; Details [Q-API-2-RESPONSE-LIMITS.md](Q-API-2-RESPONSE-LIMITS.md) |
| 13 | **Testprojekt für den McpServer** (OAuth-Fassade, UserContext-Client-Seite, Hardening) | Q-EXT-2 | P2 | Entwicklung | xUnit-Projekt anlegen |
| 14 | **SAST, IaC-Scanner (tfsec/Checkov/Trivy), SBOM** in CI | Q-TF-1 | P2 | Entwicklung | als nicht blockierende Jobs |
| 15 | **Gateway-Containerstart** 140–170 s (auch B1) | F-014, Q-GW-4 | P2 | Entwicklung | Start messen/optimieren, Always On vorhanden |
| 16 | **Pentest und Audit**: Freigabe, Testnutzer, Termin; Logs vorher sichern | PENTEST-*, AUDIT-PREPARATION | P2 | Eigentümer | Scope freigeben |
| 17 | **Rollback-Test** im Dev-Tenant nie durchgeführt | [FASTMCP-GATEWAY-ROLLBACK.md](FASTMCP-GATEWAY-ROLLBACK.md) | P2 | Eigentümer/Entwicklung | Clients testweise auf die Fassade zurück |
| 18 | **Storage-Shared-Keys** am Gateway-State wieder abschalten (akzeptiert) | F-012, Q-AZ-3 | P3 | Entwicklung | `storage_use_azuread` + Data-Plane-Rolle für den Deployer prüfen |
| 19 | **Ungenutzter Code**: `ApiTokenService.GetOnBehalfOfTokenAsync` (OBO nicht eingeführt) | ADR-0006 | P3 | Entwicklung | entfernen |
| 20 | **MCP-Protokollstand**: `2025-11-25` nachgewiesen; neuere Spezifikation ungeprüft | Q-MCP-1 | P3 | Entwicklung | erst prüfen, dann behaupten |
| 21 | **Gemeinsame Bibliothek** statt Compile-Links (3+3 Dateien) | ADR-0009, Q-EXT-1 | P3 | Entwicklung | `src/Shared` beim zweiten MCP |
| 22 | **Alter Branch `master`** auf GitHub (enthält nur eine frühe LICENSE-Historie) | – | P3 | Eigentümer | löschen oder behalten |
| 23 | **Mehrere Umgebungen** (staging/prod): nur `dev` vorhanden | ADR-0012, Q-TF-2 | P3 | Eigentümer | bei Bedarf |

## 7. Ungeklärtes (bewusst als Unsicherheit stehen gelassen)

- **Warum ChatGPT jetzt funktioniert.** Vorher lehnte der Client die Antwort mit „issuer does not match the expected issuer“ ab. Hypothesen (Client-Bug openai/codex#31573, Schrägstrich im Issuer, RFC-9207-Flag) wurden nicht bestätigt; das Flag steht wieder auf dem Standard, der Wrapper `gateway/issuer.py` bleibt vorerst.
- **Warum `tools/call` nach PR #16 einmal klappte**, bevor PR #29 den eigentlichen Fehler (reservierte `_meta`-Schlüssel) behob.
- **F-016** ist ein Befund aus Konfiguration und Code; ein tatsächlicher Angriff wurde nie nachgestellt.

## 8. Betrieb in Kürze

| Aufgabe | Wie |
|---|---|
| Gateway live testen | `cd src/OAuthGateway && python tools/gateway_e2e.py https://entramcp-dev-oauth-gateway.azurewebsites.net/mcp` (Browser-Login, 5 Minuten Zeit) |
| Logs lesen | Kudu `…scm.azurewebsites.net/api/vfs/LogFiles/` (Gateway, API, MCP; **1 Tag**) |
| Nach Deploy | Gateway braucht 2–3 Minuten zum Start; in dieser Zeit `503` |
| Rollback der Clients | auf die .NET-Fassade zurückstellen: [FASTMCP-GATEWAY-ROLLBACK.md](FASTMCP-GATEWAY-ROLLBACK.md) |
| Schalter (Standard beibehalten) | `GATEWAY_DEBUG_ERRORS` (aus), `GATEWAY_ADVERTISE_ISS_PARAM` (true), `GATEWAY_LIMITS_ENABLED` (true), `Hardening:Enabled` (true), `Audit:LogUserPrincipalName` (true) |
| Foundry-App anlegen | `./scripts/New-FoundryMcpClient.ps1` ([FOUNDRY-OAUTH.md](FOUNDRY-OAUTH.md)) |

## 9. Änderungshistorie dieser Arbeit

PRs #8–#34 (Gateway, Terraform, CI, Sicherheitsmaßnahmen, Dokumentation). Jeder PR beschreibt, was belegt und was ungeprüft ist; aufgeräumt wurden alle gemergten Branches, das veraltete `poc/`-Verzeichnis ist durch `src/OAuthGateway` ersetzt.
