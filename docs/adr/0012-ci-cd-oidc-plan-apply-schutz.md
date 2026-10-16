# ADR-0012: CI/CD – OIDC, Plan im PR, Apply nur auf `main`, Lösch-Sicherheitsnetz
- Status: Akzeptiert · Datum: 2026-10-16 (rückwirkend)

## Kontext
Infrastruktur und Entra-Objekte ändern sich über denselben Terraform-State. Fehlerhafte Änderungen (v. a. Löschungen von App-Registrierungen) sind schwer rückgängig zu machen. Langlebige Secrets in GitHub sollen vermieden werden.

## Entscheidung
- `main` ist geschützt: kein Direct-Push, Pflicht-PR, Pflicht-Checks `build` + `deploy-dev`. Nur ein Merge nach `main` löst das Deployment aus; Feature-Branches deployen nie.
- Workflow `.github/workflows/deploy.yml`: Job `build` (`dotnet build EntraMcpMvp.sln`) → bei **Pull Request** `terraform-plan` (Plan-Report als PR-Kommentar), bei **Push auf `main`** `deploy-dev` (Environment `dev`: `terraform apply -AutoApprove`, `dotnet publish` beider Dienste, `azure/webapps-deploy`).
- Authentifizierung per **GitHub-OIDC** (`azure/login@v2` + `ARM_USE_OIDC`), kein Client-Secret. Federated Credential mit numerischem Subject-Format (`repo:<owner>@<ownerId>/<repo>@<repoId>:environment:dev`).
- `Invoke-TerraformApply.ps1` bricht bei geplanten Löschungen ohne `-Force` ab. `-Force` darf **nie** dauerhaft in den Workflow; nur einmalig mit Rückfrage und danach sofort entfernen.
- App-Namen aus Terraform-Outputs werden validiert (`*-api`, `*-mcp`), bevor deployt wird.

## Konsequenzen
+ Jede Änderung ist vor dem Merge als Plan sichtbar; versehentliche Löschungen stoppen das Apply.
− Der `deploy-dev`-Job wendet Terraform und Code in einem Lauf an; ein Fehler dazwischen lässt Infrastruktur und Code zeitweise inkonsistent.
− Es gibt nur die Umgebung `dev`; weitere Umgebungen brauchen neue tfvars, State-Key, Federated Credential und Workflow-Jobs.
− Der Backend-Block in `providers.tf` ist auf `stgeuwmcpo365dev` fest verdrahtet.

## Umsetzung
`.github/workflows/deploy.yml`, `scripts/Invoke-TerraformPlan.ps1`, `scripts/Invoke-TerraformApply.ps1`, `scripts/Connect-Azure.ps1`, `terraform/providers.tf`, `docs/DEPLOYMENT.md` (CI-Bootstrap), `docs/WHATIF-GUIDE.md`.

## Änderungs-Leitfaden
Neue Umgebung: `terraform.<env>.tfvars`, eigener State-Key, Federated Credential, `environment:`-Job. Änderungen am Workflow immer per Feature-Branch/PR testen (der Plan-Job läuft dort). Fehlende Azure-/Entra-Rolle in CI: Scope und Rollenname benennen und Rückfrage stellen, nie Rechte umgehen.
