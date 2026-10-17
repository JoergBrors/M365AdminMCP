"""Optional: prove the REAL .NET McpServer validates tokens forwarded by the gateway.

Starts a mock OIDC issuer (discovery + JWKS), launches `dotnet run` for src/McpServer with env-only
configuration pointing at the mock issuer, then posts MCP initialize with good/bad tokens.
No .NET source is modified. Requires the .NET 10 SDK. Usage: python tools/dotnet_leg.py
"""
import json
import os
import pathlib
import subprocess
import sys
import time

import httpx
from cryptography.hazmat.primitives import serialization
from jwt.algorithms import RSAAlgorithm
from starlette.applications import Starlette
from starlette.responses import JSONResponse
from starlette.routing import Route

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from tests.harness import MockEntra, free_port, serve  # noqa: E402

REPO = ROOT.parents[1]
AUD = "api://mcp-server-test"

idp = MockEntra()
pub = serialization.load_pem_public_key(idp.keys.public_key.encode())
jwk = json.loads(RSAAlgorithm.to_jwk(pub))
jwk.update(use="sig", alg="RS256", kid="k1")
port = free_port()
base = f"http://127.0.0.1:{port}"
issuer = f"{base}/tenant/v2.0"


async def disco(request):
    return JSONResponse({"issuer": issuer, "jwks_uri": f"{base}/keys", "authorization_endpoint": f"{base}/a",
                         "token_endpoint": f"{base}/t", "id_token_signing_alg_values_supported": ["RS256"],
                         "response_types_supported": ["code"], "subject_types_supported": ["public"]})


async def keys(request):
    return JSONResponse({"keys": [jwk]})

serve(Starlette(routes=[Route("/tenant/v2.0/.well-known/openid-configuration", disco),
                        Route("/tenant/.well-known/openid-configuration", disco),
                        Route("/keys", keys)]), port)

import jwt as pyjwt  # noqa: E402


def mint(**kw):
    claims = {"iss": kw.get("iss", issuer), "aud": kw.get("aud", AUD), "sub": "u1", "scp": kw.get("scp", "Mcp.Access"),
              "ver": "2.0", "tid": "tenant", "iat": int(time.time()), "nbf": int(time.time()) - 5,
              "exp": int(time.time()) + kw.get("exp_in", 3600)}
    return pyjwt.encode(claims, (kw.get("key") or idp.keys.private_key).get_secret_value(), algorithm="RS256", headers={"kid": "k1"})


app_port = free_port()
env = {**os.environ, "ASPNETCORE_ENVIRONMENT": "Development", "ASPNETCORE_URLS": f"http://127.0.0.1:{app_port}",
       "AzureAd__Instance": base + "/", "AzureAd__TenantId": "tenant", "AzureAd__ClientId": "00000000-0000-0000-0000-000000000001",
       "AzureAd__Audience": AUD, "AzureAd__UseManagedIdentity": "false", "AzureAd__ClientSecret": "x",
       "ApiServer__BaseUrl": "http://127.0.0.1:1", "McpAuth__RequireAuthentication": "true", "Authentication__Schemes__Bearer__RequireHttpsMetadata": "false"}
proc = subprocess.Popen(["dotnet", "run", "--project", str(REPO / "src/McpServer"), "--no-launch-profile"],
                        env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
try:
    for _ in range(120):
        try:
            if httpx.get(f"http://127.0.0.1:{app_port}/healthz", timeout=1).status_code == 200:
                break
        except Exception:
            time.sleep(1)
    else:
        proc.terminate(); print(proc.stdout.read()[-3000:]); raise SystemExit("McpServer did not start")
    H = {"Accept": "application/json, text/event-stream", "Content-Type": "application/json"}
    INIT = {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
        "protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "t", "version": "1"}}}
    LIST = {"jsonrpc": "2.0", "id": 2, "method": "tools/list"}
    url = f"http://127.0.0.1:{app_port}/mcp"
    cases = {
        "valid": (mint(), 200), "no token": (None, 401), "wrong aud": (mint(aud="api://other"), 401),
        "wrong issuer": (mint(iss="https://evil/v2.0"), 401), "expired": (mint(exp_in=-3600), 401),
        "bad signature": (mint(key=idp.other_keys.private_key), 401), "wrong scope": (mint(scp="Other"), 403),
    }
    ok = True
    for name, (tok, want) in cases.items():
        h = dict(H, **({"Authorization": f"Bearer {tok}"} if tok else {}))
        got = httpx.post(url, json=INIT, headers=h).status_code
        ok &= got == want
        print(f"{'PASS' if got == want else 'FAIL'} {name}: got {got}, want {want}")
    r = httpx.post(url, json=LIST, headers=dict(H, Authorization=f"Bearer {mint()}"))
    print("tools/list with valid token:", r.status_code, r.text[:200].replace("\n", " "))
    if not ok:
        proc.terminate()
        print('--- server log ---')
        print(''.join(l for l in proc.stdout.readlines() if 'IDX' in l or 'Bearer' in l or 'fail' in l)[-3000:])
    sys.exit(0 if ok else 1)
finally:
    proc.terminate()
