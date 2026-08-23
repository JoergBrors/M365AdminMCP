# Entra ID + MCP/API Server – MVP Entwicklungsumgebung

Fertiges Starter-Repo für:
- **API Server** (ASP.NET Core, .NET 10 LTS) – geschützt via Entra ID, unterstützt **App-only** (Client Credentials) **und** **Delegated** (Auth Code / On-Behalf-Of) Tokens
- **MCP Server** (ASP.NET Core, .NET 10 LTS + `ModelContextProtocol.AspNetCore` v2, Streamable HTTP, stateless) – ruft den API Server sowohl app-only als auch im Namen eines angemeldeten Nutzers auf, und liest zusätzlich tenant-weit **Office 365 Status** (Service Health), **Message Center** (Nachrichten) und **Adoption/Usage Reports** direkt über Microsoft Graph. Vorregistrierte OAuth-Clients für **ChatGPT**, **Claude** und **Copilot Studio** (siehe `docs/DEPLOYMENT.md`, Abschnitt "MCP OAuth Clients")
- **Infrastruktur als Code** – **Terraform** (empfohlen, `terraform/`) und alternativ **Bicep** (`infra/`) für Azure App Service, Key Vault, Log Analytics/App Insights
- **Entra ID Provisionierung** – per Terraform (`hashicorp/azuread`, stabil), per Skript (Azure CLI, robust) oder optional per Bicep über die (Preview-)Microsoft-Graph-Bicep-Extension
- **What-If / Config-Diff** – bei Terraform in einem gemeinsamen `terraform plan` (Azure + Entra ID im selben State); bei Bicep als zwei getrennte Diffs (`az deployment ... what-if` + eigener Entra-Diff)

## Ordnerstruktur

```
entra-mcp-mvp/
├── src/
│   ├── ApiServer/        # ASP.NET Core Web API, Microsoft.Identity.Web
│   └── McpServer/        # MCP Server (HTTP/SSE): Tasks, Office 365 Status/Nachrichten/Adoption
├── terraform/            # EMPFOHLENER Deployment-Weg
│   ├── main.tf, providers.tf, variables.tf, outputs.tf
│   ├── terraform.dev.tfvars
│   └── modules/
│       ├── entra-id/     # azuread_application, App Roles, Graph-Permissions, Admin Consent
│       └── infra/        # azurerm: App Service Plan, 2 Linux Web Apps, Key Vault, Monitoring
├── infra/                # Alternative: Bicep
│   ├── main.bicep
│   ├── modules/          # appservice.bicep, keyvault.bicep, monitoring.bicep, entra-id.bicep (Preview)
│   ├── parameters/       # *.bicepparam pro Umgebung
│   └── entra-desired-state/ # Soll-Zustand der App-Registrierungen (JSON) für den Bicep-Config-Diff
├── scripts/                                          # PowerShell 7 (.ps1) - macOS/Windows/Linux
│   ├── Connect-Azure.ps1                             # liest .env, az login/account set auf Tenant+Subscription
│   ├── Install-Prerequisites.ps1                     # installiert az, gh, terraform falls fehlend
│   ├── New-GitHubRepo.ps1                            # Repo-Setup via GitHub CLI (gh)
│   ├── Initialize-TerraformBackend.ps1, Invoke-TerraformPlan.ps1, Invoke-TerraformApply.ps1
│   ├── Set-EntraIdApps.ps1, Invoke-BicepWhatIf.ps1, Invoke-BicepDeploy.ps1
│   ├── Add-McpOauthRedirectUri.ps1                    # neue ChatGPT/Claude/Copilot-Redirect-URI nachtragen
│   └── Get-EntraIdDiff.ps1                           # nur für Bicep-Weg separat nötig
├── docs/
│   ├── ARCHITECTURE.md
│   ├── ENTRA-ID-SETUP.md
│   ├── DEPLOYMENT.md
│   └── WHATIF-GUIDE.md
├── .env.example                                      # Vorlage für .env (Tenant/Subscription, nicht committen)
└── .github/workflows/deploy.yml
```

