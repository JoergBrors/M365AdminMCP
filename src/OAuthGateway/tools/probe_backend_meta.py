"""Local probe: which JSON-RPC requests (method + _meta keys) does the gateway forward to the backend?

Starts the mock IdP + FastMCP backend + gateway (tests/harness), puts an ASGI recorder in front of the backend, and calls
initialize/tools/list/tools/call through the gateway with the FastMCP client. Prints the methods and `_meta` keys the backend saw.
Run from src/OAuthGateway:  python tools/probe_backend_meta.py
"""
import asyncio
import json
import sys

sys.path.insert(0, ".")

from fastmcp import Client  # noqa: E402

from tests.harness import Backend, Gateway, MockEntra, free_port, serve  # noqa: E402
from tests.test_oauth_flow import get_token  # noqa: E402

seen = []


def recorder(app):
    async def wrapper(scope, receive, send):
        if scope["type"] != "http" or scope["method"] != "POST":
            return await app(scope, receive, send)
        body = b""
        more = True
        while more:
            message = await receive()
            body += message.get("body", b"")
            more = message.get("more_body", False)
        try:
            doc = json.loads(body)
            items = doc if isinstance(doc, list) else [doc]
            for item in items:
                meta = (item.get("params") or {}).get("_meta") or {}
                seen.append((item.get("method"), sorted(meta)))
        except ValueError:
            pass
        sent = False

        async def replay():
            nonlocal sent
            if not sent:
                sent = True
                return {"type": "http.request", "body": body, "more_body": False}
            return await receive()

        await app(scope, replay, send)

    return wrapper


idp = MockEntra()
backend = Backend.__new__(Backend)
backend.port = free_port()
backend.url = f"http://127.0.0.1:{backend.port}/mcp"
from fastmcp import FastMCP  # noqa: E402
from fastmcp.server.auth.providers.jwt import JWTVerifier  # noqa: E402
from tests.harness import MCP_AUD  # noqa: E402

verifier = JWTVerifier(public_key=idp.keys.public_key, issuer=idp.issuer, audience=MCP_AUD, required_scopes=["Mcp.Access"])
mcp = FastMCP("backend", auth=verifier)


@mcp.tool
def whoami() -> str:
    return "ok"


serve(recorder(mcp.http_app(path="/mcp", stateless_http=True)), backend.port)
gw = Gateway(idp, backend)
_, tok = get_token(gw)


async def main():
    async with Client(f"{gw.url}/mcp", auth=tok["access_token"]) as c:
        await c.list_tools()
        await c.call_tool("whoami", {})


asyncio.run(main())
for method, meta in seen:
    print(method, meta)
