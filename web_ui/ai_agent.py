# -*- coding: utf-8 -*-
"""AI 助手代理:浏览器聊天 → LLM(OpenAI 兼容接口,默认 DeepSeek)→ TIA serve 工具调用。

零依赖(仅标准库 urllib)。配置(config.json 的 "llm" 段,该文件不入库):
  {
    "llm": {
      "provider": "deepseek" | "dashscope" | "openai",
      "api_key": "sk-...",
      "model": "deepseek-chat",      // 可选,省略用各 provider 默认
      "base_url": "..."              // 可选,自定义兼容端点
    }
  }
key 回退顺序:config.json → 环境变量(DEEPSEEK_API_KEY / DASHSCOPE_API_KEY)→
~/qwen-vision-mcp/.env 的 QWV_API_KEY。
"""

import json
import os
import urllib.error
import urllib.request
from pathlib import Path

# 各 provider 预设(OpenAI 兼容 /chat/completions 端点)
PROVIDERS = {
    "deepseek": {"base_url": "https://api.deepseek.com/chat/completions", "model": "deepseek-chat"},
    "dashscope": {"base_url": "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions", "model": "qwen-max"},
    "openai": {"base_url": "https://api.openai.com/v1/chat/completions", "model": "gpt-4o-mini"},
}
MAX_TOOL_ROUNDS = 6      # 单条消息最多工具调用轮数
RESULT_SNIPPET = 8000    # 工具结果回填给 LLM 的最大长度

SYSTEM_PROMPT = (
    "你是 TIA Portal(博途)自动化助手,通过工具操作**用户已经手动打开**的博途工程。规则:\n"
    "1. 开始工作前先调用 connect_project 绑定用户当前打开的工程;如果报错提示没开博途,就请用户先去打开博途窗口。\n"
    "2. 用户的工程类请求(查看/新建/修改/写入/编译/删除等)**必须通过工具真实执行**,"
    "禁止只用文字假装完成,禁止编造结果。拿不准先调用 read_project / list_tag_tables 了解现状。\n"
    "3. 全部用中文回复,简洁明了,像工程师对工程师说话,不要长篇大论。\n"
    "4. 每次工具执行完,用一两句话向用户汇报结果(成功/失败/数量);编译有错误时给出具体修复建议。\n"
    "5. 用户要变量表用 add_tags,要程序用 import_scl(SCL 源文件)或 generate_lad_block(梯形图配方)。\n"
    "6. 写 SCL 时尽量用中文变量名和中文注释,支持 UTF-8。\n"
    "7. 工具执行失败时,如实告诉用户失败原因,并给出下一步建议。"
)

