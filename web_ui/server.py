# -*- coding: utf-8 -*-
"""TIA Portal Web 面板后端:零依赖(仅标准库)。

HTTP API:
    GET  /            → 面板页面(static/index.html)
    GET  /api/status  → {attach, project, ready}
    POST /api/cmd     → 通用命令代理 {cmd, args} → TiaWorker.call()
    日志:worker stderr 尾部接口 GET /api/log
"""
import json
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent / "mcp"))
from mcp_server import TiaWorker  # noqa: E402  复用 serve 通信(含自动 Attach)

ROOT = Path(__file__).parent.parent          # 仓库根
WORKER = TiaWorker()
LOG_TAIL = []          # 最近日志行(worker 侧 stderr 由 TiaWorker 丢弃,这里只记前端操作)
LOG_LOCK = threading.Lock()
OUT_DIR = str(ROOT / "output")
READY = {"attach": False, "project": None, "ready": False, "error": None, "outDir": OUT_DIR}

INDEX_HTML = Path(__file__).parent / "static" / "index.html"


def log(msg: str):
    with LOG_LOCK:
        LOG_TAIL.append(f"[{time.strftime('%H:%M:%S')}] {msg}")
        del LOG_TAIL[:-200]


def init_status():
    """启动时探测一次连接状态(worker 首次调用才会真正连接,这里做轻量探测)。"""
    try:
        # 用 list-projects 触发 worker 连接并感知 attach(attach 时工程自动绑定)
        result = WORKER.call("list-projects")
        READY["ready"] = True
        READY["error"] = None
    except Exception as ex:
        READY["error"] = str(ex)


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):  # 静默访问日志
        pass

    def _send(self, code: int, body: bytes, ctype: str = "application/json"):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/" or self.path.startswith("/index.html"):
            data = INDEX_HTML.read_bytes() if INDEX_HTML.exists() else b"<h1>index.html missing</h1>"
            self._send(200, data, "text/html; charset=utf-8")
            return
        if self.path == "/api/status":
            self._send(200, json.dumps(READY, ensure_ascii=False).encode("utf-8"))
            return
        if self.path == "/api/log":
            with LOG_LOCK:
                self._send(200, json.dumps({"lines": list(LOG_TAIL)}, ensure_ascii=False).encode("utf-8"))
            return
        self._send(404, b'{"error":"not found"}')

    def do_POST(self):
        if self.path == "/api/save-scl":
            try:
                length = int(self.headers.get("Content-Length", 0))
                req = json.loads(self.rfile.read(length).decode("utf-8"))
                name = req.get("name", "SCL_Import")
                code = req.get("code", "")
                out_dir = Path(OUT_DIR) / "scl"
                out_dir.mkdir(parents=True, exist_ok=True)
                path = out_dir / f"{name}.scl"
                path.write_text(code, encoding="utf-8")
                self._send(200, json.dumps({"ok": True, "path": str(path)}, ensure_ascii=False).encode("utf-8"))
            except Exception as ex:
                self._send(400, json.dumps({"ok": False, "error": str(ex)}, ensure_ascii=False).encode("utf-8"))
            return
        if self.path != "/api/cmd":
            self._send(404, b'{"error":"not found"}')
            return
        try:
            length = int(self.headers.get("Content-Length", 0))
            req = json.loads(self.rfile.read(length).decode("utf-8"))
            cmd = req.get("cmd", "")
            args = req.get("args", {}) or {}
        except Exception as ex:
            self._send(400, json.dumps({"ok": False, "error": f"bad request: {ex}"}, ensure_ascii=False).encode("utf-8"))
            return

        log(f"▶ {cmd} {json.dumps(args, ensure_ascii=False)[:120]}")
        try:
            result = WORKER.call(cmd, args)
            log(f"✓ {cmd} ok")
            body = json.dumps({"ok": True, "result": result}, ensure_ascii=False).encode("utf-8")
            self._send(200, body)
        except Exception as ex:
            log(f"✗ {cmd}: {ex}")
            body = json.dumps({"ok": False, "error": str(ex)}, ensure_ascii=False).encode("utf-8")
            self._send(200, body)


def main():
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8000
    threading.Thread(target=init_status, daemon=True).start()
    srv = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    print(f"TIA Web 面板: http://127.0.0.1:{port}")
    srv.serve_forever()


if __name__ == "__main__":
    main()


if __name__ == "__main__":
    main()
