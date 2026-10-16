variable "resource_group_name" {
  type = string
}

variable "location" {
  type = string
}

variable "name_prefix" {
  type = string
}

variable "tags" {
  type = map(string)
}

variable "app_service_sku" {
  type    = string
  default = "F1" # Free-Tier - kostenlos, kein "Always On" (App schlaeft bei Inaktivitaet ein)
}

variable "log_analytics_daily_quota_gb" {
  description = "Hartes Tageslimit für Log-Ingestion in GB, deckelt die Kosten auf Cent-Beträge. -1 = kein Limit."
  type        = number
  default     = 1
}

variable "tenant_id" {
  type = string
}

variable "api_app_id" {
  type = string
}

variable "api_app_identifier_uri" {
  type = string
}

variable "api_app_client_secret" {
  type      = string
  sensitive = true
}

variable "swagger_client_app_id" {
  type = string
}

variable "mcp_app_id" {
  type = string
}

variable "mcp_app_identifier_uri" {
  type = string
}

variable "mcp_app_client_secret" {
  type      = string
  sensitive = true
}

variable "mcp_oauth_client_ids" {
  type    = map(string)
  default = {}
}

variable "copilot_mcp_client_secret" {
  description = "Client Secret des Copilot-Studio-OAuth-Clients (confidential client - siehe Kommentar in modules/entra-id/main.tf)."
  type        = string
  sensitive   = true
}

variable "azure_health_subscription_ids" {
  description = "Subscription-IDs, deren Azure Service/Resource Health die API-Identitaet lesen darf (Allowlist der Anwendung + RBAC-Zuweisung je Subscription). Leer = Azure-Health-Funktionen deaktiviert, keine RBAC-Zuweisung."
  type        = list(string)
  default     = []

  validation {
    condition     = alltrue([for id in var.azure_health_subscription_ids : can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", id))])
    error_message = "azure_health_subscription_ids darf nur gueltige GUIDs enthalten."
  }
}

variable "azure_health_role_name" {
  description = "Azure-RBAC-Rolle fuer den lesenden Zugriff auf Resource Health. Standard: Built-in-Rolle 'Service Health Security Reader' (nur Microsoft.ResourceHealth-Leseaktionen). Alternative: 'Reader' (breiter)."
  type        = string
  default     = "Service Health Security Reader"
}
