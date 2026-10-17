"""Startup configuration with fail-fast validation. Secrets only come from the environment
(Key Vault references in App Service); nothing here has defaults for secret values."""
from __future__ import annotations

import os
import re
from dataclasses import dataclass, field
from urllib.parse import urlparse

_GUID = re.compile(r"^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$")


class ConfigError(RuntimeError):
    pass


@dataclass(frozen=True)
class GatewayConfig:
    tenant_id: str
    client_id: str
    client_secret: str = field(repr=False)
    jwt_signing_key: str = field(repr=False)
    storage_encryption_key: str = field(repr=False)
    mcp_app_id_uri: str
    mcp_app_client_id: str
    public_url: str
    backend_mcp_url: str
    storage_account: str | None = None
    storage_table: str = "oauthstate"
    allow_memory_storage: bool = False
    backend_timeout_seconds: float = 25.0

    @property
    def backend_host(self) -> str:
        return urlparse(self.backend_mcp_url).hostname or ""


def _req(env, name: str) -> str:
    v = (env.get(name) or "").strip()
    if not v:
        raise ConfigError(f"missing required setting {name}")
    return v


def load_config(env=None) -> GatewayConfig:
    env = os.environ if env is None else env
    cfg = GatewayConfig(
        tenant_id=_req(env, "AZURE_TENANT_ID"),
        client_id=_req(env, "GATEWAY_ENTRA_CLIENT_ID"),
        client_secret=_req(env, "GATEWAY_ENTRA_CLIENT_SECRET"),
        jwt_signing_key=_req(env, "GATEWAY_JWT_SIGNING_KEY"),
        storage_encryption_key=_req(env, "GATEWAY_STORAGE_ENCRYPTION_KEY"),
        mcp_app_id_uri=_req(env, "MCP_APP_ID_URI"),
        mcp_app_client_id=_req(env, "MCP_APP_CLIENT_ID"),
        public_url=_req(env, "GATEWAY_PUBLIC_URL").rstrip("/"),
        backend_mcp_url=_req(env, "BACKEND_MCP_URL"),
        storage_account=(env.get("GATEWAY_STORAGE_ACCOUNT") or None),
        storage_table=env.get("GATEWAY_STORAGE_TABLE", "oauthstate"),
        allow_memory_storage=env.get("GATEWAY_ALLOW_MEMORY_STORAGE", "false").lower() == "true",
    )
    problems = []
    if not _GUID.match(cfg.tenant_id):
        problems.append("AZURE_TENANT_ID must be a GUID (single tenant, no 'common'/'organizations')")
    if not _GUID.match(cfg.client_id):
        problems.append("GATEWAY_ENTRA_CLIENT_ID must be a GUID")
    if not _GUID.match(cfg.mcp_app_client_id):
        problems.append("MCP_APP_CLIENT_ID must be a GUID")
    if len(cfg.jwt_signing_key) < 32:
        problems.append("GATEWAY_JWT_SIGNING_KEY must be at least 32 characters")
    if len(cfg.storage_encryption_key) < 32:
        problems.append("GATEWAY_STORAGE_ENCRYPTION_KEY must be at least 32 characters")
    if cfg.jwt_signing_key == cfg.storage_encryption_key:
        problems.append("signing key and storage encryption key must differ")
    for name, url in (("GATEWAY_PUBLIC_URL", cfg.public_url), ("BACKEND_MCP_URL", cfg.backend_mcp_url)):
        if urlparse(url).scheme != "https":
            problems.append(f"{name} must be https")
    if urlparse(cfg.backend_mcp_url).path.rstrip("/") != "/mcp":
        problems.append("BACKEND_MCP_URL must end with /mcp")
    if not cfg.mcp_app_id_uri.startswith("api://"):
        problems.append("MCP_APP_ID_URI must start with api://")
    if not cfg.storage_account and not cfg.allow_memory_storage:
        problems.append("GATEWAY_STORAGE_ACCOUNT is required (set GATEWAY_ALLOW_MEMORY_STORAGE=true only for local tests)")
    if problems:
        raise ConfigError("; ".join(problems))
    return cfg