# 工具定义(OpenAI 兼容 function calling 格式),映射见 TOOL_MAP
TOOLS = [
    {"type": "function", "function": {"name": "connect_project",
     "description": "绑定博途窗口里当前打开的工程(必须先手动打开博途并载入工程)。已绑定时重复调用无害。",
     "parameters": {"type": "object", "properties": {}}}},
    {"type": "function", "function": {"name": "disconnect_project",
     "description": "解除 worker 与当前工程的绑定,不影响博途窗口和工程本身。",
     "parameters": {"type": "object", "properties": {}}}},
    {"type": "function", "function": {"name": "read_project",
     "description": "读取当前工程:程序块清单(名称/类型/编号/语言)与变量表全量(名称/类型/地址)。可选 out_dir 把块内容导出为 .scl/.xml 文件。写代码前建议先调用了解现状。",
     "parameters": {"type": "object", "properties": {"out_dir": {"type": "string", "description": "导出目录(绝对路径),省略则不导出"}}}}},
    {"type": "function", "function": {"name": "list_tag_tables",
     "description": "列出当前工程的全部变量表及每个表的标签数量。",
     "parameters": {"type": "object", "properties": {}}}},
    {"type": "function", "function": {"name": "list_blocks",
     "description": "列出当前工程的程序块。",
     "parameters": {"type": "object", "properties": {}}}},
    {"type": "function", "function": {"name": "add_tags",
     "description": "向变量表批量添加标签(变量表不存在则自动创建)。",
     "parameters": {"type": "object", "properties": {
         "table": {"type": "string", "description": "变量表名,如 TagTable_1"},
         "tags": {"type": "array", "description": "标签数组",
                  "items": {"type": "object", "properties": {
                      "name": {"type": "string", "description": "变量名,可用中文"},
                      "dataType": {"type": "string", "description": "Bool/Int/Real/Word/DInt"},
                      "address": {"type": "string", "description": "如 I0.0 / MW10,省略则自动分配"}},
                      "required": ["name", "dataType"]}}},
         "required": ["table", "tags"]}}},
    {"type": "function", "function": {"name": "import_scl",
     "description": "导入 SCL 源文件(.scl 文件绝对路径)生成程序块,支持中文变量名与中文注释。需要先在本地写好 .scl 文件,路径要真实存在。",
     "parameters": {"type": "object", "properties": {
         "scl_file": {"type": "string", "description": ".scl 文件绝对路径"}},
         "required": ["scl_file"]}}},
    {"type": "function", "function": {"name": "generate_lad_block",
     "description": "按配方生成 LAD 梯形图块(FB/FC)并导入 TIA。配方:contact_coil(触点→线圈)、self_lock(启停自锁)、blink(TON 闪烁)、set/reset(置位/复位)、compare(Eq/Ne/Gt/Ge/Lt/Le 比较)、arith(Add/Sub/Mul/Div/Mod 运算)、counter(CTU/CTD 计数)、timer(ton/tof/tp 定时)。",
     "parameters": {"type": "object", "properties": {
         "spec": {"type": "object", "description": "LAD 规格:{type: FB|FC, name: 块名, number: 编号, comment, interface: {input/output/static: [{name, datatype}]}, networks: [{recipe, args}]}", "properties": {
             "type": {"type": "string", "enum": ["FB", "FC"]},
             "name": {"type": "string"},
             "number": {"type": "integer"},
             "comment": {"type": "string"},
             "interface": {"type": "object"},
             "networks": {"type": "array"}}}},
         "required": ["spec"]}}},
    {"type": "function", "function": {"name": "compile_project",
     "description": "编译当前工程的 PLC 软件,返回错误/警告诊断(含块名与描述)。编译报错时据此修改 SCL/LAD 后重新编译。",
     "parameters": {"type": "object", "properties": {}}}},
    {"type": "function", "function": {"name": "save_project",
     "description": "显式保存当前工程(每次写操作后已自动保存,一般无需调用)。",
     "parameters": {"type": "object", "properties": {}}}},
]

TOOL_MAP = {
    "connect_project": "connect",
    "disconnect_project": "close-project",
    "read_project": "read-project",
    "list_tag_tables": "list-tag-tables",
    "list_blocks": "list-blocks",
    "add_tags": "add-tags",
    "import_scl": "import-scl",
    "generate_lad_block": "gen-lad",
    "compile_project": "compile",
    "save_project": "save-project",
}


