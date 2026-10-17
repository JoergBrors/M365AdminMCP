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
