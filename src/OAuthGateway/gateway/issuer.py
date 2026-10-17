"""Emit the issuer identifier without a trailing slash.

FastMCP serialises the issuer through pydantic's AnyHttpUrl, which always renders a bare host as
"https://host/". ChatGPT rejects the authorization response ("issuer does not match the expected issuer",
RFC 9207) although the metadata and the `iss` parameter agree with each other, i.e. it expects the form
without the trailing slash. This pure-ASGI wrapper rewrites exactly three places, nothing else:
  - values equal to the issuer in /.well-known/* JSON documents (issuer, authorization_servers),
  - the `iss` query parameter of redirects back to the client.
"""
from __future__ import annotations

import json
import logging
import os
import re
import urllib.parse as up

log = logging.getLogger("gateway.issuer")
_ISS_PARAM = re.compile(r"(iss=https%3A%2F%2F[^&%/]+)%2F(?=&|$)")


def _strip(value):
    if isinstance(value, str):
        return value[:-1] if re.fullmatch(r"https://[^/]+/", value) else value
    if isinstance(value, list):
        return [_strip(v) for v in value]
    if isinstance(value, dict):
        return {k: (_strip(v) if k in ("issuer", "authorization_servers") else v) for k, v in value.items()}
    return value


def _log_redirect(path: str, location: str) -> None:
    """Log where an OAuth redirect goes and which `iss` it carries - never code, state or other parameters."""
    parts = up.urlparse(location)
    query = up.parse_qs(parts.query)
    if "iss" in query:
        log.info("oauth redirect from=%s to=%s%s iss=%r params=%s", path, parts.netloc, parts.path, query["iss"][0],
                 sorted(query))


def _advertise_iss_param() -> bool:
    """RFC 9207 flag in the AS metadata. Default true (secure). Set GATEWAY_ADVERTISE_ISS_PARAM=false only as a
    documented workaround for client bugs (openai/codex#31573); it removes the client's mix-up check."""
    return os.environ.get("GATEWAY_ADVERTISE_ISS_PARAM", "true").lower() != "false"


def issuer_without_trailing_slash(app):
    async def wrapper(scope, receive, send):
        if scope["type"] != "http":
            return await app(scope, receive, send)
        is_wellknown = scope["path"].startswith("/.well-known/")
        state = {"start": None, "body": b""}

        async def send_wrapper(message):
            if message["type"] == "http.response.start":
                headers = list(message.get("headers", []))
                if not is_wellknown:
                    new_headers = []
                    for k, v in headers:
                        if k.lower() == b"location":
                            v = _ISS_PARAM.sub(r"\1", v.decode("latin-1")).encode("latin-1")
                            _log_redirect(scope["path"], v.decode("latin-1"))
                        new_headers.append((k, v))
                    return await send({**message, "headers": new_headers})
                state["start"] = message  # hold back until the body is rewritten
                return
            if message["type"] == "http.response.body" and is_wellknown and state["start"] is not None:
                state["body"] += message.get("body", b"")
                if message.get("more_body"):
                    return
                body = state["body"]
                try:
                    doc = _strip(json.loads(body))
                    if isinstance(doc, dict) and "authorization_response_iss_parameter_supported" in doc                             and not _advertise_iss_param():
                        doc["authorization_response_iss_parameter_supported"] = False
                    body = json.dumps(doc, separators=(",", ":")).encode()
                except ValueError:
                    pass
                headers = [(k, v) for k, v in state["start"].get("headers", []) if k.lower() != b"content-length"]
                headers.append((b"content-length", str(len(body)).encode()))
                await send({**state["start"], "headers": headers})
                return await send({"type": "http.response.body", "body": body})
            return await send(message)

        await app(scope, receive, send_wrapper)

    return wrapper
