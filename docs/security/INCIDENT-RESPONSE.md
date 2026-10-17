# Incident Response (Kurzfassung)

| Szenario | Sofortmaßnahme | Danach |
|---|---|---|
| Verdacht auf Secret-Leck (Entra-Secret, Gateway-Schlüssel) | Secret in Entra/Key Vault erneuern (Terraform-Apply oder `az ad app credential reset`), betroffene App neu starten | Logs und Git-Historie prüfen, Finding anlegen |
| Gateway-Signaturschlüssel kompromittiert | Key-Vault-Secret `gateway-jwt-signing-key` ersetzen → alle FastMCP-Tokens ungültig, Clients melden sich neu an | Ursache klären |
| Gateway-Missbrauch (Massenregistrierung) | Gateway-App stoppen (`az webapp stop`) oder Storage-Tabelle leeren; Clients registrieren sich neu | Rate Limit (F-011) |
| Verdächtige Tool-Aufrufe | `Mcp.Access` für den Nutzer in Entra entziehen; Zugriff auf die MCP-App sperren | Logs sichern (Kudu läuft nur 1 Tag!) |
| Ausfall des Gateways | Clients auf die .NET-Fassade zurückstellen ([FASTMCP-GATEWAY-ROLLBACK.md](../FASTMCP-GATEWAY-ROLLBACK.md)) | Ursache, Log, Fix per PR |
| Kompromittierter CI-Zugang | Federated Credential des CI-Principals entfernen, Rollen prüfen | `main`-Historie und Workflows prüfen |

Nachweise sichern, bevor Logs rotieren (Container-Logs: 1 Tag/35 MB). Kontakt: Eigentümer.
