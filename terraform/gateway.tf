# FastMCP OAuth Gateway (optional). Default enable_gateway = false => keine Gateway-Ressourcen, keine Aenderung
# an bestehenden Ressourcen. Reihenfolge ohne Zyklus: Hostname ist deterministisch (name_prefix-oauth-gateway),
# daher kann die Entra-Redirect-URI vor der Web App feststehen; das Secret fliesst Entra -> Key Vault -> App Setting.

variable "gateway_sku" {
  description = "SKU des Gateway-Plans (F1 oder B1)"
  type        = string
  default     = "F1"
}

variable "enable_gateway" {
  description = "Aktiviert das FastMCP OAuth Gateway"
  type        = bool
  default     = false
}

module "gateway" {
  source = "./modules/gateway"
  count  = var.enable_gateway ? 1 : 0

  resource_group_name = azurerm_resource_group.this.name
  location            = var.location
  name_prefix         = local.name_prefix
  tags                = local.tags
  tenant_id           = module.entra_id.tenant_id

  key_vault_id                           = module.infra.key_vault_id
  key_vault_deployer_role_id             = module.infra.key_vault_deployer_role_id
  application_insights_connection_string = module.infra.application_insights_connection_string

  gateway_client_id      = module.entra_id.gateway_client_id
  gateway_client_secret  = module.entra_id.gateway_client_secret
  mcp_app_client_id      = module.entra_id.mcp_app_id
  mcp_app_identifier_uri = module.entra_id.mcp_app_identifier_uri
  backend_mcp_url        = "https://${module.infra.mcp_app_hostname}/mcp"
  gateway_sku            = var.gateway_sku
}

# Die in Entra registrierte Callback-URL muss exakt zum tatsaechlichen Gateway-Hostnamen passen.
check "gateway_callback_matches_hostname" {
  assert {
    condition     = !var.enable_gateway || "https://${local.name_prefix}-oauth-gateway.azurewebsites.net" == one(module.gateway[*].gateway_public_url)
    error_message = "Gateway-Redirect-URI in Entra weicht vom Gateway-Hostnamen ab."
  }
}

output "gateway_public_url" {
  value = var.enable_gateway ? module.gateway[0].gateway_public_url : null
}

output "gateway_app_name" {
  value = var.enable_gateway ? module.gateway[0].gateway_app_name : null
}
