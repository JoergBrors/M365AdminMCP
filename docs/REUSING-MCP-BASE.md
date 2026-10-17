# Wiederverwendung der MCP-Basis

## Unverändert wiederverwendbar
| Baustein | Fundstelle | Hinweis |
|---|---|---|
| JWT-Validierung (Microsoft.Identity.Web, Audience-Liste) | `McpServer/Program.cs:44-57`, `ApiServer/Program.cs:22-36` | je Dienst eigene `AzureAd`-Werte |
| Token-Beschaffung App-only (MI / Secret) | `McpServer/Auth/ApiTokenService.cs` | OBO nur mit Secret |
| Report-Katalog und Validatoren | `McpServer/Reporting/`, `McpServer/Utils/`, `ApiServer/Services/ReportParameterValidator.cs` | per Compile-Link (ADR-0009) |
| Health-Client-Muster | `ApiServer/Health/` | als Vorlage für neue Datenquellen |
| OAuth-Fassade | `McpServer/Auth/OAuthProxy.cs` | entfernt `resource`, leitet an Entra |
| Python-Gateway | `src/OAuthGateway/` | konfigurierbar über Umgebungsvariablen (`gateway/config.py`) |
| Terraform | `modules/entra-id` (Apps, Rollen), `modules/infra` (KV, Plan, Apps, MI-Rollen), `modules/gateway` | Muster kopieren statt Variablen aufblähen |
| CI | `deploy.yml` (OIDC, Plan, Gitleaks, Audits) | pro Dienst Job ergänzen |

## Nicht wiederverwendbar ohne Anpassung
`TasksReadWrite`-Policy (zu breit), `GetTasksDelegated`, fest verdrahtete Namen (`entramcp-…`, Backend-Block in `providers.tf`), das Gateway als Einzelinstanz.

## Trennung zwischen MCPs
- Eigene Audience und Scope je MCP; Clients bekommen nur den Scope des MCP, den sie nutzen sollen.
- Managed Identity je MCP, Rollen nur dort, wo nötig; ein neuer MCP erbt nichts.
- Policies je Datenbereich im ApiServer statt einer Sammelpolicy (Migration: [security/SECURITY-DECISIONS.md](security/SECURITY-DECISIONS.md), D-1).
- Ein Gateway je MCP, damit Token-Audiences nicht vermischt werden.

## Vorgeschlagene Zielstruktur (Vorschlag, nicht umgesetzt)
```text
src/Shared/       # nur wenn zwei Dienste Code teilen (löst ADR-0009 ab)
src/ApiServer/  src/McpServer/  src/OAuthGateway/
terraform/modules/{entra-id,infra,gateway,mcp-service}
docs/{adr,security}
```
Verschoben wird erst, wenn ein zweiter MCP es nachweislich erfordert und Tests den Umbau absichern. Details: [EXTENDING-MCP-PLATFORM.md](EXTENDING-MCP-PLATFORM.md), [ROADMAP.md](ROADMAP.md).
