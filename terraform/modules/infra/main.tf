data "azurerm_client_config" "current" {}

resource "random_string" "kv_suffix" {
  length  = 4
  special = false
  upper   = false
}

# --- Monitoring ---

resource "azurerm_log_analytics_workspace" "this" {
  name                = "${var.name_prefix}-law"
  location            = var.location
  resource_group_name = var.resource_group_name
  sku                 = "PerGB2018"
  retention_in_days   = 30
  daily_quota_gb      = var.log_analytics_daily_quota_gb
  tags                = var.tags
}

resource "azurerm_application_insights" "this" {
  name                = "${var.name_prefix}-appi"
  location            = var.location
  resource_group_name = var.resource_group_name
  workspace_id        = azurerm_log_analytics_workspace.this.id
  application_type    = "web"
  tags                = var.tags
}

# --- Key Vault ---

resource "azurerm_key_vault" "this" {
  name                       = substr("${var.name_prefix}-kv-${random_string.kv_suffix.result}", 0, 24)
  location                   = var.location
  resource_group_name        = var.resource_group_name
  tenant_id                  = var.tenant_id
  sku_name                   = "standard"
  enable_rbac_authorization  = true
  soft_delete_retention_days = 7
  purge_protection_enabled   = true
  tags                       = var.tags
}

resource "azurerm_role_assignment" "deployer_kv_admin" {
  scope                = azurerm_key_vault.this.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = data.azurerm_client_config.current.object_id
}

resource "azurerm_key_vault_secret" "mcp_client_secret" {
  name         = "mcp-server-client-secret"
  value        = var.mcp_app_client_secret
  key_vault_id = azurerm_key_vault.this.id
  depends_on   = [azurerm_role_assignment.deployer_kv_admin]
}

resource "azurerm_key_vault_secret" "api_app_id" {
  name         = "api-server-app-id"
  value        = var.api_app_id
  key_vault_id = azurerm_key_vault.this.id
  depends_on   = [azurerm_role_assignment.deployer_kv_admin]
}

resource "azurerm_key_vault_secret" "mcp_app_id" {
  name         = "mcp-server-app-id"
  value        = var.mcp_app_id
  key_vault_id = azurerm_key_vault.this.id
  depends_on   = [azurerm_role_assignment.deployer_kv_admin]
}

# --- App Service ---

locals {
  # F1 (Free) unterstuetzt kein "Always On" - die App schlaeft nach Inaktivitaet ein
  # (fuer Dev/Test i.d.R. unproblematisch).
  is_free_tier = var.app_service_sku == "F1"
}

resource "azurerm_service_plan" "this" {
  name                = "${var.name_prefix}-plan"
  location            = var.location
  resource_group_name = var.resource_group_name
  os_type             = "Linux"
  sku_name            = var.app_service_sku
  tags                = var.tags
}

resource "azurerm_linux_web_app" "api" {
  name                = "${var.name_prefix}-api"
  location            = var.location
  resource_group_name = var.resource_group_name
  service_plan_id     = azurerm_service_plan.this.id
  https_only          = true
  tags                = var.tags

  identity {
    type = "SystemAssigned"
  }

  site_config {
    always_on = !local.is_free_tier
    application_stack {
      dotnet_version = "8.0"
    }
  }

  app_settings = {
    "APPLICATIONINSIGHTS_CONNECTION_STRING" = azurerm_application_insights.this.connection_string
    "AzureAd__TenantId"                     = var.tenant_id
    "AzureAd__ClientId"                     = var.api_app_id
    "AzureAd__Audience"                     = var.api_app_identifier_uri
    "ASPNETCORE_ENVIRONMENT"                = "Production"
  }
}

resource "azurerm_linux_web_app" "mcp" {
  name                = "${var.name_prefix}-mcp"
  location            = var.location
  resource_group_name = var.resource_group_name
  service_plan_id     = azurerm_service_plan.this.id
  https_only          = true
  tags                = var.tags

  identity {
    type = "SystemAssigned"
  }

  site_config {
    always_on = !local.is_free_tier
    application_stack {
      dotnet_version = "8.0"
    }
  }

  app_settings = {
    "APPLICATIONINSIGHTS_CONNECTION_STRING" = azurerm_application_insights.this.connection_string
    "AzureAd__TenantId"                     = var.tenant_id
    "AzureAd__ClientId"                     = var.mcp_app_id
    # Key-Vault-Reference statt Klartext - Web App braucht dafür "Key Vault Secrets User" (s.u.)
    "AzureAd__ClientSecret"  = "@Microsoft.KeyVault(SecretUri=${azurerm_key_vault_secret.mcp_client_secret.versionless_id})"
    "ApiServer__BaseUrl"     = "https://${azurerm_linux_web_app.api.default_hostname}"
    "ASPNETCORE_ENVIRONMENT" = "Production"
  }
}

resource "azurerm_role_assignment" "mcp_kv_secrets_user" {
  scope                = azurerm_key_vault.this.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_linux_web_app.mcp.identity[0].principal_id
}

resource "azurerm_role_assignment" "api_kv_secrets_user" {
  scope                = azurerm_key_vault.this.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_linux_web_app.api.identity[0].principal_id
}
