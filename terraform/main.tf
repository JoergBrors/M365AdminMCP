locals {
  name_prefix = "entramcp-${var.environment_name}"
  tags = {
    environment = var.environment_name
    project     = "entra-mcp-mvp"
    managedBy   = "terraform"
  }
}

resource "azurerm_resource_group" "this" {
  name     = "rg-entramcp-${var.environment_name}"
  location = var.location
  tags     = local.tags
}

module "entra_id" {
  source = "./modules/entra-id"

  environment_name          = var.environment_name
  mcp_redirect_uri          = "https://${local.name_prefix}-mcp.azurewebsites.net/signin-oidc"
  mcp_redirect_uris         = ["https://${local.name_prefix}-mcp.azurewebsites.net/signin-oidc"]
  chatgpt_mcp_redirect_uris = var.chatgpt_mcp_redirect_uris
  claude_mcp_redirect_uris  = var.claude_mcp_redirect_uris
  copilot_mcp_redirect_uris = var.copilot_mcp_redirect_uris
  api_swagger_redirect_uris = [
    "https://${local.name_prefix}-api.azurewebsites.net/swagger/oauth2-redirect.html",
    "https://localhost:7043/swagger/oauth2-redirect.html",
    "http://localhost:5043/swagger/oauth2-redirect.html"
  ]
}

module "infra" {
  source = "./modules/infra"

  resource_group_name          = azurerm_resource_group.this.name
  location                     = var.location
  name_prefix                  = local.name_prefix
  tags                         = local.tags
  app_service_sku              = var.app_service_sku
  log_analytics_daily_quota_gb = var.log_analytics_daily_quota_gb

  api_app_id               = module.entra_id.api_app_id
  api_app_identifier_uri   = module.entra_id.api_app_identifier_uri
  api_app_client_secret    = module.entra_id.api_app_client_secret
  swagger_client_app_id    = module.entra_id.swagger_client_app_id
  tenant_id                = module.entra_id.tenant_id
  mcp_app_id               = module.entra_id.mcp_app_id
  mcp_app_identifier_uri   = module.entra_id.mcp_app_identifier_uri
  mcp_app_client_secret    = module.entra_id.mcp_app_client_secret
  mcp_oauth_client_ids     = module.entra_id.mcp_oauth_client_ids
  mcp_oauth_client_secrets = module.entra_id.mcp_oauth_client_secrets
}
