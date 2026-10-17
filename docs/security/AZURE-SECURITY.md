# Azure-Sicherheit

| Thema | Stand | Beleg |
|---|---|---|
| App Services | `https_only = true`, System-Assigned Managed Identity | `terraform/modules/infra/main.tf`, `modules/gateway/main.tf` |
| Public Network Access | aktiv für alle drei Apps; keine IP-Restriktion, kein Private Endpoint, kein VNet | Terraform enthält keine entsprechenden Blöcke (`grep`); F-008 |
| Key Vault | RBAC-Modell, Purge Protection, Soft Delete 7 Tage; Apps als *Key Vault Secrets User* | `infra/main.tf:42-52,199-209` |
| Storage (Gateway-State) | LRS, TLS 1.2, kein öffentlicher Blob-Zugriff; **Shared Keys aktiviert** (F-012); App greift per MI (*Storage Table Data Contributor*) zu | `modules/gateway/main.tf` |
| TLS | `min_tls_version` nur am Storage gesetzt; App-Service-Mindest-TLS nicht explizit konfiguriert (❓) | – |
| Logging | Container-Dateilogs (1 Tag, 35 MB) für Gateway, API und MCP. **Application Insights/Log Analytics sind angelegt (Tageslimit 1 GB), erhalten aber keine Telemetrie:** weder die .NET-Apps (kein `Microsoft.ApplicationInsights`/OpenTelemetry-Paket in den `.csproj`) noch das Gateway senden etwas; Abfrage 2026-10-17: 1 Request, 1 Trace insgesamt | `modules/gateway/main.tf`, `infra/main.tf` |
| Azure RBAC | MI API: Health-Rolle nur auf freigegebenen Subscriptions (`azure_health_subscription_ids`) | ADR-0016 |
| Defender/WAF/Front Door/APIM | nicht vorhanden | – |

## Welche Maßnahmen auf F1/B1 möglich sind
| Maßnahme | F1 | B1 |
|---|---|---|
| IP-Restriktionen (Access Restrictions) | nach meiner Kenntnis verfügbar, **nicht geprüft** | verfügbar (nicht geprüft) |
| Private Endpoint / VNet-Integration | nicht verfügbar | für Basic nicht verfügbar (nach Microsoft-Tier-Matrix erst ab höherer SKU, nicht für B1 verifiziert) |
| Always On | nein | ja |
| Deployment-Slots | nein | nein (ab Standard) |
| Eigener Plan | – | Gateway bereits auf eigenem B1-Plan (kostenpflichtig, Eigentümerentscheidung) |

Keine kostenpflichtigen Ressourcen werden ohne ausdrückliche Freigabe eingeführt. Kostenpflichtige Optionen: Private Endpoints/VNet (höhere Tarife), Front Door + WAF, API Management (Developer/Standard v2; Consumption bietet kein Private Endpoint und keine MCP-Funktionen, siehe [APIM-FAST-MCP-FEASIBILITY.md](../APIM-FAST-MCP-FEASIBILITY.md)), Log Analytics-Diagnoseeinstellung für das Gateway.
