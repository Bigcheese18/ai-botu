# TIA Portal Openness Worker — AI 直连博途操作手册

本手册让 AI 在任意会话直接驱动 TIA Portal:读取/写入/修改用户工程(Attach 模式)
或自建工程(headless)。所有能力均经实机验证(2026-08-01)。

## 架构速览

```
Claude Code → MCP(tia-openness,13 工具)→ serve worker(JSON 行协议)→ TIA Portal V21
```

- **Worker**:`src\TiaOpennessWorker\`(net48)。入口 `TiaOpennessWorker.exe <cmd>`
- **MCP**:`mcp\mcp_server.py`,已注册 `claude mcp add --scope user tia-openness -- python D:/Workspace/TiaOpennessWorker/mcp/mcp_server.py`
- **两种模式**:用户已开博途 → Attach(写进用户工程,界面实时可见);无实例 → headless 自动启动
- **构建**:`dotnet build src/TiaOpennessWorker/TiaOpennessWorker.csproj`(DLL 引用在 `D:\Workspace\Portal V21\PublicAPI\V21\net48`,CopyLocal=False)
- **回归测试**:`cd mcp && python e2e_test.py`(真实 stdio MCP 客户端,~5 分钟)

## 标准工作流(AI 写程序)

1. 用户开着博途 → 直接操作;用户给 .ap21 路径 → `open_project`
2. 无 PLC 设备 → `add_cpu`(CPU 1511-1 PN)
3. 建变量表 `add_tags`(Bool/Int/Real,地址 I0.0/MW10 格式,V21 自动加 % 前缀)
4. 写程序:SCL 用 `import_scl`;LAD 用 `generate_lad_block`(配方见下)
5. `compile_project` 拿诊断 → 有错修 SCL/改 spec 重新导入 → 再编译(修复循环)
6. 写操作已自动保存;`save_project` 可显式保存
7. 收尾 `shutdown`(Attach 模式不关用户工程、不杀用户 TIA)

## LAD 配方(generate_lad_block 的 spec.networks[].recipe)

| recipe | args | 说明 |
|---|---|---|
| contact_coil | operand, output | 常开触点→线圈 |
| self_lock | start, stop, output | 启停自锁(并联自保) |
| blink | enable, output, ton, period | TON 自复位闪烁,period="T#500ms" |
| tof / tp | enable, output, inst, period | 断电延时 / 单脉冲 |
| counter | kind=CTU\|CTD, operand, reset, output, inst, pv="10", pvType="Int" | IEC 计数器 |
| pulse | kind=PBox\|NBox, operand, m, output | 上升/下降沿,m=Static 存储位 |
| set / reset | operand, output | 置位/复位线圈 |
| compare | kind=Eq\|Ne\|Gt\|Ge\|Lt\|Le, srcType, in1, in2, output | 比较 |
| arith | kind=Add\|Sub\|Mul\|Div\|Mod, srcType, in1, in2, output | 算术 |

接口:input/output/inout/static/temp;定时器/计数器实例放 FB.Static(TON_TIME/TOF_TIME/TP_TIME/CTU_INT 等);FC 不允许 Static 段。
spec 示例:samples\led_spec.json、samples 里已生成 lad\*.xml。

## HMI(经典 WinCC Comfort,已验证 2026-08-01)

- `add_hmi`:添加 KTP700 Basic PN(480x800 竖屏;AddHmiDevice 搜 6AV2123-2GB03-0AX0)
- `generate_hmi_screen(spec)`:生成并导入画面 XML。spec:{name, number, width=480, height=800, items:[{type: TextField|Button|Lamp|Rectangle, name, text, left, top, width, height}]}
- 画面 XML 规则(V21 实测):Screen 根元素 + Layers→ScreenItems;Text 用 `<body><p>` HTML 包装;Font=MultiLingualFont(宋体);**Button 不支持 Visible 属性**;画面尺寸必须等于设备分辨率(KTP700 Basic PN 竖屏=480x800);导入 API:`hmiTarget.ScreenFolder.Screens.Import`
- 获取 HmiTarget:设备 DeviceItems 走 GetService<SoftwareContainer>().Software as HmiTarget(同 PlcSoftware 模式)
- **HMI 连接:Openness 无创建 API(经典屏无 Connection 服务;本机 WinCCUnified.dll 无 Unified API)** —— 连接需用户在 TIA 网络视图手动拉一条(15 秒,一次性,工程内永久)。连接建好后:`add-hmi-conn`(XML 导入会失败,跳过)直接用 `gen-hmi-tags` 导入带 Connection 引用的变量表 + `gen-hmi` 画面带 tag/actionKind 绑定,链路全自动
- HMI 变量表 XML:gen-hmi-tags 命令(Hmi.Tag.TagTable,Connection+ControllerTag 符号绑定,Length 必须匹配类型字节数)
- 画面绑定:item 支持 tag(ProcessValue 动态绑定)和 actionKind/actionTag(Click 事件 SetBit/ResetBit)
- 参考:bulaofen0036-coder/TIA_Portal_Openness_MCP ClassicHmiScreenXmlBuilder.cs

## 读取工程

- `read_project(out_dir?)`:块列表 + 变量表全量 + 导出块内容(SCL→.scl 文本,LAD→.xml)
- `list_blocks` / `list_tag_tables` / `list_projects` / `use_project(name)`

## SimaticML 关键规则(写 LAD XML 时)

1. XML 必须 UTF-8 **BOM**(TIA 靠 BOM 识别编码)
2. 每个 Wire 引用要独立 Access 节点(同操作数多次出现各建一个)
3. 并联分支 = 一条 Wire 带多个 NameCon(Powerrail + 多 in);一个网络只能有一条 Powerrail
4. O 盒 Card 模板值 Type="**Cardinality**"(不是 Int);Add/Mul 必须显式 Card,Sub/Div/Mod 不能带
5. CTU 需 value_type 模板值;PV 数值常量用 Scope="LiteralConstant"+`<ConstantType>`,时间常量(T#)用 TypedConstant
6. UId 十进制、网络内 21 起;块级 MultilingualText ID 全局递增
7. SCL 中文注释已支持(BlockImporter 自动转 UTF-8 BOM 再导入,TIA 靠 BOM 识别编码);同名 SCL 重复导入会自动删旧源
8. **模板库**(gen-template 命令):samples/templates/*.scl,占位符 {{KEY}}。traffic-light(绿/黄/红时间)、motor-rev(正反转互锁)、counter(计数分拣 TARGET)。中文注释直接写

## Web 可视化面板(2026-08-01 已完成)

- 启动:`python D:\Workspace\TiaOpennessWorker\web_ui\server.py [端口=8000]`,浏览器打开 http://127.0.0.1:8000
- 零依赖(纯 Python 标准库);复用 mcp_server.py 的 TiaWorker 通信(自动 Attach 用户博途)
- 功能:工程(新建/打开/加CPU/HMI/保存/报告)、变量表表格化编辑、程序生成(模板/LAD 自锁闪烁/SCL 粘贴含中文注释)、编译诊断、日志窗口
- API:GET /api/status, POST /api/cmd {cmd,args}(通用命令透传), POST /api/save-scl, GET /api/log
- 演示工程:output/projects/WebDemo_Proj(含 FB_TrafficLight)

## 本机环境注意事项

- Git Bash 传参 `\\` 会折叠成 `\` → bash 命令一律用正斜杠路径(D:/...)
- Git Bash /tmp = `C:\Users\21238\AppData\Local\Temp`(Read 工具用 Windows 路径)
- python 重定向输出默认 GBK → 脚本里 `sys.stdout.reconfigure(encoding="utf-8")`
- 内存仅 15.7GB(chrome/ToDesk 常驻):TIA 启动崩溃会残留 ObjectFrame.FileStorage.Server 等进程,serve 已内置启动前清理 + 150s 看门狗 + 60s 重试
- 本机已装 SINAMICS Startdrive Advanced(运动控制可用,但工艺对象/轴必须用户在博途界面手动添加,Openness 无创建 API)
