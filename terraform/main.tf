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

  environment_name = var.environment_name
  mcp_redirect_uri = "https://${local.name_prefix}-mcp.azurewebsites.net/signin-oidc"
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

  api_app_id             = module.entra_id.api_app_id
  api_app_identifier_uri = module.entra_id.api_app_identifier_uri
  tenant_id              = module.entra_id.tenant_id
  mcp_app_id             = module.entra_id.mcp_app_id
  mcp_app_client_secret  = module.entra_id.mcp_app_client_secret
}
