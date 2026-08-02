---
name: tia-expert
description: 写博图(TIA Portal S7-1200/1500)程序的专家指南——SCL 语法硬规则、LAD 配方表、工艺模板库、编译错误修复对照。任何涉及生成/导入/修改博图程序块的任务必须遵循本指南。
---

# TIA Portal 博图专家

通过 MCP 工具(mcp_server.py)操作用户已打开的博途工程。**必须先 connect_project 绑定工程再操作**;所有写操作后 worker 已自动保存。

## 写程序的标准工作流

1. `connect_project` 绑定用户当前打开的工程(没绑定会报"请先打开博途")
2. `list_blocks` / `read_project` 了解现状,避免重复创建同名块
3. 能复用模板就复用(`import_scl` 导入 `samples/library/*.scl`,再按需求改接口/逻辑)
4. `import_scl_source(source, name)` 直接写 SCL 源码字符串(优先,不用先落盘)
5. `compile_project` 编译 → 按下方"错误对照表"修复 → 重新 import + compile,直到 0 错误

## SCL 语法硬规则(编译 0 错误的保证,全部实测)

1. **定时器调用必须每次带 PT**:`tonX(IN := FALSE, PT := T#1S);` 这种复位式调用也**必须带 PT**,否则报 "Parameter 'PT' has to be used"。定时器实例类型用 `TON_TIME` / `TOF_TIME` / `TP_TIME`(IEC 风格)。
2. **VAR_OUTPUT 参数禁止"先读后写"**:`i当前 := i当前 + 1` 写在输出参数上会报 "parameter might not be initialized" 警告。解法:静态缓存累加 + 最后输出映射:
   ```scl
   i缓存 := i缓存 + i增量;   // VAR 段缓存
   ...
   i输出 := i缓存;           // 末尾统一映射到 VAR_OUTPUT
   ```
3. **条件分支里"先读后写"同样会警告**(即使变量已初始化)→ 用"增量变量(1/0)+ 无条件累加"模式:
   ```scl
   IF b计数 AND NOT b上次 THEN i增量 := 1; ELSE i增量 := 0; END_IF;
   b上次 := b计数;
   i缓存 := i缓存 + i增量;
   ```
4. **FC 必须写 `: Void`**:`FUNCTION "FC_名称" : Void`,否则 "VERSION 无效"。
5. **FB 实例只能调用一次**:多个条件用单次调用 + 布尔表达式组合,不要两个 IF 里各调一次。
6. **OB 的 TEMP 区不能放 FB 实例**;OB 需要状态时用背景 DB(`CreateInstanceDB(name, false, 200, instanceOf)`),或改用 FC。
7. **FC 不允许 Static 段**(VAR 静态区是 FB 专属)。
8. **中文变量名/中文注释完全支持**(worker 自动转 UTF-8 BOM),命名风格:b布尔输入 / q布尔输出 / r实数 / i整数 / t时间 / ton定时器。
9. Int 计数类变量给初值 `:= 0`,消除"可能未初始化"警告(输出参数初值不够,见规则 2/3)。
10. 定时器/计数器实例放 VAR(静态)段;块内每个定时器只调用一次/每扫描一条路径一次。

## LAD 配方表(gen-lad / generate_lad_block 的 networks[].recipe)

| 配方 | 参数(必须精确) |
|---|---|
| contact_coil | {operand, output} |
| self_lock 启停自锁 | {start, stop, output} |
| blink TON闪烁 | {enable, ton(定时器实例名), period("T#500ms"), output} |
| set / reset | {operand, output} |
| compare | {kind: Eq/Ne/Gt/Ge/Lt/Le, srcType: Int/Real, in1, in2, output} |
| arith | {kind: Add/Sub/Mul/Div/Mod, srcType: Int/Real, in1, in2, output} |
| counter | {kind: CTU/CTD, operand, reset, pv("10"), pvType, output, inst} |
| timer ton/tof/tp | {kind, enable, inst, period("T#5s"), output} |
| pulse 边沿 | {kind: PBox/NBox, operand, m(存储位), output} |

spec 结构:`{type: "FB"|"FC", name, number, comment, interface: {input/output/static/temp: [{name, datatype}]}, networks: [{recipe, args}]}`。操作数用接口成员名;定时器/计数器实例放 static 段。**复杂逻辑(交通灯循环等)优先用 SCL/模板,别硬拼 LAD**。

## 工艺模板库(samples/library/,全部实测 0 错误 0 警告)

| 文件 | 用途 |
|---|---|
| FB_电机正反转.scl | 四重互锁(按钮/程序/反馈/时间)+ 切换延时 + 反馈校验故障 |
| FB_星三角启动.scl | 主/星/三角三接触器,星形延时切三角 |
| FB_气缸控制.scl | 伸出/缩回 + 到位检测 + 动作超时报警 |
| FB_传送带分拣.scl | 皮带自锁 + 入口计数 + 奇偶轮转两路分拣 + 计数 |
| FC_模拟量标定.scl | 4-20mA 原始值 → 工程值(线性标定 + 限幅 + 断线检测) |
| FB_PID温控.scl | 位置式 PID(100ms 周期)+ 手动/自动 + 抗积分饱和 |
| FB_批次计数.scl | 上升沿计数 + 目标批次 + 确认换批 |
| FB_报警处理.scl | 报警锁存 + 确认复位 + 蜂鸣器定时自停 |
| FB_滑动平均滤波.scl | 10 点环形缓存滑动平均 |
| FB_交通灯.scl | 绿→黄→红循环 + 故障黄闪模式 |

用法:import 后按用户需求改接口(变量名/时间参数),或调用 `FC_模拟量标定` 处理多路 AI。

## 编译错误对照表(实测)

| 报错 | 原因 | 修复 |
|---|---|---|
| Parameter 'PT' has to be used | 定时器调用漏 PT | 所有调用(含复位式)都带 PT |
| parameter might not be initialized | 输出参数先读后写 | 静态缓存 + 输出映射(规则 2/3) |
| Tag #xxx not defined | 引用了不存在的变量 | 检查接口成员名拼写 |
| Missing instance DB | LAD 里调用 FB 没建背景 DB | 用 create-inst-db 或改 SCL 单调用 |
| name is not unique / 同名块 | 重复导入 | 先 delete-block 旧块再导入 |
| 中文乱码 | 编码问题 | worker 已处理(UTF-8 BOM),SCL 文件别手动转码 |
| VERSION 无效 | FC 缺 : Void | FUNCTION "x" : Void |

## 常用命令速查(MCP 工具)

connect_project / disconnect_project / list_blocks / list_tag_tables / read_project(可带 out_dir 导出)
add_tags(table, tags) / import_scl(sclFile) / import_scl_source(source, name) / generate_scl_template(traffic-light|motor-rev|counter)
generate_lad_block(spec) / compile_project / save_project / create_project / add_cpu / add_hmi / save_archive
