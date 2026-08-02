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
INDEX_HTML = Path(__file__).parent / "static" / "index.html"


def log(msg: str):
    with LOG_LOCK:
        LOG_TAIL.append(f"[{time.strftime('%H:%M:%S')}] {msg}")
        del LOG_TAIL[:-200]


def _decode_body(raw: bytes) -> str:
    """请求体解码:浏览器 fetch 发 UTF-8;Windows 终端 curl 可能发 GBK,兼容两者。"""
    try:
        return raw.decode("utf-8")
    except UnicodeDecodeError:
        return raw.decode("gbk", "replace")


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
            # 实时查 worker 真实状态(worker 首次被调用时启动;未开博途时会失败并给出原因)
            try:
                st = WORKER.call("status")
                st["outDir"] = OUT_DIR
                self._send(200, json.dumps(st, ensure_ascii=False).encode("utf-8"))
            except Exception as ex:
                self._send(200, json.dumps({
                    "ready": False, "attach": False, "project": None,
                    "error": str(ex), "outDir": OUT_DIR,
                }, ensure_ascii=False).encode("utf-8"))
            return
        if self.path == "/api/log":
            with LOG_LOCK:
                self._send(200, json.dumps({"lines": list(LOG_TAIL)}, ensure_ascii=False).encode("utf-8"))
            return
        self._send(404, b'{"error":"not found"}')

    def do_POST(self):
        if self.path == "/api/upload":
            # 前端文件选择器拿到的是 fakepath,文件需经上传接口落到本机再打开
            try:
                length = int(self.headers.get("Content-Length", 0))
                ctype = self.headers.get("Content-Type", "")
                import re
                m = re.search(r"boundary=([^;]+)", ctype)
                if not m:
                    raise ValueError("not multipart")
                body = self.rfile.read(length)
                boundary = ("--" + m.group(1)).encode()
                upload_dir = Path(OUT_DIR) / "uploads"
                upload_dir.mkdir(parents=True, exist_ok=True)
                saved = None
                for part in body.split(boundary):
                    if b'filename="' not in part:
                        continue
                    header_end = part.find(b"\r\n\r\n")
                    if header_end < 0:
                        continue
                    filename = re.search(rb'filename="([^"]+)"', part[:header_end])
                    if not filename:
                        continue
                    content = part[header_end + 4:]
                    if content.endswith(b"\r\n"):
                        content = content[:-2]
                    name = filename.group(1).decode("utf-8", "replace")
                    if not name.lower().endswith(".ap21"):
                        continue
                    target = upload_dir / name
                    target.write_bytes(content)
                    saved = str(target)
                    break
                if not saved:
                    raise ValueError("no .ap21 file in upload")
                log(f"⬆ 上传工程文件: {saved}")
                self._send(200, json.dumps({"ok": True, "path": saved}, ensure_ascii=False).encode("utf-8"))
            except Exception as ex:
                self._send(400, json.dumps({"ok": False, "error": str(ex)}, ensure_ascii=False).encode("utf-8"))
            return
        if self.path == "/api/save-scl":
            try:
                length = int(self.headers.get("Content-Length", 0))
                req = json.loads(_decode_body(self.rfile.read(length)))
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
            req = json.loads(_decode_body(self.rfile.read(length)))
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
    srv = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    print(f"TIA Web 面板: http://127.0.0.1:{port}")
    srv.serve_forever()


if __name__ == "__main__":
    main()


if __name__ == "__main__":
    main()
