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
