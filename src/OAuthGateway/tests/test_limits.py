import httpx

from gateway.app import create_app
from gateway.limits import Limits, client_ip, request_limits
from tests.harness import free_port, serve
from tests.test_config_app import GOOD
from gateway.config import load_config


def _scope(headers=(), client=("10.0.0.1", 1234)):
    return {"type": "http", "headers": list(headers), "client": client}


def test_client_ip_uses_last_forwarded_entry_and_strips_port():
    assert client_ip(_scope([(b"x-forwarded-for", b"6.6.6.6, 203.0.113.9:4711")])) == "203.0.113.9"
    assert client_ip(_scope([(b"x-forwarded-for", b"203.0.113.9")])) == "203.0.113.9"
    assert client_ip(_scope()) == "10.0.0.1"


def test_classification_and_defaults():
    limits = Limits({})
    assert limits.classify("/register")[0] == "register"
    assert limits.classify("/token")[0] == "oauth"
    assert limits.classify("/mcp")[0] == "mcp"
    assert limits.classify("/.well-known/oauth-authorization-server")[0] == "other"
    assert limits.register < limits.oauth < limits.mcp


def _serve(monkeypatch, **env):
    for k, v in env.items():
        monkeypatch.setenv(k, str(v))
    app = create_app(load_config(GOOD))
    port = free_port()
    serve(app, port)
    return f"http://127.0.0.1:{port}"


def _register(base, headers=None):
    return httpx.post(f"{base}/register", headers=headers or {}, json={
        "redirect_uris": ["http://localhost:33418/cb"], "client_name": "t", "token_endpoint_auth_method": "none"})


def test_register_is_rate_limited_but_health_and_discovery_are_not(monkeypatch):
    # GOOD uses env-less config; create_app reads limits from os.environ
    base = _serve(monkeypatch, GATEWAY_LIMIT_REGISTER_PER_MIN=3)
    codes = [_register(base).status_code for _ in range(5)]
    assert codes[:3] == [201, 201, 201]
    assert codes[3:] == [429, 429]
    limited = _register(base)
    assert limited.headers["retry-after"] == "60"
    assert limited.json()["error"] == "rate_limited"
    for _ in range(20):
        assert httpx.get(f"{base}/healthz").status_code == 200
    assert httpx.get(f"{base}/.well-known/oauth-authorization-server").status_code == 200


def test_forged_first_forwarded_entry_does_not_evade_the_limit(monkeypatch):
    base = _serve(monkeypatch, GATEWAY_LIMIT_REGISTER_PER_MIN=2)
    for i in range(2):
        assert _register(base, {"X-Forwarded-For": f"9.9.9.{i}, 203.0.113.9"}).status_code == 201
    assert _register(base, {"X-Forwarded-For": "9.9.9.99, 203.0.113.9"}).status_code == 429
    assert _register(base, {"X-Forwarded-For": "9.9.9.99, 198.51.100.7"}).status_code == 201   # other real client


def test_oversized_body_is_rejected(monkeypatch):
    base = _serve(monkeypatch, GATEWAY_MAX_BODY_BYTES=2000, GATEWAY_LIMIT_REGISTER_PER_MIN=50)
    big = httpx.post(f"{base}/register", content=b"x" * 5000, headers={"content-type": "application/json"})
    assert big.status_code == 413
    assert _register(base).status_code == 201


def test_limits_can_be_disabled(monkeypatch):
    base = _serve(monkeypatch, GATEWAY_LIMITS_ENABLED="false", GATEWAY_LIMIT_REGISTER_PER_MIN=1)
    assert [_register(base).status_code for _ in range(4)] == [201, 201, 201, 201]


def test_middleware_wraps_only_http_scopes():
    called = []

    async def inner(scope, receive, send):
        called.append(scope["type"])

    import asyncio
    wrapped = request_limits(inner, {})
    asyncio.run(wrapped({"type": "lifespan"}, None, None))
    assert called == ["lifespan"]
