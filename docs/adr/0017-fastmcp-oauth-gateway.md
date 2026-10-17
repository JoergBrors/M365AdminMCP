# ADR-0017: FastMCP OAuth Gateway vor dem .NET-MCP-Server
- Status: **Vorgeschlagen** (nicht akzeptiert – G1–G5 offen) · Datum: 2026-10-17

## Kontext
Entra ID bietet weder DCR noch CIMD noch RFC 8707 (ADR-0004/0005). Jeder neue Connector braucht eine vorregistrierte Entra-App und manuell gepflegte Redirect-URIs.

## Entscheidung (vorgeschlagen)
Ein Python-FastMCP-Gateway (`src/OAuthGateway`, FastMCP 4.1.0, `AzureProvider`) auf eigenem Linux-F1-Plan übernimmt Discovery/DCR/CIMD/Consent gegenüber Clients, nutzt eine zentrale Entra-App mit fester Callback-URL und leitet das Upstream-Entra-Token an den unveränderten .NET-MCP-Server weiter. Feature-Flag `enable_gateway=false`. Die .NET-OAuth-Fassade bleibt als Rollback.

## Alternativen
APIM (kein OAuth-Server, Consumption: 30-s-Limit, keine MCP-Features), Keycloak, Container Apps (vom Eigentümer ausgeschlossen), Erweiterung der .NET-Fassade um DCR.

## Konsequenzen
+ Automatische Registrierung ohne Entra-Eingriff pro Client; Clients sehen nur die Gateway-URL.
− Zweite Laufzeit (Python), zusätzlicher Speicher (Azure Tables), F1-Cold-Starts.
− Gateway ist hochwertiges Ziel (hält Entra-Tokens verschlüsselt).
− Confidential-DCR mit Secret nicht unterstützt (FastMCP 4.1.0).
− Backend bleibt öffentlich erreichbar.

## Umsetzung
`docs/FASTMCP-GATEWAY-*.md`, `terraform/gateway.tf`, `terraform/modules/gateway/`, `terraform/modules/entra-id/gateway.tf`, `.github/workflows/deploy.yml`.

## Änderungs-Leitfaden
Akzeptieren erst nach bestandenen Gates G1–G5 (siehe `docs/FASTMCP-GATEWAY-TESTS.md`); bei Scheitern: Status „Verworfen“, `enable_gateway` bleibt `false`.

## Aktualisierung 2026-10-17
Gates G1 und G2 sind erfüllt, G3 ist für ChatGPT, Claude und Grok erfüllt (ChatGPT im Gateway-Log belegt, die anderen vom Eigentümer gemeldet). Offen: Copilot Studio/M365 Copilot (G3), Refresh und Re-Deploy (G5), Netzwerkisolation (G6), Kosten (G7). Foundry nutzt bewusst nicht das Gateway, sondern OAuth-Passthrough gegen die MCP-Adresse. **Der Status bleibt „Vorgeschlagen“, bis der Eigentümer über die Annahme für Dev entscheidet.**
