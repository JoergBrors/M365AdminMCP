# FastMCP OAuth Gateway: eigener Linux-F1-Plan, Python-Code-Deployment (kein Container).
# Der bestehende Plan "${name_prefix}-plan" wird nicht angefasst.

locals {
  app_name   = "${var.name_prefix}-oauth-gateway"
  public_url = "https://${local.app_name}.azurewebsites.net"
}

resource "azurerm_service_plan" "gateway" {
  name                = "${var.name_prefix}-gateway-plan"
  location            = var.location
  resource_group_name = var.resource_group_name
  os_type             = "Linux"
  sku_name            = var.gateway_sku # Default F1; Dev wurde auf ausdruecklichen Wunsch des Eigentümers auf B1 gestellt (F1-CPU-Quota)
  tags                = var.tags
}

# --- Persistenz: Azure Table Storage (FastMCP/py-key-value: AzureTablesStore, Auth per Managed Identity) ---
resource "random_string" "storage_suffix" {
  length  = 4
  special = false
  upper   = false
}

resource "azurerm_storage_account" "state" {
  name                            = substr(replace("${var.name_prefix}gw${random_string.storage_suffix.result}", "-", ""), 0, 24)
  resource_group_name             = var.resource_group_name
  location                        = var.location
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  min_tls_version                 = "TLS1_2"
  allow_nested_items_to_be_public = false
  # Shared Keys bleiben aktiviert: der azurerm-Provider pollt beim Anlegen die Data Plane per Key und der
  # Deployer hat keine Data-Plane-Rolle. Die App nutzt ausschliesslich Managed Identity; Keys werden nirgends verteilt.
  shared_access_key_enabled       = true
  default_to_oauth_authentication = true
  tags                            = var.tags
}

resource "azurerm_storage_table" "oauth_state" {
  name               = "oauthstate"
  storage_account_id = azurerm_storage_account.state.id # Control Plane (ARM), kein Key-Zugriff noetig
}

# --- Secrets (Werte erzeugt Terraform; liegen damit im geschuetzten Remote State) ---
resource "random_password" "jwt_signing_key" {
  length  = 64
  special = false
}

resource "random_password" "storage_encryption_key" {
  length  = 64
  special = false
}

resource "azurerm_key_vault_secret" "gateway_client_secret" {
  name         = "gateway-entra-client-secret"
  value        = var.gateway_client_secret
  key_vault_id = var.key_vault_id
  depends_on   = [var.key_vault_deployer_role_id]
}

resource "azurerm_key_vault_secret" "gateway_jwt_signing_key" {
  name         = "gateway-jwt-signing-key"
  value        = random_password.jwt_signing_key.result
  key_vault_id = var.key_vault_id
  depends_on   = [var.key_vault_deployer_role_id]
}

resource "azurerm_key_vault_secret" "gateway_storage_encryption_key" {
  name         = "gateway-storage-encryption-key"
  value        = random_password.storage_encryption_key.result
  key_vault_id = var.key_vault_id
  depends_on   = [var.key_vault_deployer_role_id]
}

resource "azurerm_linux_web_app" "gateway" {
  name                = local.app_name
  location            = var.location
  resource_group_name = var.resource_group_name
  service_plan_id     = azurerm_service_plan.gateway.id
  https_only          = true
  tags                = var.tags

  identity {
    type = "SystemAssigned"
  }

  site_config {
    always_on        = var.gateway_sku != "F1" # F1 unterstuetzt kein Always On
    http2_enabled    = true
    ftps_state       = "Disabled"
    app_command_line = "python -m uvicorn gateway.app:app --host 0.0.0.0 --port 8000 --workers 1 --proxy-headers --forwarded-allow-ips=*"

    application_stack {
      python_version = var.python_version
    }
  }

  app_settings = {
    "APPLICATIONINSIGHTS_CONNECTION_STRING" = var.application_insights_connection_string
    "SCM_DO_BUILD_DURING_DEPLOYMENT"        = "false" # Artefakt enthaelt vorgebaute Linux-Wheels
    "ENABLE_ORYX_BUILD"                     = "false"
    "WEBSITE_RUN_FROM_PACKAGE"              = "1" # Zip wird gemountet statt entpackt: spart F1-CPU-Minuten beim Deploy
    "WEBSITES_PORT"                         = "8000"
    "PYTHONPATH"                            = "/home/site/wwwroot/.python_packages/lib/site-packages"

    "AZURE_TENANT_ID"                = var.tenant_id
    "GATEWAY_ENTRA_CLIENT_ID"        = var.gateway_client_id
    "GATEWAY_ENTRA_CLIENT_SECRET"    = "@Microsoft.KeyVault(SecretUri=${azurerm_key_vault_secret.gateway_client_secret.versionless_id})"
    "GATEWAY_JWT_SIGNING_KEY"        = "@Microsoft.KeyVault(SecretUri=${azurerm_key_vault_secret.gateway_jwt_signing_key.versionless_id})"
    "GATEWAY_STORAGE_ENCRYPTION_KEY" = "@Microsoft.KeyVault(SecretUri=${azurerm_key_vault_secret.gateway_storage_encryption_key.versionless_id})"
    "MCP_APP_ID_URI"                 = var.mcp_app_identifier_uri
    "MCP_APP_CLIENT_ID"              = var.mcp_app_client_id
    "GATEWAY_PUBLIC_URL"             = local.public_url
    "BACKEND_MCP_URL"                = var.backend_mcp_url
    "GATEWAY_STORAGE_ACCOUNT"        = azurerm_storage_account.state.name
    "GATEWAY_STORAGE_TABLE"          = azurerm_storage_table.oauth_state.name
  }
}

resource "azurerm_role_assignment" "gateway_kv_secrets_user" {
  scope                = var.key_vault_id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_linux_web_app.gateway.identity[0].principal_id
}

resource "azurerm_role_assignment" "gateway_table_data" {
  scope                = azurerm_storage_account.state.id
  role_definition_name = "Storage Table Data Contributor"
  principal_id         = azurerm_linux_web_app.gateway.identity[0].principal_id
}
