output "api_app_hostname" {
  value = module.infra.api_app_hostname
}

output "mcp_app_hostname" {
  value = module.infra.mcp_app_hostname
}

output "key_vault_name" {
  value = module.infra.key_vault_name
}

output "api_app_id" {
  value = module.entra_id.api_app_id
}

output "api_app_identifier_uri" {
  value = module.entra_id.api_app_identifier_uri
}

output "mcp_app_id" {
  value = module.entra_id.mcp_app_id
}

output "swagger_client_app_id" {
  value = module.entra_id.swagger_client_app_id
}
