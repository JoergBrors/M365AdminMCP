# ADR-0013: Free-Tier (F1) App Service Plan
- Status: Akzeptiert · Datum: 2026-10-16 (rückwirkend)

## Kontext
Dev/Test-MVP, Kosten sollen minimal sein.

## Entscheidung
`app_service_sku` Default und dev-Wert `F1`. `always_on = !local.is_free_tier`. Log Analytics hat ein hartes Tageslimit (`log_analytics_daily_quota_gb = 1`).

## Konsequenzen
+ Praktisch kostenlos.
− Kein Always-On: Nach Inaktivität schläft die App ein; der erste Request kann 10–30 s dauern. Das ist **kein Bug** (kann bei MCP-Clients als Timeout beim Verbinden erscheinen: Verbindung einfach wiederholen).
− Free-Tier hat Rechen-/Speicherquoten (kein Skalieren) und keine Deployment-Slots.
− Stateless-Design (ADR-0003) erleichtert späteres Hochskalieren.

## Umsetzung
`terraform/variables.tf` (`app_service_sku`, `log_analytics_daily_quota_gb`), `terraform/terraform.dev.tfvars`, `terraform/modules/infra/main.tf` (`locals.is_free_tier`, `azurerm_service_plan.this`, `site_config.always_on`).

## Änderungs-Leitfaden
Produktivbetrieb: SKU auf B1 oder höher setzen (`always_on` schaltet sich automatisch ein), Kosten in `docs/DEPLOYMENT.md` aktualisieren, Application-Insights-Sampling prüfen.
