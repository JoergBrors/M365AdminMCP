"""Local harness: mock Entra (OIDC issuer), stub backend (validates JWTs like the .NET one),
and the gateway under test. The mock IdP stands in for Entra: it proves gateway semantics,
NOT Entra-specific behaviour and NOT the .NET leg (see docs)."""
from __future__ import annotations

import base64
import hashlib
import socket
import threading
import time
import uuid

import uvicorn
from starlette.applications import Starlette
from starlette.requests import Request
from starlette.responses import JSONResponse, RedirectResponse
from starlette.routing import Route

from fastmcp import FastMCP
from fastmcp.server.auth import OAuthProxy
from fastmcp.server.auth.providers.jwt import JWTVerifier, RSAKeyPair
from gateway.server import build_proxy

MCP_AUD = "api://mcp-server-test"


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def serve(app, port) -> uvicorn.Server:
    server = uvicorn.Server(uvicorn.Config(app, host="127.0.0.1", port=port, log_level="warning"))
    threading.Thread(target=server.run, daemon=True).start()
    for _ in range(100):
        if server.started:
            return server
        time.sleep(0.05)
    raise RuntimeError("server did not start")


class MockEntra:
    def __init__(self):
        self.port = free_port()
        self.url = f"http://127.0.0.1:{self.port}"
        self.issuer = f"{self.url}/tenant/v2.0"
        self.keys = RSAKeyPair.generate()
        self.other_keys = RSAKeyPair.generate()
        self.codes: dict = {}
        self.last_authorize_params: dict = {}
        self.last_token_form: dict = {}
        self.last_token_auth = ''
        self.issued: list[str] = []
        app = Starlette(routes=[
            Route("/authorize", self.authorize),
            Route("/token", self.token, methods=["POST"]),
        ])
        self.server = serve(app, self.port)

    def mint(self, *, aud=MCP_AUD, iss=None, exp_in=3600, keys=None, scp="Mcp.Access", sub="user-1") -> str:
        keys = keys or self.keys
        return keys.create_token(subject=sub, issuer=iss or self.issuer, audience=aud,
                                 scopes=scp.split(), expires_in_seconds=exp_in)

    async def authorize(self, request: Request):
        q = dict(request.query_params)
        self.last_authorize_params = q
        code = uuid.uuid4().hex
        self.codes[code] = q
        return RedirectResponse(f"{q['redirect_uri']}?code={code}&state={q['state']}", 302)

    async def token(self, request: Request):
        form = dict(await request.form())
        self.last_token_form = form
        self.last_token_auth = request.headers.get('authorization', '')
        if form.get("grant_type") != "refresh_token":
            code_q = self.codes.pop(form.get("code", ""), None)
            if code_q is None:
                return JSONResponse({"error": "invalid_grant"}, 400)
            digest = base64.urlsafe_b64encode(
                hashlib.sha256(form["code_verifier"].encode()).digest()).rstrip(b"=").decode()
            if digest != code_q["code_challenge"]:
                return JSONResponse({"error": "invalid_grant", "error_description": "pkce"}, 400)
        tok = self.mint()
        self.issued.append(tok)
        return JSONResponse({"access_token": tok, "token_type": "Bearer", "expires_in": 3600,
                             "refresh_token": "upstream-refresh", "scope": "Mcp.Access"})


class Backend:
    """Stand-in for the .NET /mcp: validates signature/issuer/audience/expiry/scope, nothing else."""

    def __init__(self, idp: MockEntra):
        self.port = free_port()
        self.url = f"http://127.0.0.1:{self.port}/mcp"
        verifier = JWTVerifier(public_key=idp.keys.public_key, issuer=idp.issuer, audience=MCP_AUD,
                               required_scopes=["Mcp.Access"])
        mcp = FastMCP("backend", auth=verifier)

        @mcp.tool
        def whoami() -> str:
            from fastmcp.server.dependencies import get_access_token
            return str(get_access_token().claims.get("sub", "?"))

        @mcp.tool
        def boom() -> str:
            raise ValueError("backend-failure-marker")

        @mcp.tool
        async def slow() -> str:
            import asyncio
            await asyncio.sleep(30)
            return "done"

        self.server = serve(mcp.http_app(path="/mcp", stateless_http=True), self.port)


class Gateway:
    def __init__(self, idp: MockEntra, backend: Backend, *, consent=True, allowed=None):
        self.port = free_port()
        self.url = f"http://127.0.0.1:{self.port}"
        auth = OAuthProxy(
            upstream_authorization_endpoint=f"{idp.url}/authorize",
            upstream_token_endpoint=f"{idp.url}/token",
            upstream_client_id="central-upstream-client",
            upstream_client_secret="CENTRAL-UPSTREAM-SECRET",
            token_verifier=JWTVerifier(public_key=idp.keys.public_key, issuer=idp.issuer,
                                       audience=MCP_AUD, required_scopes=["Mcp.Access"]),
            base_url=self.url, jwt_signing_key="test-signing-key-test-signing-key-123456",
            valid_scopes=["Mcp.Access"], forward_resource=False,
            require_authorization_consent=consent, allowed_client_redirect_uris=allowed)
        self.mcp = build_proxy(backend.url, auth)
        self.server = serve(self.mcp.http_app(path="/mcp"), self.port)
