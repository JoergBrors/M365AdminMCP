# ADR-0005: Vorregistrierte OAuth-Clients (public vs. confidential)
- Status: Akzeptiert · Datum: 2026-10-16 (rückwirkend)

## Kontext
Entra ID bietet weder Dynamic Client Registration (RFC 7591) noch CIMD. Die Connector-UIs von ChatGPT, Claude und Copilot Studio
brauchen eine feste Client-ID. Entra bestimmt den Client-Typ im Auth-Code-Flow nach der **Plattform der Redirect-URI**, nicht nach
`fallback_public_client_enabled`.

## Entscheidung
Pro Ziel-Client eine eigene App-Registrierung per Terraform (Pre-Registration):
- ChatGPT, Claude: **Public Client** (`public_client{}` + `fallback_public_client_enabled = true`, PKCE, kein Secret).
- Copilot Studio: **Confidential Client** (`web{}` + `azuread_application_password`), weil das UI ein Secret-Feld verlangt.
- Alle erhalten den Delegated Grant `Mcp.Access` + `offline_access` **gegen `mcp-server`** (vermeidet `AADSTS65001`).
Faustregel: verlangt die Connector-UI ein Secret → `web{}`, sonst `public_client{}`. Nicht `spa{}` (löst `AADSTS9002325` aus).

## Konsequenzen
+ Funktioniert ohne DCR; Redirect-URIs sind in Entra strikt kontrolliert.
− Jede neue ChatGPT-Connector-Instanz erzeugt eine neue Callback-URI → Nachtrag in Terraform/Skript nötig.
− Das Copilot-Secret läuft nach 8760 h ab (Terraform erneuert es beim nächsten Plan).

## Umsetzung
- `terraform/modules/entra-id/main.tf`: `local.mcp_oauth_clients`, `azuread_application.mcp_oauth_client` (`for_each`),
  `azuread_application.copilot_mcp_oauth_client`, `azuread_service_principal_delegated_permission_grant.*`.
- Variablen: Root- und Modul-`variables.tf` (`*_mcp_redirect_uris`), Werte in `terraform/terraform.dev.tfvars`.
- Schnelle Redirect-Ergänzung: `scripts/Add-McpOauthRedirectUri.ps1`.
- Client-IDs/Secret im Key Vault: `terraform/modules/infra/main.tf` (`azurerm_key_vault_secret.mcp_oauth_client_id`, `copilot_mcp_client_secret`).

## Änderungs-Leitfaden
Neuer Public Client: Eintrag in `local.mcp_oauth_clients` + Variable (Root + Modul) + tfvars. Neuer Confidential Client: eigener Ressourcenblock
nach dem Vorbild Copilot. Immer den Plan über `Invoke-TerraformPlan.ps1` prüfen.