class ChatAgent:
    """会话代理:维护多轮历史,执行 LLM 工具调用循环。线程安全由调用方(TiaWorker 锁)保证。"""

    def __init__(self, worker):
        self.worker = worker
        cfg = self._load_config()
        envf = self._env_file_values()  # ~/qwen-vision-mcp/.env(用户已有配置)
        provider = (cfg.get("provider") or "dashscope").lower()
        prov = PROVIDERS.get(provider, {})
        # key 优先级:config.json → 对应 provider 环境变量 → qwen-vision/.env 复用
        key = cfg.get("api_key") or ""
        if not key and provider == "deepseek":
            key = os.environ.get("DEEPSEEK_API_KEY") or ""
        if not key:
            key = os.environ.get("DASHSCOPE_API_KEY") or envf.get("QWV_API_KEY") or envf.get("DASHSCOPE_API_KEY") or ""
        self.api_key = key
        self.model = (cfg.get("model") or os.environ.get("DASHSCOPE_MODEL")
                      or prov.get("model") or "deepseek-chat")
        base = (cfg.get("base_url") or prov.get("base_url")
                or envf.get("QWV_BASE_URL") or "https://api.deepseek.com/chat/completions")
        self.base_url = base if base.endswith("/chat/completions") else base.rstrip("/") + "/chat/completions"
        self.history = []

    @staticmethod
    def _load_config():
        root = Path(__file__).resolve().parent.parent  # 仓库根
        try:
            with open(root / "config.json", encoding="utf-8") as f:
                return json.load(f).get("llm") or {}
        except Exception:
            return {}

    @staticmethod
    def _env_file_values():
        """读 ~/qwen-vision-mcp/.env 已有的配置(QWV_API_KEY / QWV_BASE_URL 等,用户复用)。"""
        values = {}
        try:
            for line in (Path.home() / "qwen-vision-mcp" / ".env").read_text(encoding="utf-8").splitlines():
                line = line.strip()
                if "=" in line and not line.startswith("#"):
                    k, v = line.split("=", 1)
                    values[k.strip()] = v.strip().strip('"').strip("'")
        except Exception:
            pass
        return values

    def _call_llm(self, messages, tools=None):
        if not self.api_key:
            raise RuntimeError(
                "未配置 AI 服务:请在 config.json 设置 \"llm\": {\"provider\": \"deepseek\", \"api_key\": \"sk-...\"}"
            )
        payload = {"model": self.model, "messages": messages, "temperature": 0.2}
        if tools:
            payload["tools"] = tools
        req = urllib.request.Request(
            self.base_url,
            data=json.dumps(payload).encode("utf-8"),
            headers={"Content-Type": "application/json", "Authorization": "Bearer " + self.api_key},
            method="POST",
        )
        try:
            with urllib.request.urlopen(req, timeout=180) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            body = e.read().decode("utf-8", "replace")[:500]
            raise RuntimeError(f"AI 服务返回 {e.code}: {body}")
        except Exception as e:
            raise RuntimeError(f"AI 服务请求失败: {e}")

    def chat(self, message):
        self.history = self.history[-30:]  # 只保留最近 30 条,防无限增长
        self.history.append({"role": "user", "content": message})
        steps = []
        for _ in range(MAX_TOOL_ROUNDS):
            # 每轮重建 messages:工具结果追加进 history 后必须让模型能看到
            messages = [{"role": "system", "content": SYSTEM_PROMPT}] + self.history
            resp = self._call_llm(messages, TOOLS)
            msg = ((resp.get("choices") or [{}])[0].get("message") or {})
            tool_calls = msg.get("tool_calls") or []
            if not tool_calls:
                reply = (msg.get("content") or "").strip() or "(AI 未返回文字)"
                self.history.append({"role": "assistant", "content": reply})
                return {"reply": reply, "steps": steps}
            # 记录 assistant 的工具调用意图
            self.history.append({"role": "assistant", "content": msg.get("content") or "", "tool_calls": tool_calls})
            for tc in tool_calls:
                fn = tc.get("function") or {}
                name = fn.get("name") or ""
                try:
                    args = json.loads(fn.get("arguments") or "{}")
                except Exception:
                    args = {}
                cmd = TOOL_MAP.get(name)
                if not cmd:
                    result, ok = {"error": f"未知工具 {name}"}, False
                else:
                    try:
                        result, ok = self.worker.call(cmd, args), True
                    except Exception as e:
                        result, ok = {"error": str(e)}, False
                steps.append({"tool": name, "args": args, "ok": ok, "result": result})
                self.history.append({
                    "role": "tool",
                    "tool_call_id": tc.get("id") or name,
                    "content": json.dumps(result, ensure_ascii=False)[:RESULT_SNIPPET],
                })
        return {"reply": "(工具调用轮数太多,已停止。请缩小任务范围后再说一次)", "steps": steps}

    def clear(self):
        self.history = []
