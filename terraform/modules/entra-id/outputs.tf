output "api_app_id" {
  value = azuread_application.api.client_id
}

output "api_app_identifier_uri" {
  value = one(azuread_application.api.identifier_uris)
}

output "api_app_client_secret" {
  value     = azuread_application_password.api.value
  sensitive = true
}

output "mcp_app_id" {
  value = azuread_application.mcp.client_id
}

output "mcp_app_identifier_uri" {
  value = one(azuread_application.mcp.identifier_uris)
}

output "swagger_client_app_id" {
  value = azuread_application.swagger.client_id
}

output "mcp_app_client_secret" {
  value     = azuread_application_password.mcp.value
  sensitive = true
}

output "mcp_oauth_client_ids" {
  value = merge(
    { for key, app in azuread_application.mcp_oauth_client : key => app.client_id },
    { copilot = azuread_application.copilot_mcp_oauth_client.client_id }
  )
}

output "copilot_mcp_client_secret" {
  value     = azuread_application_password.copilot_mcp_oauth_client.value
  sensitive = true
}

output "tenant_id" {
  value = data.azuread_client_config.current.tenant_id
}
