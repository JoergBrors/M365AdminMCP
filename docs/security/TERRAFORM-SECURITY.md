# Terraform- und CI-Sicherheit

| Thema | Stand |
|---|---|
| State | Remote, Azure Storage `stgeuwmcpo365dev` (RG `rg-tfstate`), Key `entra-mcp-mvp.tfstate`. **RBAC und Netzwerkregeln des State-Accounts wurden nicht geprüft** (F-013) |
| Secrets im State | ja: `azuread_application_password`, `random_password` (Gateway-Schlüssel), Key-Vault-Secret-Werte. `sensitive = true` verhindert nur die Anzeige. Write-only-Attribute wurden nicht eingesetzt (Provider-Unterstützung nicht geprüft) |
| CI-Identität | GitHub-OIDC, Federated Credential, kein Client-Secret. Principal `sp_keyvault`: *Contributor* auf `rg-entramcp-dev`, *RBAC Administrator* auf Subscription und Key Vault (per `az rest` gelesen) |
| Löschschutz | `Invoke-TerraformApply.ps1` bricht bei geplanten Löschungen ohne `-Force` ab (live beobachtet). `-Force` nie dauerhaft im Workflow |
| PR-Plan | Plan-Kommentar im PR; Drift wird sichtbar (z. B. manuell ergänzte Redirect-URI) |
| Statische Analyse | `terraform fmt -check`, `terraform validate` im Plan-Job. **Keine** IaC-Scanner (tfsec/Checkov/Trivy) – offen (ROADMAP Phase 2) |
| Manuelle Eingriffe | dokumentiert: Gateway-Logging (jetzt Terraform), Storage-Shared-Key einmalig per `az`, Foundry-App per Skript außerhalb des States |
| Admin-Consent | Terraform-Grant für das Gateway scheiterte (403: CI-Principal ohne Graph-Recht); bewusst nicht erweitert, Nutzer-Consent stattdessen |

## Empfehlungen
1. RBAC und Firewall des State-Accounts prüfen (nur CI-Principal und Administratoren; kein öffentlicher Zugriff).
2. IaC-Scan als zeitintensiven Security-Job ergänzen (nicht blockierend, bis die Findings bereinigt sind).
3. Rechte des CI-Principals regelmäßig überprüfen (RBAC Administrator auf Subscription-Ebene ist weitreichend).
