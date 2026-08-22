output "api_app_hostname" {
  value = azurerm_linux_web_app.api.default_hostname
}

output "mcp_app_hostname" {
  value = azurerm_linux_web_app.mcp.default_hostname
}

output "key_vault_name" {
  value = azurerm_key_vault.this.name
}
