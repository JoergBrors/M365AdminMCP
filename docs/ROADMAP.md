# Roadmap

Priorität P0–P3, Aufwand S/M/L (Schätzung, nicht gemessen). Abhängigkeiten in Klammern.

## Phase 1 – Stabilisierung
| Maßnahme | Prio | Aufwand | Abhängigkeit/Risiko |
|---|---|---|---|
| Claude und Copilot Studio gegen das Gateway testen (Q-GW-2/3) | P1 | S | echte Clients |
| ChatGPT-Fehler bei OpenAI melden / neue Client-Version testen (Q-GW-1) | P2 | S | Fix liegt beim Client |
| Gateway-Startzeit messen und senken (Q-GW-4) | P2 | M | – |
| Gateway-Logs nach Log Analytics (Q-AZ-2) | P2 | S | Kosten |
| Testprojekt für den McpServer (Q-EXT-2) | P2 | M | – |
| ADR-0017 abschließen, sobald G3–G5 erfüllt sind | P2 | S | Tests |

## Phase 2 – Security Hardening
| Maßnahme | Prio | Aufwand | Abhängigkeit/Risiko |
|---|---|---|---|
| Rate Limits, Request-Limits, HSTS (Q-SEC-4) | P1 | M | **umgesetzt**; Kompatibilität mit echten Clients und Live-Retest offen |
| Autorisierungsmodell und Policies je Datenbereich (Q-AUTH-1, Q-API-1) | P1 | L | Entscheidung D-1; Migration additiv, Clients nicht brechen |
| ~~`GetTasksDelegated` entfernen~~ erledigt; API-Key abschaffen (Q-SEC-3) | P2 | S | Entscheidung |
| State-Account-RBAC prüfen (Q-SEC-5) | P1 | S | Zugriff |
| SAST, IaC-Scanner, SBOM in CI (Q-TF-1) | P2 | M | Rauschen anfangs |
| Pentest-Vorbereitung abschließen, Freigabe einholen | P2 | M | Eigentümer |

## Phase 3 – MCP-Plattform
| Maßnahme | Prio | Aufwand | Abhängigkeit/Risiko |
|---|---|---|---|
| `src/Shared` für Katalog/Validatoren statt Compile-Links (Q-EXT-1) | P3 | M | Build-Umbau mit Tests |
| Terraform-Modul `mcp-service` (App Service + Entra-App + KV-Anteil) aus bestehenden Bausteinen | P2 | M | [EXTENDING-MCP-PLATFORM.md](EXTENDING-MCP-PLATFORM.md) |
| Referenz-MCP (z. B. zweiter Datenbereich) | P3 | M | Modul fertig |
| Gateway mehrmandantenfähig (mehrere Backends/Audiences) | P3 | L | Sicherheitsdesign |

## Phase 4 – Produktionsreife
| Maßnahme | Prio | Aufwand | Abhängigkeit/Risiko |
|---|---|---|---|
| Produktionsgeeignete Pläne (Always On, Slots) | P2 | S | Kosten |
| Netzwerkisolation (Private Endpoints/IP-Restriktion) (Q-AZ-1) | P2 | M/L | Kosten, Tarif |
| Observability (App Insights/OpenTelemetry im Gateway, Alarme) | P2 | M | – |
| Disaster Recovery (State, Secrets, Wiederanlauf) | P2 | M | – |
| Externes Security-Audit und Pentest | P2 | M | Freigabe |
| Last- und Skalierungstests | P3 | M | Freigabe |
