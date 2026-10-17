# FastMCP OAuth Gateway – Kosten

**Keine Preise verifiziert.** Die Azure-Preisseiten lieferten in diesem Lauf keine USD-Werte; es werden keine Beträge genannt.

| Komponente | Abrechnung | Hinweis |
|---|---|---|
| App Service Plan `entramcp-dev-gateway-plan` (F1) | Free-Tier, kein Plan-Entgelt | Kontingent- und Quoten-Grenzen von F1 nicht verifiziert |
| Web App Python | im Plan enthalten | – |
| Storage Account (LRS, Standard) + Table `oauthstate` | nutzungsbasiert (Kapazität + Transaktionen) | bei dieser Datenmenge erfahrungsgemäß Cent-Bereich; **nicht verifiziert** |
| Key Vault | bestehend; 3 weitere Secrets | Operationen nutzungsbasiert |
| Application Insights / Log Analytics | bestehend; Tageslimit 1 GB (`log_analytics_daily_quota_gb`) | zusätzliche Telemetrie des Gateways |
| Entra | kein Aufpreis | – |

## Alternativen (nur Bewertung, nicht umgesetzt)
- **Redis** statt Azure Tables: zusätzlicher kostenpflichtiger Dienst; nicht gewählt, weil `py-key-value-aio` einen `AzureTablesStore` mit Managed-Identity-Auth mitbringt (im Code vorhanden; Betrieb gegen echten Storage nicht getestet).
- **Disk/Dateispeicher** auf F1: nicht zulässig als Persistenz (nicht robust über Neustarts/Deployments; Vorgabe schließt unsichere Dateispeicherung aus).
- Falls F1 oder Azure Tables ungeeignet sind, ist ein kostenpflichtiger Plan eine **Entscheidung des Nutzers**; es wird nichts automatisch erstellt.

G7 bleibt offen, bis die Kosten nach einigen Tagen Betrieb in Azure Cost Management abgelesen wurden.
