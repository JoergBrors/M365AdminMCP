# ADR-0001: Terraform als einziger Deployment-Weg
- Status: Akzeptiert · Datum: 2026-10-16 (rückwirkend dokumentiert)

## Kontext
Das Repo enthält zwei IaC-Linien: `infra/` (Bicep + `scripts/Set-EntraIdApps.ps1`, `Invoke-Bicep*.ps1`) und `terraform/`.
Azure-Ressourcen **und** Entra-ID-Objekte (App-Registrierungen, Rollen, Grants) müssen konsistent und im selben State verwaltet werden.

## Entscheidung
Nur Terraform (`azurerm` + `azuread` + `random`) wird für Deployment verwendet. Bicep bleibt als Legacy im Repo und ist nicht in CI verdrahtet.

## Alternativen
- Bicep + Graph-Bicep-Extension: Entra-Teil ist Preview, kein gemeinsamer State mit Skript-basierten Entra-Objekten.
- Skripte (`Set-EntraIdApps.ps1`): imperativ, kein Drift-Schutz.

## Konsequenzen
+ Ein State (`stgeuwmcpo365dev` / `rg-tfstate` / `entra-mcp-mvp.tfstate`), Plan/Apply-Review.
− Legacy-Skripte parallel zu nutzen erzeugt State-Drift.
− Die Terraform-Identität braucht Graph-App-Rollen (`Application.ReadWrite.All`, `Directory.Read.All`).

## Umsetzung
- `terraform/main.tf` (Root: Resource Group, Module `entra_id` + `infra`), `terraform/providers.tf` (Backend `azurerm`, `use_oidc = true`).
- `terraform/modules/entra-id/` (Entra-Objekte), `terraform/modules/infra/` (Azure-Ressourcen).
- Wrapper: `scripts/Invoke-TerraformPlan.ps1`, `scripts/Invoke-TerraformApply.ps1`.

## Änderungs-Leitfaden
Neue Ressource → im passenden Modul ergänzen. Neue Variable → Root-`variables.tf` **und** Modul-`variables.tf` **und** `terraform.dev.tfvars`.
Nie `terraform plan/apply` direkt, immer über die Skripte (Löschwarnung, siehe ADR-0012). `infra/` nicht als Vorbild nehmen.
