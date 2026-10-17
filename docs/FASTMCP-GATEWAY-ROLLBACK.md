# FastMCP OAuth Gateway – Migration und Rollback

## Grundsatz
Das Gateway ist **additiv**. Die bestehende .NET-OAuth-Fassade (`src/McpServer/Auth/OAuthProxy.cs`), alle vorregistrierten Entra-Clients (ChatGPT, Claude, Copilot) und die URLs `https://entramcp-dev-mcp.azurewebsites.net/mcp` bleiben unverändert aktiv. Es wurde keine .NET-Datei geändert.

## Migration (nach positivem Gate-Ergebnis)
1. PR mit `enable_gateway=true` in `terraform.dev.tfvars`; PR-Plan mit CI-Identität prüfen (nur Gateway-Adds, keine Replace/Delete bestehender Ressourcen).
2. Merge löst Apply + Gateway-Deploy aus (nur durch Freigabe des Eigentümers).
3. Tests nach [FASTMCP-GATEWAY-TESTS.md](FASTMCP-GATEWAY-TESTS.md) mit **neuen** Connectoren auf die Gateway-URL; bestehende Connectoren unberührt lassen.
4. Umschalten einzelner Clients erst nach bestandenen Gates.

## Rollback
- **Clients:** auf die bisherige MCP-URL zurückstellen; die alten Registrierungen existieren weiter. Kein Eingriff am .NET nötig.
- **Infrastruktur:** `enable_gateway=false` setzen → Plan zeigt die Gateway-Ressourcen als Löschung (über `Invoke-TerraformApply.ps1` erfordert das bewusst `-Force`; einmalig, mit Rückfrage, danach nicht dauerhaft in `deploy.yml`). Key-Vault-Secrets sind durch Soft-Delete/Purge-Protection geschützt (`purge_protection_enabled = true`): gleichnamige Secrets lassen sich nach Löschung erst nach Ablauf der Aufbewahrung neu anlegen bzw. müssen wiederhergestellt werden.
- **Gateway-Registrierungen** liegen nur im Gateway-Store; sie gehen beim Löschen verloren (Clients registrieren sich erneut).
- Der Rollback-Test im Dev-Tenant wurde **nicht** durchgeführt.
