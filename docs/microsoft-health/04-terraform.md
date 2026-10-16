# 04 – Terraform

## 1. Geänderte Dateien

| Datei | Änderung |
|---|---|
| `terraform/variables.tf` | neue Variablen `azure_health_subscription_ids` (`list(string)`, Standard `[]`) und `azure_health_role_name` (Standard `"Service Health Security Reader"`) |
| `terraform/main.tf` | reicht beide Variablen an `module.infra` weiter |
| `terraform/modules/infra/variables.tf` | dieselben Variablen; GUID-Validierung der Subscription-IDs |
| `terraform/modules/infra/main.tf` | (1) `app_settings` der API-Web-App erweitert um `MicrosoftHealth__*`; (2) neue Ressource `azurerm_role_assignment.api_azure_health_reader` (`for_each` über die Subscription-Liste) |
| `terraform/terraform.example.tfvars.example` | Beispielzeile (auskommentiert) |

**Nicht geändert:** `modules/entra-id/*` (keine neue Graph-Permission nötig), alle vorhandenen App-Role-Assignments, App-Registrierungen, Service Principals, Key Vault, Backend, Provider-Versionen und `.terraform.lock.hcl`. Die neue Variable hat den Standard `[]`; `terraform.dev.tfvars` wurde **nicht** angefasst.

## 2. Neue Variablen

| Variable | Standard | Wirkung |
|---|---|---|
| `azure_health_subscription_ids` | `[]` | Allowlist der Anwendung (`MicrosoftHealth__AllowedSubscriptionIds__N`) **und** Scopes der Rollenzuweisung. Leer: keine RBAC-Ressource, `MicrosoftHealth__EnableAzure=false` |
| `azure_health_role_name` | `Service Health Security Reader` | Built-in-Rolle; Alternative `Reader` (breiter) |

## 3. Neue/angepasste Ressourcen

```hcl
# modules/infra/main.tf
resource "azurerm_role_assignment" "api_azure_health_reader" {
  for_each             = toset(var.azure_health_subscription_ids)
  scope                = "/subscriptions/${each.value}"
  role_definition_name = var.azure_health_role_name
  principal_id         = azurerm_linux_web_app.api.identity[0].principal_id   # Managed Identity der API, NICHT die App Registration
  principal_type       = "ServicePrincipal"
}
```
`azurerm_linux_web_app.api` bleibt dieselbe Ressource (gleiche State-Adresse); es ändert sich nur der `app_settings`-Block (neue Schlüssel `MicrosoftHealth__Enabled`, `__TenantId`, `__EnableIntune`, `__EnableAzure`, `__AllowedSubscriptionIds__N`). Keine Secrets, keine Key-Vault-Einträge, keine Outputs.

## 4. Vorhandene versus neue Ressourcen

| Ressource | Zustand |
|---|---|
| `azuread_app_role_assignment.api_to_graph_service_health` / `…_service_message` (Entra-Modul) | **vorhanden, unverändert** |
| `azuread_app_role_assignment.api_managed_identity_to_graph_service_health` / `…_service_message` (Infra-Modul) | **vorhanden, unverändert** |
| `azurerm_linux_web_app.api` | vorhanden, **in-place Update** (App Settings; Neustart der API-Web-App) |
| `azurerm_role_assignment.api_azure_health_reader["<subscription>"]` | **neu**, nur wenn `azure_health_subscription_ids` nicht leer ist |

## 5. Plan-Erwartung und Ergebnis

Erwartung bei Standardwerten (`azure_health_subscription_ids = []`): **1 to change** (`module.infra.azurerm_linux_web_app.api`, nur `app_settings`), **0 add, 0 destroy**, keine Replacements, keine Änderung an Permissions.
Mit einer Subscription: zusätzlich **1 to add** (`api_azure_health_reader["…"]`).

Lokal geprüft (tatsächlich ausgeführt, in einer Kopie ohne Backend-Zustand, Provider laut Lock-Datei azurerm 4.81.0 / azuread 3.9.0):

- `terraform fmt -check -recursive` → Exit 0
- `terraform init -backend=false` → Exit 0
- `terraform validate` → **Success** (eine bereits vorhandene Deprecation-Warnung zu `azuread_application_password`, nicht von dieser Änderung)

**Nicht lokal ausführbar:** `terraform plan` gegen das Backend. Der lokale `az`-Kontext zeigt auf einen anderen Tenant/Subscription als `.env`; `Invoke-TerraformPlan.ps1` würde ein interaktives `az login` auslösen. Der maßgebliche Plan ist der Job `terraform-plan` des Pull Requests (PR-Kommentar, siehe PR-Beschreibung für das Ergebnis). **Es wurde kein `terraform apply` und kein `destroy` ausgeführt.**

## 6. Deployment-Auswirkungen

1. Merge → `deploy-dev` wendet den Plan an (Standardwerte): API-Web-App erhält neue App Settings und startet neu. Die Intune-/Microsoft-365-Tools sind danach sofort nutzbar (Graph-Rollen existieren bereits); `azure_*`-Tools antworten `disabled`.
2. **Azure-Teil aktivieren** (separater, bewusster Schritt):
   1. Freigabe einholen, dass der Deployer (CI-Principal) auf der Ziel-Subscription `Microsoft.Authorization/roleAssignments/write` erhält (z. B. `Role Based Access Control Administrator`, bevorzugt per ABAC-Bedingung auf die Rolle `Service Health Security Reader` beschränkt). Bisher hat er das nur auf dem Key Vault.
   2. `azure_health_subscription_ids = ["<guid>"]` in `terraform/terraform.dev.tfvars` setzen, PR, Plan prüfen (`1 to add`), Merge.
   3. Funktionskontrolle laut [05-operations.md](05-operations.md).
   Alternativ kann ein Subscription-Owner die Rolle einmalig manuell zuweisen – dann würde Terraform sie beim ersten Apply übernehmen müssen (`terraform import module.infra.azurerm_role_assignment.api_azure_health_reader[\"<guid>\"] <role-assignment-id>`); **nicht automatisch ausführen**, vorher abstimmen.

## 7. Risiken für bestehende Infrastruktur

- API-Web-App-Neustart beim Apply (F1: kurzer Cold-Start).
- Fehlende Deployer-Berechtigung bei aktivierter Subscription → `AuthorizationFailed` auf `Microsoft.Authorization/roleAssignments/write`; der Apply bricht ab, ohne bestehende Ressourcen zu verändern (kein Delete im Plan).
- Rollenzuweisungen können 1–2 Minuten bis zur Wirksamkeit brauchen.

## 8. Rollback

- Variable wieder auf `[]` setzen → Plan zeigt `1 to destroy` für die Rollenzuweisung (und Änderung der App Settings). Der Schutz in `Invoke-TerraformApply.ps1` bricht bei Löschungen ohne `-Force` ab – das ist hier erwartetes Verhalten und muss bewusst freigegeben werden (nie dauerhaft `-Force` im Workflow).
- Komplett-Rollback: Revert-PR des Merge-Commits; zusätzlich vorhandene Rollenzuweisung über Terraform entfernen (siehe oben).
- Anwendungsseitig genügt `MicrosoftHealth__Enabled=false` (App Setting), um alle Health-Endpunkte auf `disabled` zu schalten.
