output "api_app_id" {
  value = azuread_application.api.client_id
}

output "api_app_identifier_uri" {
  value = "api://${azuread_application.api.client_id}"
}

output "mcp_app_id" {
  value = azuread_application.mcp.client_id
}

output "mcp_app_client_secret" {
  value     = azuread_application_password.mcp.value
  sensitive = true
}

output "tenant_id" {
  value = data.azuread_client_config.current.tenant_id
}
