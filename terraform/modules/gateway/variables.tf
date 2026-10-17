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

variable "tenant_id" {
  type = string
}

variable "key_vault_id" {
  type = string
}

variable "key_vault_deployer_role_id" {
  type = string
}

variable "application_insights_connection_string" {
  type      = string
  sensitive = true
}

variable "gateway_client_id" {
  type = string
}

variable "gateway_client_secret" {
  type      = string
  sensitive = true
}

variable "mcp_app_identifier_uri" {
  type = string
}

variable "backend_mcp_url" {
  type = string
}

variable "python_version" {
  description = "Python-Runtime des App Service (PoC und Tests mit 3.12; `az webapp list-runtimes` listet 3.10-3.15)."
  type        = string
  default     = "3.12"
}

variable "gateway_sku" {
  description = "SKU des separaten Gateway-Plans (F1 oder B1). Aenderung nur mit Freigabe des Eigentuemers."
  type        = string
  default     = "F1"

  validation {
    condition     = contains(["F1", "B1"], var.gateway_sku)
    error_message = "gateway_sku muss F1 oder B1 sein."
  }
}
