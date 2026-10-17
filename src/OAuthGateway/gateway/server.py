"""FastMCP OAuth gateway PoC in front of the existing .NET MCP server.

Model B: the client only ever holds a FastMCP-issued JWT. The gateway swaps it for the
upstream Entra access token (audience = MCP app, scope Mcp.Access) and forwards THAT token
to the .NET backend, which keeps validating Entra JWTs unchanged. No unsigned headers,
no shared secrets, no extra Graph/ARM permission.
"""
from __future__ import annotations

import httpx2
from fastmcp import FastMCP
from fastmcp.client.transports import StreamableHttpTransport
from fastmcp.server.dependencies import get_access_token
from fastmcp.server.providers.proxy import FastMCPProxy, ProxyClient


class _UpstreamBearer(httpx2.Auth):
    """Per-request: use the caller's validated *upstream* token, never the FastMCP JWT."""

    def auth_flow(self, request):
        token = get_access_token()
        if token is None:
            raise RuntimeError("no authenticated caller - refusing to call backend anonymously")
        request.headers["Authorization"] = f"Bearer {token.token}"
        yield request


def build_proxy(backend_mcp_url: str, auth) -> FastMCP:
    def factory() -> ProxyClient:
        transport = StreamableHttpTransport(backend_mcp_url, auth=_UpstreamBearer())
        return ProxyClient(transport)

    # forward_incoming_headers is irrelevant here: the transport sets Authorization itself,
    # and the incoming one is the FastMCP JWT which must never reach the backend.
    return FastMCPProxy(client_factory=factory, name="m365-mcp-gateway", auth=auth)
