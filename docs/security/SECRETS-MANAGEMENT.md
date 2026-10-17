# Secrets-Management

| Secret | Ursprung | Ablage zur Laufzeit | Rotation |
|---|---|---|---|
| MCP-/API-App-Secret | `azuread_application_password` | Key Vault `mcp-server-client-secret`, `api-server-client-secret` → App Settings per Referenz | Terraform (End-Datum) |
| Copilot-Client-Secret | `azuread_application_password` | Key Vault `copilot-mcp-client-secret` | 8760 h (ADR-0005) |
| Gateway-Entra-Secret | `azuread_application_password` (4380 h) | Key Vault `gateway-entra-client-secret` | Terraform |
| Gateway-JWT-Signaturschlüssel | `random_password` (64 Zeichen) | Key Vault `gateway-jwt-signing-key` | **ungeprüft**; Wechsel macht ausgestellte Tokens ungültig |
| Gateway-Storage-Verschlüsselungsschlüssel | `random_password` | Key Vault `gateway-storage-encryption-key` | Wechsel macht gespeicherte Einträge unlesbar (Clients registrieren neu) |
| Foundry-Client-Secret | `New-FoundryMcpClient.ps1` | wird einmalig auf der Konsole ausgegeben, nie in eine Datei geschrieben | manuell (`az ad app credential reset`) |
| Lokal | `appsettings.Development.json`, `.env` | per `.gitignore` ausgeschlossen | – |

Regeln: keine Secrets in Git, im Deployment-Artefakt oder in GitHub-Secrets (nur OIDC-Identifikatoren). Das Gateway-Paket entsteht aus einer Dateiliste (`gateway/*.py`) mit Secret-Mustersuche und Gitleaks in CI. Secrets erscheinen zusätzlich im Terraform-State (F-013). Das Gateway maskiert OAuth-Parameter im Zugriffsprotokoll (F-010).

Nicht geprüft: Zugriffsprotokollierung des Key Vaults, Ablaufbenachrichtigungen, Rotationsautomatik.
