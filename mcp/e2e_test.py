# -*- coding: utf-8 -*-
"""MCP 端到端验证:以真实 stdio MCP 客户端(与 Claude Code 相同方式)连接
mcp_server.py,调用全部工具走完整流程。

流程:create_project → add_tags → list_tag_tables → import_scl →
      compile_project → save_archive → shutdown
"""
import asyncio
import json
import os
import sys

sys.stdout.reconfigure(encoding="utf-8")  # Windows 重定向时默认 GBK,统一 UTF-8 便于查看

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

SERVER_PY = r"D:/Workspace/TiaOpennessWorker/mcp/mcp_server.py"
MCP_DIR = r"D:/Workspace/TiaOpennessWorker/mcp"
OUT_DIR = r"D:/Workspace/TiaOpennessWorker/output"


async def call(session, name, **args):
    res = await session.call_tool(name, args)
    txt = res.content[0].text if res.content else "(no content)"
    return json.loads(txt) if txt.startswith("{") else txt


async def main():
    params = StdioServerParameters(
        command="python",
        args=[SERVER_PY],
        cwd=MCP_DIR,
        env=None,
    )
    async with stdio_client(params) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()
            tools = sorted(t.name for t in (await session.list_tools()).tools)
            print("TOOLS:", tools)

            print(json.dumps(await call(session, "create_project",
                                        out_dir=OUT_DIR), ensure_ascii=False))
            print(json.dumps(await call(session, "add_tags", table="TagTable_1", tags=[
                {"name": "Motor_On", "dataType": "Bool", "address": "I0.0"},
                {"name": "Motor_Speed", "dataType": "Int", "address": "MW10"},
                {"name": "Motor_Value", "dataType": "Real", "address": ""},
            ]), ensure_ascii=False))
            print(json.dumps(await call(session, "list_tag_tables"), ensure_ascii=False))
            print(json.dumps(await call(session, "import_scl",
                                        scl_file=r"D:/Workspace/TiaOpennessWorker/samples/GoodSample.scl"),
                             ensure_ascii=False))
            print(json.dumps(await call(session, "compile_project"), ensure_ascii=False))
            print(json.dumps(await call(session, "save_archive", out_dir=OUT_DIR),
                             ensure_ascii=False))
            print(json.dumps(await call(session, "shutdown"), ensure_ascii=False))
    print("E2E_OK")


asyncio.run(main())
