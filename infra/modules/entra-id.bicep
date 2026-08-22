// ============================================================================
// OPTIONAL / PREVIEW: Entra-ID-Objekte per Microsoft Graph Bicep Extension.
//
// Standardweg für dieses Repo ist scripts/entra-setup.sh (siehe docs/ENTRA-ID-SETUP.md).
// Dieses Modul zeigt, wie dieselben Objekte deklarativ per Bicep angelegt werden
// könnten, sobald du bewusst mit der (experimentellen) Microsoft-Graph-Bicep-
// Extension arbeiten willst. Voraussetzungen:
//   - infra/bicepconfig.json: experimentalFeaturesEnabled.extensibility = true
//   - Aktuelle Bicep-CLI-Version, die die "extension microsoftGraph" Direktive kennt
//   - Deployment-Principal braucht Microsoft-Graph-Berechtigung Application.ReadWrite.All
//     (NICHT nur Azure-RBAC!)
//   - `az deployment ... what-if` deckt diese Ressourcen nur eingeschränkt ab,
//     siehe docs/WHATIF-GUIDE.md -> entra-diff.sh bleibt zusätzlich nötig.
//
// Schema/Ressourcennamen können sich mit neueren Extension-Versionen ändern -
// vor Nutzung gegen die aktuelle Microsoft-Dokumentation der Graph-Bicep-Extension
// prüfen.
// ============================================================================

extension microsoftGraph

param apiAppDisplayName string = 'api-server'
param mcpAppDisplayName string = 'mcp-server'
param mcpRedirectUri string

resource apiApp 'Microsoft.Graph/applications@v1.0' = {
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
  displayName: mcpAppDisplayName
  signInAudience: 'AzureADMyOrg'
  web: {
    redirectUris: [
      mcpRedirectUri
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

output apiAppId string = apiApp.appId
output mcpAppId string = mcpApp.appId
