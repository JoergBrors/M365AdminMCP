# ADR-0007: Managed Identity in Azure, Client-Secret lokal, Key-Vault-Referenzen
- Status: Akzeptiert · Datum: 2026-10-16 (rückwirkend)

## Kontext
Die Dienste brauchen Tokens (MCP→API, API→Graph). In Azure sollen keine Secrets im Code oder in App Settings im Klartext liegen; lokal (VS-Code-Debug) gibt es keine Managed Identity.

## Entscheidung
- **Azure:** Beide Web Apps haben eine System-Assigned Managed Identity. Konfiguration `AzureAd__UseManagedIdentity=true` (MCP) bzw. `GraphAuth__UseManagedIdentity=true` (API).
  Die Managed Identity erhält die nötigen App-Rollen per Terraform.
- **Lokal:** `UseManagedIdentity=false` mit Client Secret aus `appsettings.Development.json` (per `.gitignore` ausgeschlossen, erzeugt von `scripts/Export-TerraformLocalSettings.ps1`).
- **Secrets in Azure:** Key Vault (RBAC-Modell, Purge Protection) + `@Microsoft.KeyVault(SecretUri=…)`-Referenzen in App Settings; Web Apps bekommen `Key Vault Secrets User`.
- `appsettings.json` enthält nur Platzhalter (`__REPLACE_WITH_…__`).

## Konsequenzen
+ Keine Klartext-Secrets in Azure/Git; Rotation über Terraform möglich.
− Zwei Codepfade pro Token-Service (MI vs. Secret) → beide testen.
− OBO funktioniert nur im Secret-Pfad (ADR-0006).
− `appsettings.Development.json` enthält ein echtes Secret lokal: nie committen (steht in `.gitignore`; `CopyToPublishDirectory="Never"` verhindert zusätzlich die Aufnahme ins Publish-Paket).

## Umsetzung
- `src/McpServer/Auth/ApiTokenService.cs` und `src/ApiServer/Auth/GraphTokenService.cs` (Verzweigung `ManagedIdentityCredential` vs. `ConfidentialClientApplication`).
- `src/*/Auth/*Options.cs`, `src/*/appsettings.json`, `src/*/*.csproj` (`Content Update="appsettings.Development.json"`).
- `terraform/modules/infra/main.tf`: `azurerm_key_vault`, `azurerm_key_vault_secret.*`, `azurerm_role_assignment.*_kv_secrets_user`, App Settings der Web Apps.
- `scripts/Export-TerraformLocalSettings.ps1`, `scripts/Set-GitHubActionsSettings.ps1`.

## Änderungs-Leitfaden
Neues Secret: `azurerm_key_vault_secret` ergänzen, per Key-Vault-Referenz in die App Settings der betroffenen App, lokal im Export-Skript. Auf Zertifikate umstellen: nur die Token-Service-Klassen
und die `azuread_application_password`-Ressourcen anfassen.
