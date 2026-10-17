# Zusaetzliche Outputs fuer das optionale Gateway-Modul (rein lesend, keine neuen Ressourcen).

output "key_vault_id" {
  value = azurerm_key_vault.this.id
}

output "key_vault_deployer_role_id" {
  description = "Damit abhaengige Module erst nach der Secrets-Officer-Rolle des Deployers Secrets anlegen."
  value       = azurerm_role_assignment.deployer_kv_admin.id
}

output "application_insights_connection_string" {
  value     = azurerm_application_insights.this.connection_string
  sensitive = true
}
