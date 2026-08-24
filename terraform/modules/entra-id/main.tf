# ============================================================================
# Entra-ID-Provisionierung über den hashicorp/azuread Provider.
#
# Anders als die Microsoft-Graph-Bicep-Extension (siehe infra/modules/entra-id.bicep,
# dort als optionaler Preview-Pfad markiert) ist dieser Terraform-Provider stabil
# und produktionsreif. Das ist der empfohlene Weg für die Entra-ID-Objekte in
# diesem Repo, sobald Terraform als IaC-Tool verwendet wird.
#
# Legt an:
#   - api-server:  zentrale API mit App Role, Delegated Scope und Microsoft-Graph-App-Permissions
#   - mcp-server:  Client-App mit Application Permission auf api-server
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

  single_page_application {
    redirect_uris = var.api_swagger_redirect_uris
  }

  api {
    requested_access_token_version = 2

    oauth2_permission_scope {
      id                         = random_uuid.api_delegated_scope.result
      admin_consent_description  = "Allow the app to read/write tasks on behalf of the signed-in user"
      admin_consent_display_name = "Tasks.ReadWrite"
      value                      = "Tasks.ReadWrite"
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

  # Microsoft Graph: Office 365 Status, Message Center, Adoption/Usage Reports (alle App-only).
  # Diese Berechtigungen liegen bewusst auf der API, damit MCP keine Graph-Tokens mehr anfordert.
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

resource "random_uuid" "api_delegated_scope" {}
resource "random_uuid" "api_app_role" {}

resource "azuread_service_principal" "api" {
  client_id = azuread_application.api.client_id
}

resource "azuread_application_password" "api" {
  application_id    = azuread_application.api.id
  display_name      = "terraform-managed-local-debug-secret-${var.environment_name}"
  end_date_relative = "8760h" # lokal/debug fallback; Azure nutzt bevorzugt Managed Identity
}

# --- swagger-client (Browser/SPAs fuer lokale Swagger UI) ---

resource "azuread_application" "swagger" {
  display_name     = "swagger-client-${var.environment_name}"
  sign_in_audience = "AzureADMyOrg"

  single_page_application {
    redirect_uris = var.api_swagger_redirect_uris
  }

  required_resource_access {
    resource_app_id = azuread_application.api.client_id

    resource_access {
      id   = azuread_application.api.oauth2_permission_scope_ids["Tasks.ReadWrite"]
      type = "Scope"
    }
  }
}

resource "azuread_service_principal" "swagger" {
  client_id = azuread_application.swagger.client_id
}

# --- mcp-server (Client App) ---

resource "azuread_application" "mcp" {
  display_name     = "mcp-server-${var.environment_name}"
  sign_in_audience = "AzureADMyOrg"

  identifier_uris = ["api://mcp-server-${var.environment_name}"]

  web {
    redirect_uris = coalesce(var.mcp_redirect_uris, [var.mcp_redirect_uri])
    implicit_grant {
      access_token_issuance_enabled = false
      id_token_issuance_enabled     = true
    }
  }

  api {
    requested_access_token_version = 2

    oauth2_permission_scope {
      id                         = random_uuid.mcp_access_scope.result
      admin_consent_description  = "Allow ChatGPT and other MCP clients to access the MCP server on behalf of the signed-in user"
      admin_consent_display_name = "Access MCP server"
      value                      = "Mcp.Access"
      type                       = "User"
      enabled                    = true
      user_consent_description   = "Allow this client to access the MCP server on your behalf"
      user_consent_display_name  = "Access MCP server"
    }
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

}

resource "random_uuid" "mcp_access_scope" {}

resource "azuread_service_principal" "mcp" {
  client_id = azuread_application.mcp.client_id
}

resource "azuread_application_password" "mcp" {
  application_id    = azuread_application.mcp.id
  display_name      = "terraform-managed-secret-${var.environment_name}"
  end_date_relative = "8760h" # 1 Jahr - für Produktion Zertifikat statt Secret erwägen
}

# --- externe MCP OAuth Clients (ChatGPT / Claude / Copilot Studio) ---

locals {
  mcp_oauth_clients = {
    chatgpt = {
      display_name  = "chatgpt-mcp-client-${var.environment_name}"
      redirect_uris = var.chatgpt_mcp_redirect_uris
    }
    claude = {
      display_name  = "claude-mcp-client-${var.environment_name}"
      redirect_uris = var.claude_mcp_redirect_uris
    }
  }
}

resource "azuread_application" "mcp_oauth_client" {
  for_each = local.mcp_oauth_clients

  display_name     = each.value.display_name
  sign_in_audience = "AzureADMyOrg"

  # WICHTIG: "publicClient"-Plattform (NICHT "web", NICHT "spa"). ChatGPT/Claude/Copilot Studio
  # sind serverseitige Connectoren, die den Auth-Code NICHT aus gleichem Browser-JS-Origin
  # einloesen (kein echter SPA-Client, obwohl der initiale Redirect durch den Browser des
  # Nutzers laeuft). Drei Fallstricke, alle bereits durchlaufen:
  #   - "web" (auch MIT fallback_public_client_enabled=true) => Entra klassifiziert den Client-
  #     Typ per authorization_code-Flow anhand der Redirect-URI-PLATTFORM, nicht anhand von
  #     isFallbackPublicClient - eine "web"-Redirect-URI erzwingt IMMER confidential client
  #     (client_secret/client_assertion Pflicht, AADSTS7000218), unabhaengig vom Flag.
  #     isFallbackPublicClient wirkt nur bei Flows OHNE redirect_uri (Device Code, ROPC).
  #   - "spa"-Plattform loest Entras Origin-basierte Cross-Origin-PKCE-Pruefung aus, die nur
  #     fuer echte Browser-JS-Redemption gedacht ist; eine serverseitige Einloesung ohne
  #     Origin-Header (aber ueber eine spa-Redirect-URI) scheitert mit AADSTS9002325.
  #   - Fix: "publicClient"-Redirect-URI (Terraform: public_client-Block) - https-URLs zu
  #     Drittanbieter-Domains sind dort ausdruecklich erlaubt (kein Custom-Scheme/localhost-
  #     Zwang), und der Grant-Type authorization_code+PKCE wird korrekt als public client ohne
  #     Secret behandelt.
  # https://learn.microsoft.com/troubleshoot/entra/entra-id/app-integration/confidential-client-application-authentication-error-aadsts7000218#how-microsoft-entra-id-determines-the-client-type
  # https://learn.microsoft.com/entra/identity-platform/reply-url
  fallback_public_client_enabled = true

  public_client {
    redirect_uris = each.value.redirect_uris
  }

  required_resource_access {
    resource_app_id = azuread_application.mcp.client_id

    resource_access {
      id   = azuread_application.mcp.oauth2_permission_scope_ids["Mcp.Access"]
      type = "Scope"
    }
  }
}

resource "azuread_service_principal" "mcp_oauth_client" {
  for_each = azuread_application.mcp_oauth_client

  client_id = each.value.client_id
}

# Kein Client Secret fuer diese Apps: public-client + PKCE braucht keins (siehe Kommentar oben
# bei azuread_application.mcp_oauth_client) - ChatGPT/Claude senden am Token-Endpunkt
# "Authentifizierungsmethode: none".

# --- Copilot Studio: eigener confidential client (Sonderfall) ---
#
# Copilot Studios "Manuell"-Konfigurationstyp fuer OAuth-2.0-MCP-Connectors verlangt zwingend
# ein "Geheimer Clientschluessel"-Feld (Pflichtfeld im UI) - anders als ChatGPT/Claude, die
# einen echten public client (Authentifizierungsmethode "none") nutzen. Um das zu bedienen,
# braucht dieser Client eine "web"-Redirect-URI (siehe Kommentar oben: eine "web"-Redirect-URI
# zwingt Entra IMMER zu confidential-client-Verhalten, unabhaengig von
# fallback_public_client_enabled) statt der public_client-Plattform der anderen MCP-Clients.
resource "azuread_application" "copilot_mcp_oauth_client" {
  display_name     = "copilot-mcp-client-${var.environment_name}"
  sign_in_audience = "AzureADMyOrg"

  web {
    redirect_uris = var.copilot_mcp_redirect_uris
  }

  required_resource_access {
    resource_app_id = azuread_application.mcp.client_id

    resource_access {
      id   = azuread_application.mcp.oauth2_permission_scope_ids["Mcp.Access"]
      type = "Scope"
    }
  }
}

resource "azuread_service_principal" "copilot_mcp_oauth_client" {
  client_id = azuread_application.copilot_mcp_oauth_client.client_id
}

resource "azuread_application_password" "copilot_mcp_oauth_client" {
  application_id    = azuread_application.copilot_mcp_oauth_client.id
  display_name      = "terraform-managed-secret-${var.environment_name}"
  end_date_relative = "8760h"
}

resource "azuread_service_principal_delegated_permission_grant" "copilot_mcp_oauth_client_delegated_to_mcp" {
  service_principal_object_id          = azuread_service_principal.copilot_mcp_oauth_client.object_id
  resource_service_principal_object_id = azuread_service_principal.mcp.object_id
  claim_values                         = ["Mcp.Access", "offline_access"]
}

# --- Admin Consent / App Role Assignments (App-only) ---

resource "azuread_app_role_assignment" "mcp_to_api_task_readwrite" {
  app_role_id         = azuread_application.api.app_role_ids["Tasks.ReadWrite.All"]
  principal_object_id = azuread_service_principal.mcp.object_id
  resource_object_id  = azuread_service_principal.api.object_id
}

resource "azuread_app_role_assignment" "api_to_graph_service_health" {
  app_role_id         = data.azuread_service_principal.msgraph.app_role_ids["ServiceHealth.Read.All"]
  principal_object_id = azuread_service_principal.api.object_id
  resource_object_id  = data.azuread_service_principal.msgraph.object_id
}

resource "azuread_app_role_assignment" "api_to_graph_service_message" {
  app_role_id         = data.azuread_service_principal.msgraph.app_role_ids["ServiceMessage.Read.All"]
  principal_object_id = azuread_service_principal.api.object_id
  resource_object_id  = data.azuread_service_principal.msgraph.object_id
}

resource "azuread_app_role_assignment" "api_to_graph_reports" {
  app_role_id         = data.azuread_service_principal.msgraph.app_role_ids["Reports.Read.All"]
  principal_object_id = azuread_service_principal.api.object_id
  resource_object_id  = data.azuread_service_principal.msgraph.object_id
}

# --- Admin Consent für den Delegated Scope (On-Behalf-Of) ---

resource "azuread_service_principal_delegated_permission_grant" "mcp_delegated_to_api" {
  service_principal_object_id          = azuread_service_principal.mcp.object_id
  resource_service_principal_object_id = azuread_service_principal.api.object_id
  claim_values                         = ["Tasks.ReadWrite"]
}

resource "azuread_service_principal_delegated_permission_grant" "swagger_delegated_to_api" {
  service_principal_object_id          = azuread_service_principal.swagger.object_id
  resource_service_principal_object_id = azuread_service_principal.api.object_id
  claim_values                         = ["Tasks.ReadWrite"]
}

resource "azuread_service_principal_delegated_permission_grant" "mcp_oauth_client_delegated_to_mcp" {
  for_each = azuread_service_principal.mcp_oauth_client

  service_principal_object_id          = each.value.object_id
  resource_service_principal_object_id = azuread_service_principal.mcp.object_id
  # "offline_access" gehoert explizit HIERHER (gegen mcp-server), nicht implizit gegen
  # Microsoft Graph - sonst grantet Entra bei manchen Client-Consent-Fluessen einen separaten,
  # unvollstaendigen Grant gegen Graph, was zu AADSTS65001 ("not consented") fuehrt, obwohl
  # Mcp.Access bereits gewaehrt ist (beobachtet bei Claude, das scope="Mcp.Access offline_access"
  # gemeinsam gegen dieselbe Ressource sendet).
  claim_values = ["Mcp.Access", "offline_access"]
}
