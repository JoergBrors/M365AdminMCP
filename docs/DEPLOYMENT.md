# Deployment

Es gibt zwei vollständige, unabhängige Deployment-Wege im Repo: **Terraform** (`terraform/`, empfohlen)
und **Bicep** (`infra/`, Alternative). Beide erzeugen dieselbe Zielarchitektur. Bitte nicht gleichzeitig
gegen dieselbe Umgebung laufen lassen (unterschiedliche State-Verwaltung → Konflikte).

Alle Skripte liegen als PowerShell 7 (`.ps1`) vor und laufen identisch unter macOS, Windows und Linux
(`pwsh`). Fehlende CLIs (`az`, `gh`, `terraform`) installiert `Install-Prerequisites.ps1` automatisch.

## Weg 1: Terraform (empfohlen)

### Einmalige Vorbereitung

```powershell
# Remote State Backend anlegen (einmalig pro Subscription/Tenant)
pwsh ./scripts/Initialize-TerraformBackend.ps1 -StorageAccountName <global-eindeutiger-name>
# -> Ausgabe in terraform/providers.tf im "backend \"azurerm\""-Block eintragen und Kommentar entfernen
```

### Laufender Betrieb

```powershell
Set-Location terraform; terraform init; Set-Location ..

# Plan (Azure-Ressourcen + Entra-ID-Objekte in einem Diff, siehe docs/WHATIF-GUIDE.md)
pwsh ./scripts/Invoke-TerraformPlan.ps1 -Environment dev

# Apply (wendet exakt den zuvor erzeugten Plan an, fragt vorher nach Bestätigung,
# bricht bei erkannten Löschungen ohne -Force ab)
pwsh ./scripts/Invoke-TerraformApply.ps1 -Environment dev
```

Umgebungen werden über `terraform/terraform.<env>.tfvars` unterschieden (Beispiel: `terraform.dev.tfvars`
liegt bereits im Repo, weitere nach Bedarf ergänzen).

## Weg 2: Bicep (Alternative)

Parameterdateien liegen in `infra/parameters/`, eine pro Umgebung (`main.dev.bicepparam`, weitere nach Bedarf ergänzen, z. B. `main.staging.bicepparam`, `main.prod.bicepparam`).

### Ablauf (lokal)

```powershell
# 1. Entra ID (idempotent, siehe docs/ENTRA-ID-SETUP.md)
pwsh ./scripts/Set-EntraIdApps.ps1 -Environment dev

# 2. What-If: zeigt Azure-Ressourcen-Diff + Entra-Config-Diff, schreibt Report nach reports/
pwsh ./scripts/Invoke-BicepWhatIf.ps1 -Environment dev

# 3. Deployment (fragt interaktiv nochmal nach Bestätigung, bevor "az deployment group create" läuft)
pwsh ./scripts/Invoke-BicepDeploy.ps1 -Environment dev
```

`Invoke-BicepDeploy.ps1` ist bewusst so gebaut, dass es **niemals ohne vorherigen What-If-Lauf** deployed – es ruft `Invoke-BicepWhatIf.ps1` selbst als ersten Schritt auf und bricht ab, wenn kein Report erzeugt wurde.

## Repo-Setup (GitHub CLI)

```powershell
pwsh ./scripts/New-GitHubRepo.ps1 -Name entra-mcp-mvp -Visibility private
```

Installiert `gh` automatisch, falls nicht vorhanden, meldet bei Bedarf per `gh auth login` an, initialisiert
`git` lokal (falls noch nicht geschehen) und legt das Repository inklusive erstem Push an.

## Ablauf (CI/CD, GitHub Actions)

`.github/workflows/deploy.yml`:
- **Pull Request** → Build + Unit Tests + `az deployment ... what-if` (via `Invoke-BicepWhatIf.ps1`, `shell: pwsh`) → Ergebnis als PR-Kommentar
- **Merge nach `main`** → Deployment in `dev` automatisch; `staging`/`prod` über GitHub Environments mit **Required Reviewers** (manuelle Freigabe), jeweils erneut mit What-If-Gate direkt davor

GitHub-gehostete Runner (`ubuntu-latest`, `windows-latest`, `macos-latest`) haben PowerShell 7 vorinstalliert –
`shell: pwsh` funktioniert auf allen dreien identisch, ohne zusätzliche Setup-Schritte.

Benötigte Secrets im Repo (Settings → Secrets and variables → Actions):
- `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` (für OIDC-Login via `azure/login`, kein langlebiges Secret nötig)

## Rollback

Da Deployments idempotent (Bicep, vollständiger Stack) sind: einfach den vorherigen Commit/Tag erneut deployen. Für Entra-ID-Änderungen: `Set-EntraIdApps.ps1` ist additiv/aktualisierend – ein "Rollback" bedeutet, die Soll-Zustands-JSON auf den alten Stand zu bringen und erneut auszuführen.

## Empfehlung: Deployment Stacks für Drift-Erkennung

Zusätzlich zum What-If (Momentaufnahme vor dem Deployment) empfiehlt sich für den weiteren Ausbau der Einsatz von **Azure Deployment Stacks** (`az stack group create`), da diese:
- verwaiste Ressourcen automatisch erkennen/aufräumen (`--action-on-unmanage`)
- eine dauerhafte Sicht auf "was gehört zu diesem Deployment" bieten, statt nur einen einmaligen Diff

Im MVP-Scope bewusst nicht enthalten, aber empfohlene nächste Ausbaustufe – siehe `docs/WHATIF-GUIDE.md`.
