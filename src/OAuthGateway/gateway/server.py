"""FastMCP OAuth gateway PoC in front of the existing .NET MCP server.

Model B: the client only ever holds a FastMCP-issued JWT. The gateway swaps it for the
upstream Entra access token (audience = MCP app, scope Mcp.Access) and forwards THAT token
to the .NET backend, which keeps validating Entra JWTs unchanged. No unsigned headers,
no shared secrets, no extra Graph/ARM permission.
"""
from __future__ import annotations

import logging
import os

import httpx2
from fastmcp import FastMCP
from fastmcp.client.transports import StreamableHttpTransport
from fastmcp.server.dependencies import get_access_token
from fastmcp.server.middleware import Middleware
from fastmcp.server.providers.proxy import FastMCPProxy, ProxyClient


class _UpstreamBearer(httpx2.Auth):
    """Per-request: use the caller's validated *upstream* token, never the FastMCP JWT."""

    def auth_flow(self, request):
        token = get_access_token()
        if token is None:
            raise RuntimeError("no authenticated caller - refusing to call backend anonymously")
        request.headers["Authorization"] = f"Bearer {token.token}"
        yield request


log = logging.getLogger("gateway.proxy")


class _ErrorLogging(Middleware):
    """Log failures of proxied MCP requests (method + exception chain, never headers or tokens)."""

    async def on_message(self, context, call_next):
        try:
            return await call_next(context)
        except Exception as exc:
            chain, e = [], exc
            while e is not None and len(chain) < 6:
                status = getattr(getattr(e, "response", None), "status_code", None)
                chain.append(f"{type(e).__name__}({status if status else str(e)[:200]})")
                e = e.__cause__ or e.__context__
            text = " <- ".join(chain)
            log.error("proxy request failed method=%s chain=%s", getattr(context, "method", "?"), text)
            if os.environ.get("GATEWAY_DEBUG_ERRORS", "").lower() == "true":
                # Temporary diagnostics (off by default): error types/status codes only, never headers or tokens.
                from mcp.shared.exceptions import MCPError
                raise MCPError(code=-32603, message=f"gateway debug: {text}") from exc
            raise


_RESERVED_META_PREFIX = "io.modelcontextprotocol/"


def _install_reserved_meta_filter() -> None:
    """Do not forward protocol-reserved `_meta` keys (`io.modelcontextprotocol/*`, e.g. logLevel) to the backend.

    FastMCP's proxy forwards the frontend request's `_meta` except three connection keys, so a modern client's per-request
    `io.modelcontextprotocol/logLevel` reaches the backend. The .NET MCP server (handshake era, protocol 2025-11-25) rejects it:
    "The reserved per-request metadata key ... is not valid with protocol version '2025-11-25'" (live failure on tools/call).
    Other keys (progressToken, tracing) still pass. Guarded by tests/test_mcp_e2e.py::test_reserved_meta_is_not_forwarded_to_backend.
    """
    from fastmcp.server.providers import proxy as fastmcp_proxy

    original = fastmcp_proxy._forwardable_request_meta
    if getattr(original, "_reserved_filter", False):
        return

    def filtered(ctx):
        meta = original(ctx)
        if not meta:
            return meta
        kept = {k: v for k, v in meta.items() if not str(k).startswith(_RESERVED_META_PREFIX)}
        return kept or None

    filtered._reserved_filter = True
    fastmcp_proxy._forwardable_request_meta = filtered


_install_reserved_meta_filter()


def make_backend_client(backend_mcp_url: str) -> ProxyClient:
    transport = StreamableHttpTransport(backend_mcp_url, auth=_UpstreamBearer())
    # The .NET MCP server (ModelContextProtocol.AspNetCore, protocol 2025-11-25) speaks the handshake era only.
    # Without pinning, the proxy mirrors a modern client's era to the backend and the .NET server rejects the
    # reserved per-request metadata (live failure on tools/call). Pin the backend leg to the handshake era.
    return ProxyClient(transport, mode="legacy")


def build_proxy(backend_mcp_url: str, auth) -> FastMCP:
    def factory() -> ProxyClient:
        return make_backend_client(backend_mcp_url)

    # forward_incoming_headers is irrelevant here: the transport sets Authorization itself,
    # and the incoming one is the FastMCP JWT which must never reach the backend.
    proxy = FastMCPProxy(client_factory=factory, name="m365-mcp-gateway", auth=auth)
    proxy.add_middleware(_ErrorLogging())
    return proxy
