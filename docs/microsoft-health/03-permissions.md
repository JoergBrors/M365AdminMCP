# 03 – Berechtigungen

## 1. Ergebnis in einem Satz

**Neue Microsoft-Graph-Berechtigungen sind nicht erforderlich** – `ServiceHealth.Read.All` und `ServiceMessage.Read.All` sind bereits vorhanden (Application-Rollen, für App-Registrierung **und** Managed Identity). Neu ist nur die **Azure-RBAC-Zuweisung** für Azure Resource Health, bewusst optional und standardmäßig leer.

## 2. Berechtigungsmatrix vor der Änderung (aus der tatsächlichen Terraform-Konfiguration)

| API | Permission | Typ | Terraform-Ressource | Runtime-Identität | Status | Änderung |
|---|---|---|---|---|---|---|
| Microsoft Graph | `ServiceHealth.Read.All` | Application | `module.entra_id.azuread_application.api` (`required_resource_access`, `entra-id/main.tf:64`), `module.entra_id.azuread_app_role_assignment.api_to_graph_service_health` (`:290`) | App-Registrierung `api-server` (lokal) | In Terraform definiert | **Unverändert / nicht erforderlich** |
| Microsoft Graph | `ServiceHealth.Read.All` | Application | `module.infra.azuread_app_role_assignment.api_managed_identity_to_graph_service_health` (`infra/main.tf`, Zeile 218 in diesem Branch) | **Managed Identity der API-Web-App (Azure)** | In Terraform definiert | **Unverändert / nicht erforderlich** |
| Microsoft Graph | `ServiceMessage.Read.All` | Application | `entra-id/main.tf:68`, `…api_to_graph_service_message` (`:296`) | App-Registrierung (lokal) | In Terraform definiert | Unverändert / nicht erforderlich |
| Microsoft Graph | `ServiceMessage.Read.All` | Application | `infra/main.tf` (Zeile 224) `…api_managed_identity_to_graph_service_message` | Managed Identity API (Azure) | In Terraform definiert | Unverändert / nicht erforderlich |
| Microsoft Graph | `Reports.Read.All` | Application | `entra-id/main.tf:72`, `…:302`, `infra/main.tf` (Zeile 230) | beide | In Terraform definiert | Unverändert (nicht Teil dieser Erweiterung) |
| `api-server` (eigene API) | `Tasks.ReadWrite.All` | Application | `azuread_app_role_assignment.mcp_to_api_task_readwrite`, `…mcp_managed_identity_to_api_task_readwrite` | MCP-App / MCP-Managed-Identity | In Terraform definiert | Unverändert |
| `api-server` | `Tasks.ReadWrite` | Delegated | `azuread_service_principal_delegated_permission_grant.mcp_delegated_to_api`, `swagger_delegated_to_api` | MCP-App / Swagger | In Terraform definiert | Unverändert |
| `mcp-server` | `Mcp.Access` (+ `offline_access`) | Delegated | `mcp_oauth_client_delegated_to_mcp`, `copilot_mcp_oauth_client_delegated_to_mcp` | externe OAuth-Clients | In Terraform definiert | Unverändert |
| Azure Resource Manager | *(keine Rolle für die API-Identität)* | Azure RBAC | – | Managed Identity API | – | **Neu erforderlich (optional)** für `azure_*`-Tools |

Verteilung auf mehrere Dateien: ja – die Graph-Rollen stehen in `modules/entra-id/main.tf` (App-Registrierung, lokal) **und** `modules/infra/main.tf` (Managed Identity, Azure). Beides bleibt unverändert; es wurde nichts doppelt angelegt oder entfernt. Gegenprobe der Live-Identität: Der lokal ausgestellte Graph-Token enthält die Rollen `Reports.Read.All`, `ServiceHealth.Read.All`, `ServiceMessage.Read.All`.

Nicht hinzugefügt: `DeviceManagementManagedDevices.Read.All` und alle weiteren `DeviceManagement*`-Rechte (für Service Health nicht nötig). Es wurde keine Permission durch eine „ähnliche“ ersetzt.

## 3. Neu: Azure-RBAC für Resource Health

Benötigte Aktionen laut aktueller Azure-Doku:

| API | Aktion |
|---|---|
| `GET …/subscriptions/{id}/providers/Microsoft.ResourceHealth/events` | `Microsoft.ResourceHealth/events/read` |
| `GET …/{resourceUri}/providers/Microsoft.ResourceHealth/availabilityStatuses/current` | `Microsoft.ResourceHealth/AvailabilityStatuses/current/read` |
| `GET …/subscriptions/{id}/providers/Microsoft.ResourceHealth/availabilityStatuses` | `Microsoft.ResourceHealth/AvailabilityStatuses/read` |

