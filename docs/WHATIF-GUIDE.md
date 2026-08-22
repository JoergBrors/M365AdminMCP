# What-If & Config-Diff – Empfehlung zur Konfiguration

## Terraform: `terraform plan` (empfohlener Weg)

Mit Terraform entfällt das Problem "zwei getrennte Diffs" aus dem Bicep-Abschnitt unten weitgehend:
Azure-Ressourcen (`azurerm_*`) **und** Entra-ID-Objekte (`azuread_*`) liegen im **selben State**, daher
zeigt ein einziger Lauf beide Änderungsarten zusammen:

```powershell
pwsh ./scripts/Invoke-TerraformPlan.ps1 -Environment dev
```

Das Skript:
1. `terraform init` + `terraform validate` (Syntaxfehler früh erkennen)
2. `terraform plan -out=<binary>` – der Plan wird als Binärdatei gesichert, damit `Invoke-TerraformApply.ps1`
   **exakt** diesen geprüften Plan anwendet und nicht zwischen Plan und Apply erneut plant (Race-Condition-Schutz)
3. `terraform show` als lesbarer Markdown-Report unter `reports/`
4. Zählt geplante `delete`-Aktionen über `terraform show -json | ConvertFrom-Json` (nativ in PowerShell, kein `jq` nötig) und warnt bei Funden

**Empfehlung:** In CI immer `-out=<file>` verwenden und denselben Plan-File im nachfolgenden Apply-Schritt
anwenden (nicht neu planen!) – das ist der Terraform-Standardweg, um sicherzustellen, dass genau das
angewendet wird, was zuvor reviewt wurde. `scripts/Invoke-TerraformApply.ps1` macht das automatisch.

**Sensible Outputs** (z. B. `mcp_app_client_secret`) sind in den `outputs.tf`-Dateien mit
`sensitive = true` markiert, damit sie nicht im Klartext in Plan-/Apply-Logs landen.

## Bicep: `az deployment ... what-if`

Der folgende Abschnitt gilt für den **alternativen Bicep-Weg** (`infra/`). Bei Bicep sind Azure-Ressourcen
und Entra-ID-Objekte (sofern Weg B/Preview genutzt wird) technisch getrennt, daher braucht es dort zwei
Diffs statt einem.

## Warum zwei getrennte Diffs nötig sind (nur relevant für den Bicep-Weg)

- `az deployment group what-if` deckt **ARM/Bicep-native Ressourcen** ab (App Service, Key Vault, App Insights, …) sehr gut.
- Die Entra-ID-Objekte (App-Registrierungen, App Roles, Permission Grants) sind **keine** klassischen ARM-Ressourcen. Selbst über die Microsoft-Graph-Bicep-Extension ist die `what-if`-Unterstützung (Stand jetzt) unvollständig.
- Deshalb macht `scripts/Invoke-BicepWhatIf.ps1` **beides**: den nativen Azure-what-if **und** einen eigenen Vergleich (`Get-EntraIdDiff.ps1`) zwischen Soll-Zustand (`infra/entra-desired-state/*.json`) und Ist-Zustand (`az ad app show` / Microsoft Graph).

## Empfohlene `what-if`-Einstellungen

```powershell
az deployment group what-if `
  --resource-group $ResourceGroupName `
  --template-file infra/main.bicep `
  --parameters "infra/parameters/main.$Environment.bicepparam" `
  --result-format FullResourcePayloads `
  --no-pretty-print
```

(Genau so implementiert in `Invoke-BicepWhatIf.ps1` – der Backtick ist unter PowerShell das Äquivalent zum
Backslash-Zeilenumbruch in Bash und funktioniert identisch unter macOS, Windows und Linux.)

- **`--result-format FullResourcePayloads`**: zeigt vollständige Properties statt nur geänderter IDs – wichtig, um versteckte Änderungen (z. B. an verschachtelten Objekten wie `siteConfig`) nicht zu übersehen. Für einen schnellen Überblick reicht `ResourceIdOnly`, für Reviews/Freigaben lieber `FullResourcePayloads`.
- Immer **vor** dem `what-if` ein `az bicep build --file infra/main.bicep` laufen lassen (macht `Invoke-BicepWhatIf.ps1` automatisch) – so werden Syntaxfehler früh erkannt, statt erst beim Deployment.
- `what-if` **pro Umgebung mit eigener Parameterdatei** ausführen, nie mit "geschätzten" Parametern – sonst ist der Diff wertlos.
- In der CI-Pipeline: What-If-Ergebnis **immer als Kommentar im Pull Request** posten (nicht nur in Logs verstecken) – das ist der eigentliche Review-Mechanismus vor dem Merge.

## Empfohlene Einstellungen für den Entra-Config-Diff

`scripts/Get-EntraIdDiff.ps1`:
- Vergleicht `displayName`, `signInAudience`, `api.oauth2PermissionScopes`, `appRoles`, `web.redirectUris` und bestehende `appRoleAssignments`
- Ignoriert bewusst volatile Felder (`id`, `createdDateTime`, `deletedDateTime`) – sonst wäre jeder Lauf ein "Diff", obwohl sich fachlich nichts geändert hat
- Schreibt das Ergebnis nach `reports/entra-diff-<env>-<timestamp>.md` im gleichen Stil wie der Azure-what-if-Report, damit beide zusammen reviewt werden können

## Wann NICHT automatisch deployen

- Wenn `Invoke-BicepWhatIf.ps1` eine **Löschung** ("Delete") einer Ressource anzeigt → niemals automatisiert weiterlaufen lassen, sondern manuell bestätigen (im Scaffold bereits so gebaut: `Invoke-BicepDeploy.ps1` bricht bei erkannten "Delete"-Einträgen ab und verlangt `--force`)
- Wenn der Entra-Diff eine Änderung an `signInAudience` oder eine Entfernung eines bestehenden App Roles zeigt, auf das produktive Clients sich verlassen → das würde bestehende Integrationen brechen

## Nächster Ausbauschritt (empfohlen, nicht Teil des MVP)

- Umstieg von reinem `what-if` auf **Deployment Stacks** (`az stack group create --deny-settings-mode denyDelete`), um zusätzlich laufende Drift-Erkennung (nicht nur vor dem nächsten Deployment) zu bekommen
- Policy-as-Code (Azure Policy / OPA) zusätzlich zum What-If, um Konfigurationsregeln (z. B. "TLS 1.2 minimum", "kein öffentlicher Blob-Zugriff") automatisiert vor dem Deployment zu erzwingen
