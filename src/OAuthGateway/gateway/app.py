"""ASGI entrypoint:  python -m uvicorn gateway.app:app --host 0.0.0.0 --port 8000"""
from __future__ import annotations

import logging

from fastmcp import FastMCP
from fastmcp.server.auth.providers.azure import AzureProvider
from key_value.aio.stores.memory import MemoryStore
from key_value.aio.wrappers.encryption import FernetEncryptionWrapper
from starlette.responses import JSONResponse

from .config import GatewayConfig, load_config
from .server import build_proxy

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
log = logging.getLogger("gateway")


def build_storage(cfg: GatewayConfig):
    if cfg.storage_account:
        from azure.identity.aio import DefaultAzureCredential
        from key_value.aio.stores.azure_tables import AzureTablesStore
        inner = AzureTablesStore(account_name=cfg.storage_account, credential=DefaultAzureCredential(),
                                 table_name=cfg.storage_table, auto_create=False)
    else:
        inner = MemoryStore()
    return FernetEncryptionWrapper(key_value=inner, source_material=cfg.storage_encryption_key,
                                   salt="m365-mcp-gateway-oauth-state", raise_on_decryption_error=False)


def build_auth(cfg: GatewayConfig, storage) -> AzureProvider:
    return AzureProvider(
        client_id=cfg.client_id, client_secret=cfg.client_secret, tenant_id=cfg.tenant_id,
        identifier_uri=cfg.mcp_app_id_uri, required_scopes=["Mcp.Access"],
        base_url=cfg.public_url, jwt_signing_key=cfg.jwt_signing_key, client_storage=storage,
        forward_resource=False,                      # Entra rejects RFC 8707 resource (AADSTS9010010)
        require_authorization_consent=True,          # never "external"/False in deployments
    )


def create_app(cfg: GatewayConfig | None = None):
    cfg = cfg or load_config()
    mcp: FastMCP = build_proxy(cfg.backend_mcp_url, build_auth(cfg, build_storage(cfg)))

    @mcp.custom_route("/healthz", methods=["GET"])
    async def healthz(request):          # liveness: process is up; no secrets, no upstream calls
        return JSONResponse({"status": "healthy"})

    @mcp.custom_route("/readyz", methods=["GET"])
    async def readyz(request):           # readiness: configuration validated at startup
        return JSONResponse({"status": "ready", "storage": "azure-tables" if cfg.storage_account else "memory"})

    log.info("gateway configured: public=%s backend_host=%s storage=%s", cfg.public_url, cfg.backend_host,
             "azure-tables" if cfg.storage_account else "memory")
    return mcp.http_app(path="/mcp")


def _lazy():
    import os
    return create_app() if os.environ.get("GATEWAY_ENTRA_CLIENT_ID") else None


app = _lazy()
