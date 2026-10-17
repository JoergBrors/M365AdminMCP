# FastMCP OAuth gateway PoC (local only)

Not deployed, not wired into CI or Terraform. See `docs/APIM-FAST-MCP-FEASIBILITY.md` for results and limits.

- `gateway/server.py` – gateway: `AzureProvider` (Entra upstream) + proxy that forwards the validated upstream Entra token to the .NET `/mcp`.
- `tests/` – 21 tests against a mock IdP and a Python backend stub (the stub is NOT the .NET server).
- `tools/dotnet_leg.py` – attempts to run the real `src/McpServer` against a mock issuer; documents why that cannot succeed offline.

Required env for `build_azure_gateway`: `AZURE_TENANT_ID`, `GATEWAY_ENTRA_CLIENT_ID`, `GATEWAY_ENTRA_CLIENT_SECRET`, `GATEWAY_JWT_SIGNING_KEY`, `MCP_APP_ID_URI`, `GATEWAY_PUBLIC_URL`, `BACKEND_MCP_URL`.
