variable "environment_name" {
  description = "Kurzname der Umgebung, z.B. dev, staging, prod"
  type        = string
}

variable "location" {
  description = "Azure-Region"
  type        = string
  default     = "westeurope"
}

variable "app_service_sku" {
  description = "App Service Plan SKU"
  type        = string
  default     = "F1" # Free-Tier - kostenlos, kein "Always On" (App schlaeft bei Inaktivitaet ein)
}

variable "log_analytics_daily_quota_gb" {
  description = "Hartes Tageslimit für Log-Ingestion in GB, deckelt die Kosten auf Cent-Beträge. -1 = kein Limit."
  type        = number
  default     = 1
}
