# Security-Checkliste vor Releases

- [ ] `dotnet build`, `dotnet test` grün; NuGet-Audit ohne Treffer
- [ ] `pytest`, `ruff`, `pip-audit`, Gitleaks im Gateway grün
- [ ] Gateway-Paket in leerer Umgebung importierbar (CI-Schritt)
- [ ] Terraform-Plan geprüft: **keine** ungeplanten Löschungen/Replace bestehender Ressourcen, keine neuen Graph-/ARM-Rechte
- [ ] Keine Secrets im Diff (Gitleaks) und nicht in Logs
- [ ] `McpAuth__RequireAuthentication` ist in Azure `true`; `McpAuth__ApiKey` nicht gesetzt (oder bewusst begründet)
- [ ] `GATEWAY_DEBUG_ERRORS` und `GATEWAY_ADVERTISE_ISS_PARAM` nicht auf Nicht-Standardwerten (`az webapp config appsettings list`)
- [ ] Neue Tools: lesend? Autorisierung je Tool dokumentiert? Eingaben validiert (Whitelist/Format)?
- [ ] Neue Redirect-URIs nur über Terraform oder das dokumentierte Skript
- [ ] Neue Berechtigungen im ADR/Security-Decisions begründet und freigegeben
- [ ] Findings-Register aktualisiert ([SECURITY-FINDINGS.md](SECURITY-FINDINGS.md))
