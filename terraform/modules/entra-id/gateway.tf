# --- FastMCP OAuth Gateway: zentrale Upstream-App (nur wenn enable_gateway = true) ---
#
# Confidential client (web{}-Plattform, Secret) mit EINER festen Callback-URL. Das Gateway registriert
# Downstream-Clients selbst (DCR/CIMD) und leitet nur ueber diese App nach Entra weiter. Die App erhaelt
# ausschliesslich die delegierte Berechtigung Mcp.Access auf die bestehende MCP-App - keine Graph-
# Application-Permissions, keine Azure-Rollen.

variable "enable_gateway" {
  description = "Legt die Upstream-App fuer das FastMCP OAuth Gateway an."
  type        = bool
  default     = false
}

variable "gateway_redirect_uri" {
  description = "Feste Callback-URL des Gateways (https://<host>/auth/callback)."
  type        = string
  default     = ""
}

resource "azuread_application" "gateway" {
  count = var.enable_gateway ? 1 : 0

  display_name     = "oauth-gateway-${var.environment_name}"
  sign_in_audience = "AzureADMyOrg"

  web {
    redirect_uris = [var.gateway_redirect_uri]
  }

  required_resource_access {
    resource_app_id = azuread_application.mcp.client_id

    resource_access {
      id   = azuread_application.mcp.oauth2_permission_scope_ids["Mcp.Access"]
      type = "Scope"
    }
  }
}

resource "azuread_service_principal" "gateway" {
  count     = var.enable_gateway ? 1 : 0
  client_id = azuread_application.gateway[0].client_id
}

# Hinweis: Der Secret-Wert liegt (wie alle azuread_application_password-Secrets dieses Repos) im Terraform-State.
resource "azuread_application_password" "gateway" {
  count             = var.enable_gateway ? 1 : 0
  application_id    = azuread_application.gateway[0].id
  display_name      = "terraform-managed-secret-${var.environment_name}"
  end_date_relative = "4380h"
}

# Bewusst KEIN Admin-Consent-Grant: dem CI-Principal fehlt dafuer ein Graph-Recht (403 im Apply vom 2026-10-17) und
# dieses Recht wird nicht erweitert. Der Nutzer stimmt Mcp.Access beim ersten Login selbst zu.

output "gateway_client_id" {
  value = one(azuread_application.gateway[*].client_id)
}

output "gateway_client_secret" {
  value     = one(azuread_application_password.gateway[*].value)
  sensitive = true
}