Gewählte Rolle (Standard): Built-in **`Service Health Security Reader`** (Microsoft Learn: *Azure built-in roles for Monitor*). Sie enthält diese drei Aktionen und sonst nur `Microsoft.ResourceHealth`-Aktionen (u. a. `events/fetchEventDetails/action`, `emergingissues/read`, `metadata/read`, `potentialoutages/read`; `NotActions`: `events/fetchBillingCommunicationDetails/action`) – keine Aktionen auf anderen Resource Providern. Die Tools rufen `fetchEventDetails` **nicht** auf.

Alternativen und Abwägung:

| Option | Bewertung |
|---|---|
| `Service Health Security Reader` (**gewählt**) | Auf `Microsoft.ResourceHealth` begrenzt; enthält jedoch zusätzlich Lese-/Aktionsrechte für Security-Advisory-Details, die die Tools nicht nutzen. Überschreitung des Minimalbedarfs ist gering und auf einen Provider begrenzt. |
| `Reader` | Breiter (`*/read` auf alle Ressourcen der Subscription). Nur wählen, wenn die Service-Health-Rolle nicht verfügbar ist (`azure_health_role_name = "Reader"`). |
| Custom Role | Exakt minimal (3 Aktionen), erfordert aber `Microsoft.Authorization/roleDefinitions/write` für den Deployer; nicht umgesetzt, solange nicht gewünscht. |
| `Contributor`/`Owner` | **Ausgeschlossen.** |

Scope: Subscription (`/subscriptions/{id}`), nur für die in `azure_health_subscription_ids` gelisteten Subscriptions. Die Subscription-Liste ist zugleich die Allowlist der Anwendung (`MicrosoftHealth__AllowedSubscriptionIds__N`). Eine Rolle auf Resource-Group-Ebene würde `azure_service_health`/`azure_subscription_resource_health` nicht abdecken (Subscription-Ebene-Endpunkte).

## 4. Runtime-Identität nach der Änderung

| Zweck | Identität | Rechte |
|---|---|---|
| Graph (Intune/M365 Health) | Managed Identity der API-Web-App | Graph-App-Rollen `ServiceHealth.Read.All`, `ServiceMessage.Read.All` (vorhanden) |
| Azure Resource Health | **dieselbe** Managed Identity | RBAC-Rolle (optional) auf freigegebenen Subscriptions |
| MCP → API | Managed Identity der MCP-Web-App | `Tasks.ReadWrite.All` auf der API (unverändert) |

Lokal (VS Code): App-Registrierung `api-server-<env>` per Client Secret. Sie hat **keine** Azure-RBAC-Rolle → die `azure_*`-Tools antworten lokal mit `forbidden` (erwartet und getestet).

## 5. Administratorzustimmung / Zuweisungsstand

- Graph-Anwendungsrollen sind über `azuread_app_role_assignment` **tatsächlich zugewiesen** (nicht nur deklariert). Nachweis im Tenant: Token-Claim `roles` enthält die Rollen (siehe oben).
- Der Deployer braucht für die neue RBAC-Zuweisung **zusätzlich** `Microsoft.Authorization/roleAssignments/write` auf der Subscription (z. B. `Role Based Access Control Administrator`, idealerweise mit ABAC-Bedingung auf diese eine Rolle). Heute besitzt er diese Berechtigung nur auf dem Key Vault. Fehlt sie, scheitert der Apply mit `AuthorizationFailed` – deshalb ist die Variable standardmäßig leer. **Nicht eigenmächtig vergeben** (AGENTS.md): Scope und Rolle benennen und Freigabe einholen.

## 6. Effektive Berechtigungen prüfen

```powershell
# Graph-Rollen der Managed Identity der API-Web-App
$principal = az webapp identity show -g rg-entramcp-dev -n entramcp-dev-api --query principalId -o tsv
az rest --method get --url "https://graph.microsoft.com/v1.0/servicePrincipals/$principal/appRoleAssignments" --query "value[].appRoleId"

# Azure-RBAC der Managed Identity auf der Subscription
az role assignment list --assignee $principal --scope "/subscriptions/<subscriptionId>" --query "[].roleDefinitionName" -o tsv
```
Funktionsprüfung: Tool `microsoft_health_summary` – je Quelle `status`; `forbidden` nennt die fehlende Berechtigung (siehe [05-operations.md](05-operations.md)).
