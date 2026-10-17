import logging

import httpx
import pytest

from gateway.app import create_app
from gateway.config import ConfigError, load_config
from tests.harness import free_port, serve

GOOD = {
    "AZURE_TENANT_ID": "db88f9b6-c89a-41a3-a21d-b61458e234ff",
    "GATEWAY_ENTRA_CLIENT_ID": "11111111-2222-3333-4444-555555555555",
    "GATEWAY_ENTRA_CLIENT_SECRET": "s3cret-VALUE-for-tests",
    "GATEWAY_JWT_SIGNING_KEY": "a" * 40,
    "GATEWAY_STORAGE_ENCRYPTION_KEY": "b" * 40,
    "MCP_APP_ID_URI": "api://mcp-server-dev",
    "MCP_APP_CLIENT_ID": "00282801-c55c-4ce8-8039-5b80521e58f1",
    "GATEWAY_PUBLIC_URL": "https://gw.example.net",
    "BACKEND_MCP_URL": "https://backend.example.net/mcp",
    "GATEWAY_ALLOW_MEMORY_STORAGE": "true",
}


@pytest.mark.parametrize("key", [k for k in GOOD if k != "GATEWAY_ALLOW_MEMORY_STORAGE"])
def test_missing_setting_fails_fast(key):
    env = {k: v for k, v in GOOD.items() if k != key}
    with pytest.raises(ConfigError):
        load_config(env)


@pytest.mark.parametrize("patch", [
    {"AZURE_TENANT_ID": "common"}, {"GATEWAY_PUBLIC_URL": "http://gw.example.net"},
    {"BACKEND_MCP_URL": "https://b.example.net/other"}, {"GATEWAY_JWT_SIGNING_KEY": "short"},
    {"GATEWAY_STORAGE_ENCRYPTION_KEY": "a" * 40}, {"MCP_APP_ID_URI": "https://x"},
    {"GATEWAY_ALLOW_MEMORY_STORAGE": "false"},
])
def test_invalid_setting_rejected(patch):
    with pytest.raises(ConfigError):
        load_config({**GOOD, **patch})


def test_repr_does_not_leak_secrets():
    text = repr(load_config(GOOD))
    for s in ("s3cret-VALUE-for-tests", "a" * 40, "b" * 40):
        assert s not in text


def test_config_error_does_not_leak_secret_values():
    try:
        load_config({**GOOD, "GATEWAY_JWT_SIGNING_KEY": "short"})
    except ConfigError as e:
        assert "short" not in str(e).replace("at least 32 characters", "")


def test_app_health_endpoints_and_auth_required(caplog):
    caplog.set_level(logging.INFO)
    app = create_app(load_config(GOOD))
    port = free_port()
    serve(app, port)
    base = f"http://127.0.0.1:{port}"
    assert httpx.get(f"{base}/healthz").json() == {"status": "healthy"}
    assert httpx.get(f"{base}/readyz").json()["status"] == "ready"
    assert httpx.post(f"{base}/mcp", json={}).status_code == 401
    meta = httpx.get(f"{base}/.well-known/oauth-authorization-server").json()
    assert meta["issuer"].rstrip("/") == "https://gw.example.net"      # public host, not localhost
    assert "registration_endpoint" in meta
    assert "s3cret-VALUE-for-tests" not in caplog.text


def test_azure_tables_storage_path_imports_and_builds():
    """Regression: production uses the Azure Tables store (needs aiohttp for the async azure clients);
    earlier tests only used the memory store and missed the missing dependency."""
    from gateway.app import build_storage
    cfg = load_config({**GOOD, "GATEWAY_STORAGE_ACCOUNT": "acct", "GATEWAY_ALLOW_MEMORY_STORAGE": "false"})
    assert build_storage(cfg) is not None


def test_token_audience_is_the_mcp_app_not_the_gateway_app():
    """Regression for the live failure: Entra issues aud=<MCP app GUID>; the gateway app id must not be accepted."""
    from gateway.app import build_auth, build_storage
    cfg = load_config(GOOD)
    verifier = build_auth(cfg, build_storage(cfg))._token_validator
    assert verifier.audience == [GOOD["MCP_APP_CLIENT_ID"], GOOD["MCP_APP_ID_URI"]]
    assert GOOD["GATEWAY_ENTRA_CLIENT_ID"] not in verifier.audience


