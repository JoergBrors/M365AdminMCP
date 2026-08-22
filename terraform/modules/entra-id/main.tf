# ============================================================================
# Entra-ID-Provisionierung über den hashicorp/azuread Provider.
#
# Anders als die Microsoft-Graph-Bicep-Extension (siehe infra/modules/entra-id.bicep,
# dort als optionaler Preview-Pfad markiert) ist dieser Terraform-Provider stabil
# und produktionsreif. Das ist der empfohlene Weg für die Entra-ID-Objekte in
# diesem Repo, sobald Terraform als IaC-Tool verwendet wird.
#
# Legt an:
#   - api-server:  eigene API mit App Role (App-only) + Delegated Scope
#   - mcp-server:  Client-App mit
#       a) Application Permission auf api-server (App-only)
#       b) Delegated Permission auf api-server (On-Behalf-Of)
#       c) Application Permissions auf Microsoft Graph für
#          Office 365 Service Health (Status), Message Center (Nachrichten)
#          und Usage Reports (Adoption):
#            - ServiceHealth.Read.All
#            - ServiceMessage.Read.All
#            - Reports.Read.All
#          (alle drei erfordern Admin Consent, siehe app_role_assignment unten)
# ============================================================================

data "azuread_client_config" "current" {}

data "azuread_application_published_app_ids" "well_known" {}

data "azuread_service_principal" "msgraph" {
  client_id = data.azuread_application_published_app_ids.well_known.result["MicrosoftGraph"]
}

# --- api-server (Resource App) ---

resource "azuread_application" "api" {
  display_name     = "api-server-${var.environment_name}"
  sign_in_audience = "AzureADMyOrg"

  identifier_uris = ["api://api-server-${var.environment_name}"]

  api {
    requested_access_token_version = 2

    oauth2_permission_scope {
      id                         = random_uuid.api_delegated_scope.result
      admin_consent_description  = "Allow the app to read/write tasks on behalf of the signed-in user"
      admin_consent_display_name = "Tasks.ReadWrite"
      value                       = "Tasks.ReadWrite"
      type                       = "User"
      enabled                    = true
      user_consent_description   = "Allow the app to read/write your tasks"
      user_consent_display_name  = "Read/write your tasks"
    }
  }

  app_role {
    id                   = random_uuid.api_app_role.result
    allowed_member_types = ["Application"]
    description          = "App-only read/write access to tasks"
    display_name         = "Tasks.ReadWrite.All"
    value                = "Tasks.ReadWrite.All"
    enabled              = true
  }
}

resource "random_uuid" "api_delegated_scope" {}
resource "random_uuid" "api_app_role" {}

resource "azuread_service_principal" "api" {
  client_id = azuread_application.api.client_id
}

# --- mcp-server (Client App) ---

resource "azuread_application" "mcp" {
  display_name     = "mcp-server-${var.environment_name}"
  sign_in_audience = "AzureADMyOrg"

  web {
    redirect_uris = [var.mcp_redirect_uri]
  }

  # Application Permission (App-only) + Delegated Scope auf api-server
  required_resource_access {
    resource_app_id = azuread_application.api.client_id

    resource_access {
      id   = azuread_application.api.app_role_ids["Tasks.ReadWrite.All"]
      type = "Role"
    }
    resource_access {
      id   = azuread_application.api.oauth2_permission_scope_ids["Tasks.ReadWrite"]
      type = "Scope"
    }
  }

  # Microsoft Graph: Office 365 Status, Message Center, Adoption/Usage Reports (alle App-only)
  required_resource_access {
    resource_app_id = data.azuread_service_principal.msgraph.client_id

    resource_access {
      id   = data.azuread_service_principal.msgraph.app_role_ids["ServiceHealth.Read.All"]
      type = "Role"
    }
    resource_access {
      id   = data.azuread_service_principal.msgraph.app_role_ids["ServiceMessage.Read.All"]
      type = "Role"
    }
    resource_access {
      id   = data.azuread_service_principal.msgraph.app_role_ids["Reports.Read.All"]
      type = "Role"
    }
  }
}

resource "azuread_service_principal" "mcp" {
  client_id = azuread_application.mcp.client_id
}

resource "azuread_application_password" "mcp" {
  application_id = azuread_application.mcp.id
  display_name   = "terraform-managed-secret-${var.environment_name}"
  end_date_relative = "8760h" # 1 Jahr - für Produktion Zertifikat statt Secret erwägen
}

# --- Admin Consent / App Role Assignments (App-only) ---

resource "azuread_app_role_assignment" "mcp_to_api_task_readwrite" {
  app_role_id         = azuread_application.api.app_role_ids["Tasks.ReadWrite.All"]
  principal_object_id = azuread_service_principal.mcp.object_id
  resource_object_id  = azuread_service_principal.api.object_id
}

resource "azuread_app_role_assignment" "mcp_to_graph_service_health" {
  app_role_id         = data.azuread_service_principal.msgraph.app_role_ids["ServiceHealth.Read.All"]
  principal_object_id = azuread_service_principal.mcp.object_id
  resource_object_id  = data.azuread_service_principal.msgraph.object_id
}

resource "azuread_app_role_assignment" "mcp_to_graph_service_message" {
  app_role_id         = data.azuread_service_principal.msgraph.app_role_ids["ServiceMessage.Read.All"]
  principal_object_id = azuread_service_principal.mcp.object_id
  resource_object_id  = data.azuread_service_principal.msgraph.object_id
}

resource "azuread_app_role_assignment" "mcp_to_graph_reports" {
  app_role_id         = data.azuread_service_principal.msgraph.app_role_ids["Reports.Read.All"]
  principal_object_id = azuread_service_principal.mcp.object_id
  resource_object_id  = data.azuread_service_principal.msgraph.object_id
}

# --- Admin Consent für den Delegated Scope (On-Behalf-Of) ---

resource "azuread_service_principal_delegated_permission_grant" "mcp_delegated_to_api" {
  service_principal_object_id          = azuread_service_principal.mcp.object_id
  resource_service_principal_object_id = azuread_service_principal.api.object_id
  claim_values                         = ["Tasks.ReadWrite"]
}
