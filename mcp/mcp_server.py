#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
TIA Portal Openness MCP server
==============================
把 TiaOpennessWorker.exe 的 serve 长驻模式封装为 MCP 工具,让 Claude Code 能:
创建/打开 TIA 工程、管理变量表、导入 SCL、编译诊断、保存归档。

架构:本进程(MCP stdio)持有一个 serve 子进程,通过 stdin/stdout 逐行 JSON 通信。
serve 子进程的 stderr 重定向到日志文件(避免管道填满导致死锁)。

注意:第一个工具调用会启动 TIA Portal(无界面),耗时 30-90 秒;之后同会话内
后续调用都是毫秒级。TIA 实例在整个 MCP 会话期间常驻,shutdown 时才关闭。
"""

import json
import os
import subprocess
import threading

from mcp.server.fastmcp import FastMCP

_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))  # 仓库根
WORKER_EXE = os.path.join(_ROOT, "src", "TiaOpennessWorker", "bin", "Debug", "net48", "TiaOpennessWorker.exe")
OUT_DIR = os.path.join(_ROOT, "output")
WORKER_LOG = os.path.join(OUT_DIR, "mcp_worker.log")

# 本地配置(不入库):非标准安装时设 openness_dir 指向 PublicAPI/V21/net48
_CONFIG = {}
_cfg_path = os.path.join(_ROOT, "config.json")
if os.path.exists(_cfg_path):
    try:
        _CONFIG = json.load(open(_cfg_path, encoding="utf-8"))
    except Exception:
        _CONFIG = {}

mcp = FastMCP(
    "tia-openness",
    instructions=(
        "TIA Portal Openness 工具集。要求用户已打开 TIA Portal(带界面),worker 自动 Attach 进用户实例,"
        "直接写进用户正在看的工程(界面实时可见,ready 时无需再 create_project/open_project,用 list_tag_tables 等直接操作)。"
        "若用户还没开博途,工具调用会失败并提示'请先打开博途窗口'——必须让用户先打开博途,不再自动启动无界面实例。"
        "若工程没有 PLC 设备,先调用 add_cpu。"
        "典型流程:add_tags → import_scl 或 generate_lad_block → compile_project。"
        "compile_project 返回 messages(含 block/description),AI 可据此修复 SCL 后重新 import_scl + compile_project。"
    ),
)


class TiaWorker:
    """serve 子进程管理器:线程安全、逐行 JSON 协议。"""

    def __init__(self, exe=WORKER_EXE):
        self.exe = exe
        self._proc = None
        self._lock = threading.Lock()
        self._next_id = 0

    def _ensure_started(self):
        if self._proc is not None and self._proc.poll() is None:
            return
        os.makedirs(os.path.dirname(WORKER_LOG), exist_ok=True)
        err_file = open(WORKER_LOG, "ab")
        env = dict(os.environ)
        openness = _CONFIG.get("openness_dir") or os.environ.get("TIA_OPENNESS_DIR")
        if openness:
            env["TIA_OPENNESS_DIR"] = openness
        self._proc = subprocess.Popen(
            [self.exe, "serve"],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=err_file,
            cwd=os.path.dirname(self.exe),
            env=env,
        )
        line = self._proc.stdout.readline()
        if not line or b'"ready":true' not in line:
            # 进程可能已退出(最常见:没开博途窗口)或启动失败:取 stderr 日志尾部找原因
            reason = ""
            try:
                with open(WORKER_LOG, "rb") as f:
                    reason = "".join(l.decode("utf-8", "replace") for l in f.readlines()[-4:])
            except Exception:
                pass
            hint = (reason.strip()[:200] or "请先打开博途窗口再连接")
            raise RuntimeError("worker 未就绪: " + hint)

    def call(self, cmd, args=None):
        with self._lock:
            self._ensure_started()
            self._next_id += 1
            payload = (
                json.dumps({"id": str(self._next_id), "cmd": cmd, "args": args or {}}) + "\n"
            ).encode("utf-8")
            self._proc.stdin.write(payload)
            self._proc.stdin.flush()
            line = self._proc.stdout.readline()
            if not line:
                raise RuntimeError(
                    f"worker 进程已退出(调用 {cmd} 时),日志见 {WORKER_LOG}"
                )
            resp = json.loads(line.decode("utf-8"))
            if not resp.get("ok"):
                raise RuntimeError(resp.get("error", "未知错误"))
            return resp.get("result", {})

    def shutdown(self):
        try:
            if self._proc is not None and self._proc.poll() is None:
                self.call("shutdown")
                self._proc.wait(timeout=30)
        except Exception:
            pass
        finally:
            if self._proc is not None:
                try:
                    self._proc.kill()
                except Exception:
                    pass
            self._proc = None


_worker = TiaWorker()


# ---------- MCP 工具 ----------

@mcp.tool()
def create_project(out_dir: str, project_name: str | None = None) -> dict:
    """在 TIA Portal 中新建工程并添加一个 S7-1500 CPU(CPU 1511-1 PN)。

    Args:
        out_dir: 工程输出目录(绝对路径,如 D:/Workspace/TiaOpennessWorker/output)。
        project_name: 工程名,省略时自动生成。
    Returns:
        {projectName, device}
    """
    return _worker.call("create-project", {"outDir": out_dir, "projectName": project_name})


@mcp.tool()
def open_project(project_file: str) -> dict:
    """打开已有 TIA 工程(.ap21 文件,绝对路径)。

    Returns:
        {projectFile, projectName}
    """
    return _worker.call("open-project", {"projectFile": project_file})


@mcp.tool()
def close_project() -> dict:
    """关闭当前打开的工程(不退出 TIA 实例)。"""
    return _worker.call("close-project", {})


@mcp.tool()
def generate_scl_template(template: str, params: dict | None = None) -> dict:
    """按模板生成常用程序并导入(模板带中文注释)。

    Args:
        template: traffic-light(交通灯,绿/黄/红定时循环)
                  motor-rev(电机正反转互锁)
                  counter(计数分拣,满 N 输出)
        params: 可选参数,如 {"GREEN_TIME": "5s", "TARGET": "25"}
    Returns:
        {template, sclFile, generated}
    """
    return _worker.call("gen-template", {"template": template, "params": params or {}})


@mcp.tool()
def add_cpu() -> dict:
    """给当前工程添加 S7-1500 CPU(CPU 1511-1 PN)。

    Attach 到用户已打开的工程且该工程还没有 PLC 设备时使用。
    Returns:
        {device, plcSoftware}
    """
    return _worker.call("add-cpu", {})


@mcp.tool()
def list_projects() -> dict:
    """列出 TIA Portal 当前打开的全部工程。

    Returns:
        {projects: [{name, isCurrent}]}
    """
    return _worker.call("list-projects", {})


@mcp.tool()
def use_project(name: str) -> dict:
    """切换到指定的已打开工程(有多个工程时)。

    Args:
        name: 工程名(用 list_projects 查看)。
    """
    return _worker.call("use-project", {"name": name})


@mcp.tool()
def import_scl(scl_file: str) -> dict:
    """导入 SCL 源文件(.scl,绝对路径)并生成块。

    语法错误不会在这里报,而是要在 compile_project 的诊断中看。
    Returns:
        {sclFile, generated}
    """
    return _worker.call("import-scl", {"sclFile": scl_file})


@mcp.tool()
def import_scl_source(source: str, name: str) -> dict:
    """直接导入 SCL 源码字符串(无需先写文件)并生成块,支持中文变量/中文注释。

    优先用这个而不是 import_scl:AI 直接写代码字符串即可。
    Args:
        source: 完整的 SCL 源码(FUNCTION_BLOCK / FUNCTION ... END_* 全文)。
        name: 块名(仅用于临时文件名,实际块名由源码里的块声明决定)。
    Returns:
        {sclFile, generated}
    """
    import tempfile
    fd, path = tempfile.mkstemp(prefix="tia_scl_", suffix=".scl")
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(source.encode("utf-8"))  # UTF-8(无 BOM,worker 会预处理)
        return _worker.call("import-scl", {"sclFile": path})
    finally:
        try:
            os.remove(path)
        except Exception:
            pass


@mcp.tool()
def compile_project() -> dict:
    """编译当前工程的 PLC 软件,返回结构化诊断。

    Returns:
        {state, errors, warnings, messages: [{state, block, description}]}
    messages 中的 Error 条目就是需要 AI 修复的内容(block 指明块名)。
    """
    return _worker.call("compile", {})


@mcp.tool()
def add_tags(table: str, tags: list[dict]) -> dict:
    """向变量表批量添加标签(变量表不存在则自动创建)。

    Args:
        table: 变量表名,如 TagTable_1。
        tags: 标签数组,每项 {name, dataType, address?}。
              dataType 如 Bool/Int/Real;address 如 "I0.0"/"MW10",
              留空则由系统自动分配地址。
    Returns:
        {table, created}
    """
    return _worker.call("add-tags", {"table": table, "tags": tags})


@mcp.tool()
def read_project(out_dir: str | None = None) -> dict:
    """读取当前工程:块列表(名称/类型/编号/语言)+ 变量表全量(名称/类型/地址)。

    可选 out_dir 导出全部块内容(SCL 块→.scl 文本,其他→.xml SimaticML),
    用于 AI 分析/理解用户已有程序。
    Returns:
        {blocks: [...], tags: [...], exported: [{path}]}
    """
    return _worker.call("read-project", {"outDir": out_dir})


@mcp.tool()
def list_blocks() -> dict:
    """列出当前工程的程序块(名称/类型/编号/语言)。"""
    return _worker.call("list-blocks", {})


@mcp.tool()
def add_hmi() -> dict:
    """给当前工程添加 HMI 设备(KTP700 Basic PN,480x800 竖屏)。

    Returns:
        {device, hmiTarget}
    """
    return _worker.call("add-hmi", {})


@mcp.tool()
def generate_hmi_screen(spec: dict, out_dir: str | None = None) -> dict:
    """生成 HMI 画面并导入(经典 WinCC Comfort)。

    spec 结构:
      name: 画面名; number: 画面编号; width/height: 画面尺寸
        (KTP700 Basic PN 竖屏为 480x800)
      items: [{type, name, text, left, top, width, height}]
        type: TextField(文本)/ Button(按钮,TextOff+TextOn)/ Lamp(指示灯矩形)/ Rectangle
    当前为静态元素(变量绑定后续版本支持)。
    Returns:
        {screenName, xmlFile, imported}
    """
    return _worker.call("gen-hmi", {"spec": spec, "outDir": out_dir})


@mcp.tool()
def save_project() -> dict:
    """保存当前工程(写入操作后已自动保存,此命令用于显式确认)。"""
    return _worker.call("save-project", {})


@mcp.tool()
def list_tag_tables() -> dict:
    """列出当前工程的全部变量表及每个表的标签数量。

    Returns:
        {tables: [{name, tags}]}
    """
    return _worker.call("list-tag-tables", {})


@mcp.tool()
def generate_lad_block(spec: dict, out_dir: str | None = None) -> dict:
    """按规格生成 LAD 梯形图块并导入 TIA Portal(SimaticML XML),导入后需 compile_project 验证。

    spec 结构:
      type: "FB" 或 "FC"(默认 FB)
      name: 块名(必填); number: 块编号(默认 20001,冲突时换大数)
      comment: 块注释(可选)
      interface: {input|output|inout|static|temp: [{name, datatype}]}
                 (FB 可用 static 放定时器实例;FC 不允许 static 段)
      networks: [{title?, comment?, recipe, args}]

    配方 recipe 与 args:
      contact_coil: {operand, output}                     单触点 → 线圈
      self_lock:    {start, stop, output}                 启停自锁(并联自保)
      blink:        {enable, output, ton, period}         LED 闪烁(TON 自复位,period 如 "T#500ms")
      set / reset:  {operand, output}                     置位 / 复位线圈
      compare:      {kind: Eq|Ne|Gt|Ge|Lt|Le, srcType: Int|Real|DInt..., in1, in2, output}

    操作数用接口成员名;period 可写常量 "T#500ms"。
    Returns:
        {blockName, xmlFile, imported}
    """
    return _worker.call("gen-lad", {"spec": spec, "outDir": out_dir})


@mcp.tool()
def save_archive(out_dir: str | None = None) -> dict:
    """保存并归档当前工程,产出 .ap21 工程和 .zap21 归档。

    Args:
        out_dir: 归档输出目录,省略则用 create_project 时指定的目录。
    Returns:
        {projectFile, archiveFile}
    """
    return _worker.call("save-archive", {"outDir": out_dir})


@mcp.tool()
def shutdown() -> dict:
    """关闭工程并退出 TIA Portal(会话结束时调用,释放进程与许可证)。"""
    result = _worker.call("shutdown", {})
    _worker.shutdown()
    return result


if __name__ == "__main__":
    mcp.run()  # stdio transport
