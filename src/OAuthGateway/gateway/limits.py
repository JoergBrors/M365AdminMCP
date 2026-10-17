"""Rate limiting and request-size limits for the gateway (finding F-009/F-011, decision D-5).

Pure ASGI middleware, in-memory sliding window per client IP. The IP is the LAST X-Forwarded-For entry (appended by
the App Service front end; a forged first entry is ignored). Limits are per instance; the gateway runs a single worker.
All limits are env-configurable, defaults are generous for legitimate OAuth/MCP clients.
"""
from __future__ import annotations

import json
import time
from collections import deque

_MAX_KEYS = 10_000


class _BodyTooLarge(Exception):
    pass


def client_ip(scope) -> str:
    for name, value in scope.get("headers", []):
        if name == b"x-forwarded-for":
            parts = [p.strip() for p in value.decode("latin-1").split(",") if p.strip()]
            if parts:
                last = parts[-1]
                if last.count(":") == 1 and "." in last:   # IPv4 with port
                    last = last.rsplit(":", 1)[0]
                return last
    client = scope.get("client")
    return client[0] if client else "unknown"


class Limits:
    def __init__(self, env):
        def num(name, default):
            try:
                return int(env.get(name, default))
            except ValueError:
                return default

        self.enabled = env.get("GATEWAY_LIMITS_ENABLED", "true").lower() != "false"
        self.window = 60.0
        self.register = num("GATEWAY_LIMIT_REGISTER_PER_MIN", 10)
        self.oauth = num("GATEWAY_LIMIT_OAUTH_PER_MIN", 60)
        self.mcp = num("GATEWAY_LIMIT_MCP_PER_MIN", 300)
        self.other = num("GATEWAY_LIMIT_OTHER_PER_MIN", 120)
        self.body_small = num("GATEWAY_MAX_BODY_BYTES", 65_536)
        self.body_mcp = num("GATEWAY_MAX_MCP_BODY_BYTES", 1_048_576)
        self.exempt = ("/healthz", "/readyz")

    def classify(self, path: str) -> tuple[str, int, int]:
        """Returns (bucket, permits per minute, max body bytes)."""
        if path == "/register":
            return "register", self.register, self.body_small
        if path in ("/authorize", "/token", "/consent", "/auth/callback"):
            return "oauth", self.oauth, self.body_small
        if path == "/mcp" or path.startswith("/mcp/"):
            return "mcp", self.mcp, self.body_mcp
        return "other", self.other, self.body_small


def _json_response(status: int, body: dict, extra_headers=()):
    payload = json.dumps(body).encode()
    headers = [(b"content-type", b"application/json"), (b"content-length", str(len(payload)).encode()), *extra_headers]
    return [{"type": "http.response.start", "status": status, "headers": headers},
            {"type": "http.response.body", "body": payload}]


def request_limits(app, env):
    limits = Limits(env)
    if not limits.enabled:
        return app
    hits: dict[tuple[str, str], deque] = {}

    def allowed(key: tuple[str, str], permits: int, now: float) -> bool:
        q = hits.get(key)
        if q is None:
            if len(hits) >= _MAX_KEYS:   # bounded memory: drop expired keys, then refuse to track new ones as allowed
                for k in [k for k, v in hits.items() if not v or v[-1] < now - limits.window]:
                    del hits[k]
                if len(hits) >= _MAX_KEYS:
                    return False
            q = hits[key] = deque()
        while q and q[0] < now - limits.window:
            q.popleft()
        if len(q) >= permits:
            return False
        q.append(now)
        return True

    async def wrapper(scope, receive, send):
        if scope["type"] != "http" or scope["path"] in limits.exempt:
            return await app(scope, receive, send)

        bucket, permits, max_body = limits.classify(scope["path"])
        if not allowed((bucket, client_ip(scope)), permits, time.monotonic()):
            for m in _json_response(429, {"error": "rate_limited", "error_description": "Too many requests."},
                                    [(b"retry-after", b"60")]):
                await send(m)
            return

        declared = next((int(v) for k, v in scope.get("headers", []) if k == b"content-length" and v.isdigit()), None)
        if declared is not None and declared > max_body:
            for m in _json_response(413, {"error": "invalid_request", "error_description": "Request body too large."}):
                await send(m)
            return

        received = 0

        async def counting_receive():
            nonlocal received
            message = await receive()
            if message["type"] == "http.request":
                received += len(message.get("body", b""))
                if received > max_body:
                    raise _BodyTooLarge
            return message

        started = False

        async def tracking_send(message):
            nonlocal started
            if message["type"] == "http.response.start":
                started = True
            await send(message)

        try:
            await app(scope, counting_receive, tracking_send)
        except _BodyTooLarge:
            if not started:
                for m in _json_response(413, {"error": "invalid_request", "error_description": "Request body too large."}):
                    await send(m)

    return wrapper
