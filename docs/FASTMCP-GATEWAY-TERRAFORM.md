# FastMCP OAuth Gateway – Terraform

Alles unter dem bestehenden Weg (`scripts/Invoke-TerraformPlan.ps1`, State `entra-mcp-mvp.tfstate`). Kein Apply durchgeführt.

## Dateien
| Datei | Inhalt |
|---|---|
| `terraform/gateway.tf` | Variable `enable_gateway` (Default `false`), Modulaufruf mit `count`, `check`-Block (Callback-URL = Hostname), Outputs |
| `terraform/modules/gateway/` | F1-Plan `entramcp-dev-gateway-plan`, Web App `entramcp-dev-oauth-gateway` (Python 3.12, kein Container), Storage Account + Table `oauthstate`, 3 Key-Vault-Secrets, 2 Rollenzuweisungen |
| `terraform/modules/entra-id/gateway.tf` | Upstream-App `oauth-gateway-dev` (`web{}`, Single-Tenant), SP, Secret, delegierter Grant `Mcp.Access` + `offline_access` auf die MCP-App |
| `terraform/modules/infra/gateway_outputs.tf` | zusätzliche Outputs (`key_vault_id`, Deployer-Rollen-ID, App-Insights-Verbindung); keine Ressourcenänderung |
| `terraform/main.tf` | zwei neue Argumente an `module.entra_id` |

## Abhängigkeiten / Zyklusfreiheit
Hostname ist deterministisch (`<name_prefix>-oauth-gateway.azurewebsites.net`, wie beim MCP-Server) → Entra-Redirect-URI steht vor der Web App fest. Fluss: Entra-App → Secret → Key-Vault-Secret → App-Setting-Referenz → Web App → Rollen. Der `check`-Block bricht die Validierung ab, wenn Redirect-URI und tatsächlicher Hostname auseinanderlaufen.

## Verifiziert (dieser Lauf)
- `terraform fmt -recursive -check`: sauber. `terraform init -backend=false` + `validate`: gültig (Warnung zu `end_date_relative`, identisch zu bestehenden Ressourcen).
- `Invoke-TerraformPlan.ps1 -Environment dev`:
  - **`enable_gateway=false`:** keine gateway-bedingten Änderungen. Beobachtet wurde nur bestehender Drift: Tag `hidden-link: /app-insights-resource-id` an `entramcp-dev-api` (Update in place) und ein **Replace** von `module.infra.azurerm_role_assignment.deployer_kv_admin`, weil der lokale Planer (`az login`-Nutzer, principal `d085818d…`) nicht der im State hinterlegte Deployer (`dc61996e…`, vermutlich der CI-Service-Principal) ist. In CI mit dem CI-Principal ist dieses Replace nicht zu erwarten – **nicht verifiziert**. Nicht lokal anwenden.
  - **`TF_VAR_enable_gateway=true`:** `Plan: 17 to add, 1 to change, 1 to destroy` – die 17 Adds sind ausschließlich Gateway-Ressourcen; Change/Destroy sind der obige Drift. Keine Änderung an bestehenden App-Registrierungen, am bestehenden Plan, den Web Apps (außer Tag-Drift), Key Vault oder Graph-Rechten.
  - Bewertung gemäß Vorgabe: ein Replace/Delete führt zu NO-GO für **Apply**; vor jedem Apply muss der Plan mit CI-Identität in der PR-Pipeline geprüft werden.

## Secrets und Terraform-State
- `azuread_application_password.gateway` und `random_password.*` legen ihre Werte **im Terraform-State** ab (`sensitive = true` verhindert nur die Anzeige). Das ist dieselbe Grenze wie bei den bestehenden MCP-/API-/Copilot-Secrets. Schutz: Remote State in `stgeuwmcpo365dev` (RG `rg-tfstate`); dessen RBAC wurde in diesem Lauf **nicht** geprüft.
- Keine Secrets in Outputs (`gateway_client_secret` ist `sensitive` und wird nicht als Root-Output exportiert), nicht in GitHub Secrets, nicht im Artefakt.
- Write-only-Attribute wurden nicht verwendet (nicht verifiziert, ob die eingesetzten Provider-Versionen sie anbieten).
- Storage-Account: `shared_access_key_enabled = false`, Zugriff nur per Managed Identity (`Storage Table Data Contributor`). Hinweis: Terraform selbst legt die Tabelle über den Azure-Provider an; mit deaktivierten Shared Keys kann das je nach Provider-Konfiguration fehlschlagen (`storage_use_azuread`), **nicht getestet** – Risiko für den ersten Apply.

## CI-Berechtigungen (GitHub-OIDC, keine Erweiterung nötig)
Per lesender Abfrage verifiziert (2026-10-17): der CI-Principal `sp_keyvault` (Object-ID `dc61996e…`) hat *Contributor* auf `rg-entramcp-dev`, *Role Based Access Control Administrator* auf dem Key Vault **und auf Subscription-Ebene** sowie eine weitere Rolle auf dem Key Vault. Die Rollenzuweisung auf den neuen Storage Account ist damit abgedeckt; eine frühere Vermutung, sie fehle, war falsch. Entra: `Application.ReadWrite.All` (laut `AGENTS.md`); der delegierte Grant folgt dem Muster des Copilot-Clients, ob dafür ein weiteres Graph-Recht nötig ist, ist **nicht verifiziert**.
