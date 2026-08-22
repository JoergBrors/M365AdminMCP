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
  default     = "B1"
}
