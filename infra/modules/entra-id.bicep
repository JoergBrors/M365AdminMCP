// ============================================================================
// OPTIONAL / PREVIEW: Entra-ID-Objekte per Microsoft Graph Bicep Extension.
//
// Standardweg für dieses Repo ist scripts/entra-setup.sh (siehe docs/ENTRA-ID-SETUP.md).
// Dieses Modul zeigt, wie dieselben Objekte deklarativ per Bicep angelegt werden
// könnten, sobald du bewusst mit der (experimentellen) Microsoft-Graph-Bicep-
// Extension arbeiten willst. Voraussetzungen:
//   - infra/bicepconfig.json: experimentalFeaturesEnabled.extensibility = true
//   - Aktuelle Bicep-CLI-Version (>= 0.36.1) + "extensions"-Alias "graphV1" in infra/bicepconfig.json
//     (die alte eingebaute "extension microsoftGraph"-Direktive wurde von Microsoft im März 2025
//     retired, siehe https://aka.ms/graphbicep/dynamictypes - Ersatz: dynamic types via MCR-Registry)
//   - Deployment-Principal braucht Microsoft-Graph-Berechtigung Application.ReadWrite.All
//     (NICHT nur Azure-RBAC!)
//   - `az deployment ... what-if` deckt diese Ressourcen nur eingeschränkt ab,
//     siehe docs/WHATIF-GUIDE.md -> entra-diff.sh bleibt zusätzlich nötig.
//
// Schema/Ressourcennamen können sich mit neueren Extension-Versionen ändern -
// vor Nutzung gegen die aktuelle Microsoft-Dokumentation der Graph-Bicep-Extension
// prüfen.
// ============================================================================

extension graphV1

param apiAppDisplayName string = 'api-server'
param mcpAppDisplayName string = 'mcp-server'
param mcpRedirectUri string

@description('Redirect URIs fuer ChatGPT/Claude/Copilot Studio MCP-OAuth-Clients. Leer = Client wird ohne Redirect-URI angelegt (spaeter manuell in Entra ergaenzen).')
param chatgptRedirectUris array = []
param claudeRedirectUris array = [
  'https://claude.ai/api/mcp/auth_callback'
]
param copilotRedirectUris array = []

resource apiApp 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: apiAppDisplayName
  displayName: apiAppDisplayName
  signInAudience: 'AzureADMyOrg'
  api: {
    requestedAccessTokenVersion: 2
    oauth2PermissionScopes: [
      {
        id: guid(apiAppDisplayName, 'delegated-scope')
        adminConsentDisplayName: 'Read/write tasks on behalf of the signed-in user'
        adminConsentDescription: 'Allows the app to read/write tasks on behalf of the signed-in user.'
        userConsentDisplayName: 'Read/write your tasks'
        userConsentDescription: 'Allows the app to read/write your tasks.'
        value: 'Tasks.ReadWrite'
        type: 'User'
        isEnabled: true
      }
    ]
  }
  appRoles: [
    {
      id: guid(apiAppDisplayName, 'app-role')
      displayName: 'Tasks.ReadWrite.All'
      description: 'Allows the app to read/write all tasks without a signed-in user.'
      value: 'Tasks.ReadWrite.All'
      allowedMemberTypes: [ 'Application' ]
      isEnabled: true
    }
  ]
}

resource apiSp 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: apiApp.appId
}

resource mcpApp 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: mcpAppDisplayName
  displayName: mcpAppDisplayName
  signInAudience: 'AzureADMyOrg'
  web: {
    redirectUris: [
      mcpRedirectUri
    ]
  }
  // Delegated Scope fuer externe MCP-OAuth-Clients (ChatGPT/Claude/Copilot Studio), siehe
  // mcpOAuthClients weiter unten.
  api: {
    requestedAccessTokenVersion: 2
    oauth2PermissionScopes: [
      {
        id: guid(mcpAppDisplayName, 'mcp-access-scope')
        adminConsentDisplayName: 'Access MCP server'
        adminConsentDescription: 'Allow MCP clients (ChatGPT, Claude, Copilot Studio) to access the MCP server on behalf of the signed-in user'
        userConsentDisplayName: 'Access MCP server'
        userConsentDescription: 'Allow this client to access the MCP server on your behalf'
        value: 'Mcp.Access'
        type: 'User'
        isEnabled: true
      }
    ]
  }
  requiredResourceAccess: [
    {
      resourceAppId: apiApp.appId
      resourceAccess: [
        {
          id: apiApp.appRoles[0].id
          type: 'Role'
        }
        {
          id: apiApp.api.oauth2PermissionScopes[0].id
          type: 'Scope'
        }
      ]
    }
  ]
}

resource mcpSp 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: mcpApp.appId
}

// App-Role-Assignment (Application Permission) mcp-server -> api-server
resource appRoleAssignment 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: apiApp.appRoles[0].id
  principalId: mcpSp.id
  resourceId: apiSp.id
}

// --- Externe MCP-OAuth-Clients (ChatGPT/Claude/Copilot Studio) ---
// WICHTIG: "web"-Plattform + isFallbackPublicClient=true (NICHT "spa"), siehe Kommentar in
// terraform/modules/entra-id/main.tf und docs/DEPLOYMENT.md ("MCP OAuth Clients") fuer die
// AADSTS7000218/AADSTS9002325-Historie dieser Entscheidung.
var mcpOAuthClients = [
  {
    key: 'chatgpt'
    displayName: 'chatgpt-mcp-client'
    redirectUris: chatgptRedirectUris
  }
  {
    key: 'claude'
    displayName: 'claude-mcp-client'
    redirectUris: claudeRedirectUris
  }
  {
    key: 'copilot'
    displayName: 'copilot-mcp-client'
    redirectUris: copilotRedirectUris
  }
]

resource mcpOAuthClientApps 'Microsoft.Graph/applications@v1.0' = [for client in mcpOAuthClients: {
  uniqueName: client.displayName
  displayName: client.displayName
  signInAudience: 'AzureADMyOrg'
  isFallbackPublicClient: true
  web: {
    redirectUris: client.redirectUris
  }
  requiredResourceAccess: [
    {
      resourceAppId: mcpApp.appId
      resourceAccess: [
        {
          id: mcpApp.api.oauth2PermissionScopes[0].id
          type: 'Scope'
        }
      ]
    }
  ]
}]

resource mcpOAuthClientSps 'Microsoft.Graph/servicePrincipals@v1.0' = [for (client, i) in mcpOAuthClients: {
  appId: mcpOAuthClientApps[i].appId
}]

output apiAppId string = apiApp.appId
output mcpAppId string = mcpApp.appId
output mcpOAuthClientIds object = {
  chatgpt: mcpOAuthClientApps[0].appId
  claude: mcpOAuthClientApps[1].appId
  copilot: mcpOAuthClientApps[2].appId
}
