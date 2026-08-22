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
  default = "B1"
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

variable "mcp_app_id" {
  type = string
}

variable "mcp_app_client_secret" {
  type      = string
  sensitive = true
}
