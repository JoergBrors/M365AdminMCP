"""Live end-to-end check (G1/G2): real OAuth flow against the deployed gateway.

Uses FastMCP's OAuth client: discovery -> DCR -> PKCE authorize in the browser -> Entra login ->
gateway consent -> token -> initialize / tools/list through the gateway to the real .NET /mcp.
Interactive: the user signs in at Entra. Usage: python tools/gateway_e2e.py https://<gateway>/mcp
"""
import asyncio
import sys

from fastmcp import Client


async def main(url: str) -> int:
    try:
        async with Client(url, auth="oauth") as c:
            tools = await c.list_tools()
            print(f"tools/list OK via gateway: {len(tools)} tools")
            for t in tools[:15]:
                print(" -", t.name)
        return 0
    except BaseException as e:  # noqa: BLE001 - diagnostic output for the live check
        leaves = getattr(e, "exceptions", [e])
        for leaf in leaves:
            print("FAILED:", type(leaf).__name__, getattr(leaf, "code", ""), getattr(leaf, "message", leaf),
                  getattr(leaf, "data", ""))
        return 1


if __name__ == "__main__":
    sys.exit(asyncio.run(main(sys.argv[1])))