## Voraussetzungen

- .NET 10 SDK (aktuelle LTS) – `Install-Prerequisites.ps1` installiert es automatisch, falls keine SDK-Version ≥ 10 gefunden wird
- **PowerShell 7** (`pwsh`) – alle Skripte in `scripts/` sind `.ps1` und laufen identisch unter macOS, Windows und Linux
- Azure CLI (`az`) ≥ 2.60, eingeloggt mit einem Konto mit **Application Administrator** (Entra-ID-Rolle) und **Contributor** auf der Ziel-Subscription/Resource Group
- **GitHub CLI** (`gh`) – für das Repo-Setup; wird von `Install-Prerequisites.ps1` automatisch installiert, falls nicht vorhanden
- **Terraform** ≥ 1.7 (empfohlener Weg) und/oder Bicep CLI ≥ 0.30 (Alternative, kommt mit aktuellem `az`)
- Ein Entra-ID-Tenant, in dem du App-Registrierungen anlegen darfst
- Lokale `.env` (aus `.env.example` kopiert) mit `AZURE_TENANT_ID` und `AZURE_SUBSCRIPTION_ID` – wird von `scripts/Connect-Azure.ps1` gelesen, das alle Deployment-Skripte automatisch als ersten Schritt aufrufen

Fehlende CLIs (az, gh, terraform) lassen sich in einem Rutsch installieren:

```powershell
pwsh ./scripts/Install-Prerequisites.ps1
```

## Schnellstart

```powershell
# 1) Repo lokal + auf GitHub anlegen (gh CLI wird bei Bedarf automatisch installiert)
pwsh ./scripts/New-GitHubRepo.ps1 -Name entra-mcp-mvp -Visibility private

# 2) Tenant/Subscription hinterlegen (einmalig)
Copy-Item .env.example .env
# .env öffnen und AZURE_TENANT_ID / AZURE_SUBSCRIPTION_ID eintragen

# 3) Anmelden (alle scripts/*.ps1, die Azure-Ressourcen anlegen/ändern, rufen dies automatisch
#    selbst als ersten Schritt auf - manueller Aufruf hier nur zur Kontrolle)
pwsh ./scripts/Connect-Azure.ps1
```

### Terraform (empfohlen)

```powershell
# einmalig: Remote-State-Backend anlegen
pwsh ./scripts/Initialize-TerraformBackend.ps1 -StorageAccountName <global-eindeutiger-name>
# -> Ausgabe in terraform/providers.tf eintragen

cd terraform; terraform init; cd ..

pwsh ./scripts/Invoke-TerraformPlan.ps1 -Environment dev    # Diff für Azure-Ressourcen UND Entra-ID-Objekte in einem Lauf
pwsh ./scripts/Invoke-TerraformApply.ps1 -Environment dev   # fragt vor dem Apply nochmal nach, bricht bei Löschungen ab
```

### Bicep (Alternative)

```powershell
pwsh ./scripts/Set-EntraIdApps.ps1 -Environment dev    # Entra ID vorbereiten (idempotent)
pwsh ./scripts/Invoke-BicepWhatIf.ps1 -Environment dev # Azure what-if + Entra Config-Diff
pwsh ./scripts/Invoke-BicepDeploy.ps1 -Environment dev # Deployment (fragt vor dem Apply nochmal nach)
```

Details, offene Design-Fragen und Empfehlungen zur What-If-Konfiguration: siehe `docs/`.

## Wichtiger Hinweis

Dies ist ein **MVP-Scaffold**, kein produktionsfertiges System. Insbesondere:
- Die Microsoft-Graph-Bicep-Extension ist (Stand jetzt) eine Preview-Funktion mit sich änderndem Schema – deshalb ist der **Skript-Weg (`Set-EntraIdApps.ps1`) oder Terraform der empfohlene Standard**, die Bicep-Extension-Variante liegt nur als Option daneben.
- Secrets werden lokal nur temporär gehalten und landen im Key Vault – nie im Klartext ins Git-Repo committen (`.gitignore` ist entsprechend vorbereitet).
