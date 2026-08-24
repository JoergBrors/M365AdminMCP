environment_name             = "dev"
location                     = "westeurope"
app_service_sku              = "F1" # Free-Tier - kostenlos fuer Dev/Test
log_analytics_daily_quota_gb = 1    # deckelt Log-Analytics-Kosten auf Cent-Beträge

chatgpt_mcp_redirect_uris = [
  "https://chatgpt.com/connector/oauth/KWbJcncpjoR9",
  "https://chatgpt.com/connector/oauth/4WbYBsSow_8L",
  "https://chatgpt.com/connector/oauth/Mw_WfO7C7DHQ"
]

claude_mcp_redirect_uris = [
  "https://claude.ai/api/mcp/auth_callback"
]

# Power Platform Custom-Connector-OAuth-Callback fuer Copilot Studio. Falls der Dialog beim
# Verbinden eine abweichende/tenant-spezifische Callback-URL anzeigt, diese hier ergaenzen statt
# ersetzen (mehrere Werte moeglich).
copilot_mcp_redirect_uris = [
  "https://global.consent.azure-apim.net/redirect",
  "https://global.consent.azure-apim.net/redirect/crea8-5fmcpservero365-5f6a2fde1466b59a2e"
]
