"""Q-API-2 E2E, Gateway-Strecke: ruft den E2E-Host (echter McpServer + echte ApiServer-Logik, simulierter Graph) mit
derselben Client-Konfiguration auf, die das FastMCP-Gateway fuer das Backend nutzt (StreamableHttpTransport + ProxyClient(mode="legacy"),
siehe src/OAuthGateway/gateway/server.py:make_backend_client) - nur ohne OAuth. Das 1-MiB-SSE-Limit (mcp/httpx2) gilt unveraendert.

Aufruf:  python gateway_leg_e2e.py <MCP_URL> [expect-failure]
  expect-failure: Negativkontrolle - der Host laeuft mit abgeschaltetem Waechter; der Aufruf MUSS am 1-MiB-Limit scheitern.
"""
import asyncio
import hashlib
import json
import sys

from fastmcp.client.transports import StreamableHttpTransport
from fastmcp.server.providers.proxy import ProxyClient  # gleicher Import-Pfad wie im Gateway (siehe server.py)


def make_client(url: str) -> ProxyClient:
    return ProxyClient(StreamableHttpTransport(url), mode="legacy")


def u16len(s: str) -> int:
    """Laenge in UTF-16-Code-Units (wie .NET string.Length / Chunk-Offsets des Servers)."""
    return len(s.encode("utf-16-le")) // 2


def text_of(result) -> str:
    return "".join(getattr(c, "text", "") for c in result.content)


async def main(url: str, expect_failure: bool) -> int:
    async with make_client(url) as client:
        if expect_failure:
            try:
                await client.call_tool("get_service_health_issues", {})
            except Exception as ex:  # noqa: BLE001
                msg = str(ex)
                print("NEGATIVE-CONTROL error:", msg[:160])
                return 0 if "byte limit" in msg or "exceeded" in msg else 2
            print("NEGATIVE-CONTROL: expected the 1 MiB SSE error but the call succeeded")
            return 3

        # 1) Legacy-Tool mit riesigem Tenant: darf nicht mehr am SSE-Limit scheitern, Ergebnis ist markiert.
        legacy = json.loads(text_of(await client.call_tool("get_service_health_issues", {})))
        assert legacy["totalItems"] == 31 and legacy["truncated"] is True and legacy["isPartial"] is True, legacy.keys()
        print(f"legacy: ok, returnedItems={legacy['returnedItems']}/{legacy['totalItems']}")

        # 2) Suche kompakt
        search = json.loads(text_of(await client.call_tool("search_service_health_issues", {"service": "Exchange Online", "maxResults": 100})))
        assert search["itemCount"] == 31 and search["hasMore"] is False
        issue_id = "EXBIG"
        print(f"search: ok, items={search['itemCount']}")

        # 3) Verlustfreier Content ueber mehrere Chunks durch das Gateway-Client-Setup
        content_id = f"issue:{issue_id}:post:0"
        parts, token, chunks = [], None, 0
        while True:
            args = {"contentId": content_id}
            if token:
                args["continuationToken"] = token
            part = json.loads(text_of(await client.call_tool("get_service_health_issue_content", args)))
            assert part["status"] == "ok", part
            assert part["offset"] == sum(u16len(p) for p in parts)
            parts.append(part["text"])
            chunks += 1
            if not part["hasMore"]:
                break
            token = part["continuationToken"]
        joined = "".join(parts)
        unit = '<p class="x">Größe äöü 日本語 😀🎉 "zitat" \\pfad\\ & <b>fett</b></p>\n'
        raw = (unit * (1_800_000 // u16len(unit) + 2)).encode("utf-16-le")[: 1_800_000 * 2]
        if 0xD800 <= int.from_bytes(raw[-2:], "little") <= 0xDBFF:  # keine halbe Surrogat-Paarung
            raw = raw[:-2]
        expected = raw.decode("utf-16-le")
        assert joined == expected, "Content nicht identisch"
        print(f"content: ok, chunks={chunks}, chars={u16len(joined)}, sha256={hashlib.sha256(joined.encode()).hexdigest()[:16]}")
    return 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main(sys.argv[1], len(sys.argv) > 2 and sys.argv[2] == "expect-failure")))
