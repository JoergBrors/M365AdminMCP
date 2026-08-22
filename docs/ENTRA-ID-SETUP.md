# Entra ID Provisionierung

Es gibt jetzt **drei** Wege, alle liegen im Repo:

| Weg | Reife | Empfehlung |
|---|---|---|
| A) `scripts/Set-EntraIdApps.ps1` (Azure CLI) | Stabil | Guter Standard ohne Terraform-Abhängigkeit |
| B) `infra/modules/entra-id.bicep` (Microsoft Graph Bicep Extension) | **Preview** | Nur bewusst, siehe Warnhinweise unten |
| C) `terraform/modules/entra-id` (hashicorp/azuread Provider) | **Stabil, GA** | **Empfohlen, sobald ohnehin Terraform genutzt wird** (siehe unten) |

Alle drei legen dieselben Objekte an: `api-server`, `mcp-server`, die App-Role-/Delegated-Permission-Grants
zwischen beiden, sowie inzwischen zusätzlich die Microsoft-Graph-Application-Permissions
`ServiceHealth.Read.All`, `ServiceMessage.Read.All`, `Reports.Read.All` für die Office-365-Status-/
Nachrichten-/Adoption-Tools des MCP Servers (siehe `docs/ARCHITECTURE.md`).

## Weg A (empfohlen): `scripts/Set-EntraIdApps.ps1`

Nutzt die Azure CLI (`az ad app`, `az ad sp`, Microsoft Graph über `az rest`). Vorteile:
- Stabil, keine Preview-Abhängigkeit
- Idempotent: erneutes Ausführen erkennt bestehende Apps (per `displayName`-Tag) und aktualisiert statt zu duplizieren
- Legt automatisch an:
  - App Registration `api-server` mit App Role `Tasks.ReadWrite.All` und Delegated Scope `Tasks.ReadWrite`
  - App Registration `mcp-server` mit Redirect URI, Client Secret
  - Service Principals für beide
  - App-Role-Assignment: `mcp-server` → `api-server` (Application Permission)
  - OAuth2-Permission-Grant: `mcp-server` → `api-server` (Delegated Permission), inkl. Admin Consent
  - Schreibt Client Secret + IDs in Key Vault (`az keyvault secret set`)
  - Schreibt eine **Soll-Zustands-Datei** nach `infra/entra-desired-state/*.json` (Basis für den Config-Diff in `Invoke-BicepWhatIf.ps1`)

Aufruf:
```powershell
pwsh ./scripts/Set-EntraIdApps.ps1 -Environment dev   # z.B. dev, staging, prod
```

Benötigte Berechtigung des ausführenden Kontos: **Application Administrator** oder **Cloud Application Administrator** in Entra ID, plus Consent-Recht (oder ein separater Admin führt `az ad app permission admin-consent` aus).

## Weg B (optional/Preview): Microsoft Graph Bicep Extension

`infra/modules/entra-id.bicep` zeigt, wie dieselben Objekte deklarativ per Bicep angelegt werden könnten (`Microsoft.Graph/applications`, `Microsoft.Graph/servicePrincipals`, `Microsoft.Graph/appRoleAssignedTo` über die Microsoft-Graph-Bicep-Extension).

**Wichtig:**
- Diese Extension ist eine **Preview-Funktion** von Bicep (`extensibility`-Feature) – Syntax/Ressourcentypen können sich ändern
- Muss explizit aktiviert werden (`infra/bicepconfig.json` → `experimentalFeaturesEnabled.extensibility: true`) und erfordert eine aktuelle Bicep-CLI-Version
- Das ausführende Deployment-Principal braucht Microsoft-Graph-Berechtigungen (`Application.ReadWrite.All`), nicht nur Azure-RBAC – bei Pipeline-Identitäten (Managed Identity/Service Principal) muss das separat als App-Role-Assignment auf Microsoft Graph vergeben werden
- **`what-if` deckt Microsoft-Graph-Ressourcen nur eingeschränkt/gar nicht ab** – deshalb ersetzt Weg B den Config-Diff aus `Invoke-BicepWhatIf.ps1` nicht; dieser bleibt weiterhin nötig

→ Nutze Weg B nur, wenn du bewusst mit Preview-Features arbeiten willst, sonst bleib bei Weg A oder C.

## Weg C (empfohlen bei Terraform-Einsatz): `terraform/modules/entra-id`

Nutzt den offiziellen `hashicorp/azuread`-Provider – im Gegensatz zu Weg B **kein Preview-Feature**,
sondern ein produktionsreif dokumentierter Terraform-Provider. Vorteile gegenüber Weg A:

- **Ein gemeinsamer State** für Azure-Ressourcen UND Entra-ID-Objekte → `terraform plan` zeigt beide
  Diffs in einem Lauf (siehe `docs/WHATIF-GUIDE.md`, Abschnitt Terraform) – kein separates `Get-EntraIdDiff.ps1` nötig
- Drift-Erkennung "for free": läuft ein Admin manuell eine Änderung an der App-Registrierung, zeigt der
  nächste `terraform plan` das automatisch als Abweichung an
- Idempotenz und Rollback sind Kernfunktionen von Terraform, nicht selbstgebaut wie in `Set-EntraIdApps.ps1`

Enthält:
- `azuread_application` für `api-server` (App Role + Delegated Scope) und `mcp-server`
- `azuread_app_role_assignment` für: `mcp-server → api-server` (App-only) sowie `mcp-server → Microsoft Graph`
  für `ServiceHealth.Read.All`, `ServiceMessage.Read.All`, `Reports.Read.All`
- `azuread_service_principal_delegated_permission_grant` für den Delegated Scope (Admin Consent für OBO)
- `azuread_application_password` als Client Secret, landet über das Infra-Modul im Key Vault

**Berechtigung des ausführenden Principals:** wie bei Weg A – Application Administrator (oder höher) in
Entra ID. Bei CI/CD-Ausführung über eine Service Principal/Managed Identity muss dieser Prinzipal selbst
`Application Administrator` (Entra-ID-Rolle, nicht Azure-RBAC!) zugewiesen bekommen.

Aufruf: siehe `docs/DEPLOYMENT.md`, Abschnitt "Terraform".

## Manuelle Kontrolle / Nachvollziehbarkeit

Nach dem Setup kannst du die erzeugten Objekte jederzeit prüfen:

```powershell
az ad app list --display-name api-server -o json | ConvertFrom-Json | Format-List
az ad app list --display-name mcp-server -o json | ConvertFrom-Json | Format-List
az ad app permission list --id <mcp-server-app-id>
```

## Rückbau

```powershell
pwsh ./scripts/Set-EntraIdApps.ps1 -Environment dev -Destroy
```

Entfernt die angelegten App-Registrierungen und Service Principals wieder (nur für Dev/Test-Umgebungen gedacht).
