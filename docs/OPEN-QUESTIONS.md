# Offene Fragen

Stand 2026-10-17. Entscheidungen, die Sicherheitsgrenzen, Kosten, öffentliche Schnittstellen oder Berechtigungen ändern, trifft der Eigentümer. Priorität: P0 kritisch … P3 niedrig.

| ID | Frage | Hintergrund | ADRs | Optionen | Empfehlung | Sicherheitsauswirkung | Verantwortlich | Prio | Status |
|---|---|---|---|---|---|---|---|---|---|
| Q-ARCH-1 | Welcher OAuth-Weg gilt pro Client: .NET-Fassade oder Python-Gateway? | Zwei Wege mit je eigenen Regeln; ChatGPT scheitert am Gateway, Foundry nur über Fassade/Entra | 0004, 0017 | A: Fassade bleibt Standard, Gateway Opt-in; B: Gateway für DCR/CIMD-Clients, Fassade für Pre-Registration | A, bis Gates G3–G5 erfüllt; ADR-0017 danach entscheiden | mittel (zwei Regelquellen) | Eigentümer | P1 | offen |
| Q-GW-1 | Warum lehnt ChatGPT die Antwort ab („issuer does not match“)? | `iss` und Metadaten stimmen überein; Flag `false` ändert nichts; bekannter Codex-Bug #31573 in ähnlicher Form | 0017 | Client-Fix abwarten; Meldung an OpenAI; mit neuer Client-Version testen | warten/melden | gering | Eigentümer | P2 | offen, nicht gelöst |
| Q-GW-2 | Funktioniert Claude end-to-end (CIMD)? | URL-`client_id`-Fehler (500) behoben; Rückmeldung zum Gesamtablauf fehlt | 0017 | Test mit Claude | testen | – | Eigentümer | P1 | offen |
| Q-GW-3 | Copilot Studio gegen das Gateway („Dynamic discovery“)? | nicht getestet | 0017 | Test | testen | – | Eigentümer | P1 | offen |
| Q-GW-4 | Startzeit des Gateway-Containers (140–170 s) | Timeouts bei OAuth-Clients möglich | 0013, 0017 | Start optimieren, Warmup, größerer Plan | messen und optimieren | Verfügbarkeit | Entwicklung | P2 | offen |
| Q-AUTH-1 | Autorisierungsmodell | alle `Mcp.Access`-Nutzer sehen tenantweite Daten | 0006 | siehe SECURITY-DECISIONS D-1 | Rollen je Datenbereich | **hoch** | Eigentümer | P1 | Entscheidung offen |
| Q-AUTH-2 | `GetTasksDelegated` behalten? | Token als LLM-Parameter; OBO in Prod nicht möglich | 0006 | entfernen / OBO serverseitig | entfernen | mittel | Eigentümer | P1 | offen |
| Q-SEC-3 | `X-MCP-API-Key` abschaffen? | In Azure nicht gesetzt | 0014 | abschaffen / scopen | abschaffen | mittel | Eigentümer | P2 | offen |
| Q-SEC-4 | Rate Limits und Request-Größen | umgesetzt (Defaults 120/min je Aufrufer, OAuth 30/min je IP, Gateway `/register` 10/min); Wirkung mit echten Clients und Last nicht gemessen | 0010 | Werte nach Betriebserfahrung anpassen | beobachten (429-Rate) | mittel | Entwicklung | P2 | umgesetzt, Retest offen |
| Q-SEC-5 | State-Account RBAC prüfen | Secrets im State | 0001, 0007 | Review | prüfen | hoch | Eigentümer | P1 | offen |
| Q-AZ-1 | Netzwerkisolation | alles öffentlich erreichbar | 0013 | IP-Restriktion; Private Endpoint (kostenpflichtig) | IP-Restriktion prüfen | mittel | Eigentümer (Kosten) | P2 | offen |
| Q-AZ-2 | Gateway-Logs nach Log Analytics | Logs rotieren nach 1 Tag | – | Diagnoseeinstellung (kostenpflichtig nach Volumen) | einrichten | gering | Eigentümer (Kosten) | P2 | offen |
| Q-AZ-3 | Storage-Shared-Keys wieder abschalten | akzeptiert (F-012) | 0017 | `storage_use_azuread` + Data-Plane-Rolle für den Deployer | prüfen | gering | Entwicklung | P3 | offen |
| Q-TF-1 | IaC-Scanner (tfsec/Checkov/Trivy) | fehlt | 0012 | als nicht blockierender Job | einführen | gering | Entwicklung | P2 | offen |
| Q-TF-2 | Mehrere Umgebungen (staging/prod) | nur `dev` | 0012 | tfvars, State-Key, Federated Credential | bei Bedarf | – | Eigentümer | P3 | offen |
| Q-API-1 | Policies je Datenbereich (Migration) | siehe Q-AUTH-1 | 0006 | additive Einführung | umsetzen nach Entscheidung | hoch | Entwicklung | P1 | wartet auf Q-AUTH-1 |
| Q-API-2 | Report-Paging und Antwortgrößen | kein `nextLink`, unbegrenzt | 0010 | Limits, Paging | umsetzen | mittel | Entwicklung | P2 | offen |
| Q-EXT-1 | Gemeinsame Bibliothek statt Compile-Links | 3 Links | 0009 | `src/Shared` | mit dem zweiten MCP angehen | – | Entwicklung | P3 | offen |
| Q-EXT-2 | Testprojekt für den McpServer | keines vorhanden | 0004 | xUnit-Projekt | anlegen | mittel (Sicherheits-Regressionen) | Entwicklung | P2 | offen |
| Q-MCP-1 | Unterstützung neuerer MCP-Spezifikation (`2026-07-28`) | nur `2025-11-25` nachgewiesen | 0003 | Test mit neuerem SDK | prüfen, bevor behauptet wird | – | Entwicklung | P3 | offen |
| Q-OPS-1 | Kosten B1 | Preis nicht verifiziert | 0013 | Cost Management beobachten | nach einer Woche prüfen | – | Eigentümer | P2 | offen |