def test_backend_client_is_pinned_to_handshake_era():
    """Regression: .NET backend rejects modern per-request metadata (live tools/call failure)."""
    from gateway.server import make_backend_client
    assert make_backend_client("https://backend.example.net/mcp").mode == "legacy"


def test_azure_tables_keys_allow_cimd_url_client_ids():
    """Regression: Claude/ChatGPT send a URL as client_id (CIMD); Azure Tables rejects '/' in RowKey -> HTTP 500."""
    from gateway.app import build_storage
    cfg = load_config({**GOOD, "GATEWAY_STORAGE_ACCOUNT": "acct", "GATEWAY_ALLOW_MEMORY_STORAGE": "false"})
    store = build_storage(cfg).key_value
    url = "https://claude.ai/oauth/mcp-oauth-client-metadata"
    safe = store._key_sanitization_strategy.sanitize(url)
    assert "/" not in safe and safe.startswith("H_")
    uuid_key = "75dcc81d-5cf4-406e-98c1-0a986b4c04ee"       # DCR ids stay as they are (existing registrations survive)
    assert store._key_sanitization_strategy.sanitize(uuid_key) == uuid_key


def test_issuer_has_no_trailing_slash_everywhere():
    """Regression: ChatGPT rejected the authorization response ('issuer does not match the expected issuer')."""
    import urllib.parse as up

    app = create_app(load_config(GOOD))
    port = free_port()
    serve(app, port)
    base = f"http://127.0.0.1:{port}"
    issuer = GOOD["GATEWAY_PUBLIC_URL"]
    assert httpx.get(f"{base}/.well-known/oauth-authorization-server").json()["issuer"] == issuer
    prm = httpx.get(f"{base}/.well-known/oauth-protected-resource/mcp").json()
    assert prm["authorization_servers"] == [issuer]
    # other URLs in the documents are untouched
    asm = httpx.get(f"{base}/.well-known/oauth-authorization-server").json()
    assert asm["authorization_endpoint"] == f"{issuer}/authorize" and asm["registration_endpoint"] == f"{issuer}/register"
    # the `iss` parameter of the redirect back to the client (error redirect carries it as well)
    reg = httpx.post(f"{base}/register", json={"redirect_uris": ["http://localhost:33418/cb"], "client_name": "t",
                                               "token_endpoint_auth_method": "none"}).json()
    r = httpx.get(f"{base}/authorize", params={
        "response_type": "code", "client_id": reg["client_id"], "redirect_uri": "http://localhost:33418/cb",
        "code_challenge": "x" * 43, "code_challenge_method": "S256", "state": "s", "scope": "Bad.Scope"},
        follow_redirects=False)
    assert r.status_code == 302
    iss = up.parse_qs(up.urlparse(r.headers["location"]).query)["iss"][0]
    assert iss == issuer


def test_redirect_log_contains_iss_but_no_code_or_state(caplog):
    import logging

    from gateway.issuer import _log_redirect
    caplog.set_level(logging.INFO)
    _log_redirect("/auth/callback", "https://chatgpt.com/cb?code=SECRETCODE&state=SECRETSTATE&iss=https%3A%2F%2Fgw.example.net")
    assert "iss='https://gw.example.net'" in caplog.text
    assert "SECRETCODE" not in caplog.text and "SECRETSTATE" not in caplog.text


def test_iss_param_flag_is_advertised_by_default_and_can_be_switched_off(monkeypatch):
    app = create_app(load_config(GOOD))
    port = free_port()
    serve(app, port)
    url = f"http://127.0.0.1:{port}/.well-known/oauth-authorization-server"
    monkeypatch.delenv("GATEWAY_ADVERTISE_ISS_PARAM", raising=False)
    assert httpx.get(url).json()["authorization_response_iss_parameter_supported"] is True
    monkeypatch.setenv("GATEWAY_ADVERTISE_ISS_PARAM", "false")
    assert httpx.get(url).json()["authorization_response_iss_parameter_supported"] is False
