environment_name             = "dev"
location                     = "westeurope"
app_service_sku              = "F1" # Free-Tier - kostenlos fuer Dev/Test
log_analytics_daily_quota_gb = 1    # deckelt Log-Analytics-Kosten auf Cent-Beträge

chatgpt_mcp_redirect_uris = [
  "https://chatgpt.com/connector/oauth/KWbJcncpjoR9"
]
