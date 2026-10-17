output "gateway_app_name" {
  value = azurerm_linux_web_app.gateway.name
}

output "gateway_hostname" {
  value = azurerm_linux_web_app.gateway.default_hostname
}

output "gateway_public_url" {
  value = local.public_url
}
