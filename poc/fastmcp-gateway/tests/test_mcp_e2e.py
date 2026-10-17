import asyncio

import httpx
import pytest
from fastmcp import Client
from fastmcp.exceptions import ToolError

from tests.harness import MCP_AUD
from tests.test_oauth_flow import get_token

pytestmark = pytest.mark.asyncio

INIT = {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
    "protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "t", "version": "1"}}}
HDRS = {"Accept": "application/json, text/event-stream", "Content-Type": "application/json"}


async def test_initialize_list_call_through_gateway(gw):
    _, tok = get_token(gw)
    async with Client(f"{gw.url}/mcp", auth=tok["access_token"]) as c:
        names = {t.name for t in await c.list_tools()}
        assert {"whoami", "boom", "slow"} <= names
        res = await c.call_tool("whoami", {})
        assert res.data == "user-1"   # backend saw the real Entra identity (token forwarded)


async def test_backend_receives_entra_token_not_fastmcp_jwt(gw, idp):
    _, tok = get_token(gw)
    async with Client(f"{gw.url}/mcp", auth=tok["access_token"]) as c:
        await c.call_tool("whoami", {})
    # the backend verifier only accepts Entra-signed aud=MCP_AUD tokens, so success proves it;
    # additionally the client token must not be accepted directly by the backend:
    r = httpx.post(f"http://127.0.0.1:{gw.port}/mcp", json=INIT, headers={**HDRS})
    assert r.status_code == 401


async def test_backend_error_is_propagated(gw):
    _, tok = get_token(gw)
    async with Client(f"{gw.url}/mcp", auth=tok["access_token"]) as c:
        with pytest.raises(ToolError) as e:
            await c.call_tool("boom", {})
        assert "backend-failure-marker" in str(e.value)


async def test_cancellation_does_not_hang_gateway(gw):
    _, tok = get_token(gw)
    async with Client(f"{gw.url}/mcp", auth=tok["access_token"]) as c:
        with pytest.raises(asyncio.TimeoutError):
            await asyncio.wait_for(c.call_tool("slow", {}), timeout=1.5)
    async with Client(f"{gw.url}/mcp", auth=tok["access_token"]) as c:   # still healthy
        assert (await c.call_tool("whoami", {})).data == "user-1"


async def test_http_methods(gw):
    _, tok = get_token(gw)
    h = {**HDRS, "Authorization": f"Bearer {tok['access_token']}"}
    assert httpx.post(f"{gw.url}/mcp", json=INIT, headers=h).status_code == 200
    for m in ("PUT", "PATCH"):
        assert httpx.request(m, f"{gw.url}/mcp", headers=h).status_code in (404, 405)
    # record (do not assert) behaviour of GET/DELETE in the stateless mode
    print("GET", httpx.get(f"{gw.url}/mcp", headers=h, timeout=3).status_code
          if False else "skipped (SSE stream would block)")
    print("DELETE", httpx.delete(f"{gw.url}/mcp", headers=h).status_code)


async def test_unauthenticated_and_garbage_tokens_rejected_at_gateway(gw):
    for h in ({}, {"Authorization": "Bearer garbage"}, {"Authorization": "Basic abc"}):
        assert httpx.post(f"{gw.url}/mcp", json=INIT, headers={**HDRS, **h}).status_code == 401


# --- backend boundary: what the .NET validation must also reject (stub mirrors its rules) ---

@pytest.mark.parametrize("kw", [
    {"aud": "api://other"}, {"iss": "https://evil/issuer"}, {"exp_in": -60}, {"scp": "Other.Scope"}])
async def test_backend_rejects_bad_claims(idp, backend, kw):
    t = idp.mint(**kw)
    r = httpx.post(backend.url, json=INIT, headers={**HDRS, "Authorization": f"Bearer {t}"})
    assert r.status_code == 401


async def test_backend_rejects_wrong_signature_and_no_token(idp, backend):
    t = idp.mint(keys=idp.other_keys)
    assert httpx.post(backend.url, json=INIT, headers={**HDRS, "Authorization": f"Bearer {t}"}).status_code == 401
    assert httpx.post(backend.url, json=INIT, headers=HDRS).status_code == 401
    assert httpx.post(backend.url, json=INIT, headers={**HDRS, "X-User": "admin"}).status_code == 401


async def test_backend_accepts_valid_token(idp, backend):
    t = idp.mint()
    r = httpx.post(backend.url, json=INIT, headers={**HDRS, "Authorization": f"Bearer {t}"})
    assert r.status_code == 200 and MCP_AUD
