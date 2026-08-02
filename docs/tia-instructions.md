# TIA Portal S7-1200/1500 指令知识库

> 从西门子 SCL 编程参考手册/系统手册及工程资料吸收整理(2026-08),供 AI 写博图程序时按手册规范使用。
> 与本地实测规则交叉标注 ⚠ 的条目,必须遵守(踩过坑)。

## 1. 定时器指令(TP/TON/TOF/TONR)

IEC 定时器是功能块,调用需背景数据。SCL 中声明实例类型:`TON_TIME` / `TOF_TIME` / `TP_TIME` / `TONR_TIME`(S7-1500 可用 LTime 长计时)。

| 指令 | 行为 | 典型应用 |
|---|---|---|
| TP 脉冲 | IN 0→1 后 Q 立即置 1,持续整段 PT;短脉冲也输出完整宽度 | 去抖动、定宽脉冲 |
| TON 接通延时 | IN 保持 1 且计时达 PT 才 Q=1;IN 变 0 立即复位清零 | 延时启动、顺序启动 |
| TOF 断开延时 | IN=1 时 Q 恒 1;IN 1→0 后计时达 PT 才 Q=0;期间 IN 恢复则终止计时 | 延时停止(风机、照明) |
| TONR 保持延时 | 同 TON,但中断保留计时值,再接通继续累加 | 运行时长累计、带暂停延时 |

**SCL 调用**:`tonX(IN := ..., PT := ...);` 读 `tonX.Q` / `tonX.ET`。
⚠ **每次调用必须带 PT,连复位式调用都要**(`tonX(IN := FALSE, PT := T#1S);`),否则编译报 "Parameter 'PT' has to be used"。
⚠ 实例必须放 **VAR(静态)段**——放 VAR_TEMP 每周期重新初始化,永远计不了时。
⚠ 赋值方向:从定时器向输出拷贝(`qDone := tonX.Q;`),不能反向。
- S7-1200 只能用 IEC 定时器;S7-1500 另有 SIMATIC 定时器但数量受限。
- 振荡电路模式:两个 TON 的 Q 互控对方 IN,周期/占空比可调。

## 2. 计数器指令(CTU/CTD/CTUD)

SCL 实例类型:`CTU_INT` / `CTD_INT` / `CTUD_INT`(可换 DInt/LInt)。

| 指令 | 行为 |
|---|---|
| CTU | CU 上升沿 CV+1;R=1 清零并 Q=0;Q = (CV >= PV);CV 到类型上限不再加 |
| CTD | CD 上升沿 CV-1;LD=1 时 PV 装入 CV;Q = (CV <= 0) |
| CTUD | CU 加/CD 减,同时上升沿 CV 不变;QU=(CV>=PV), QD=(CV<=0);LD 装 PV,R 清零 |

⚠ 软件计数器频率受扫描周期限制,高频用 HSC(见第 9 节)。
⚠ SCL 中声明 `ctuX : CTU_INT;` 调用 `ctuX(CU := ..., R := ..., PV := ...);` 读 `ctuX.CV` / `ctuX.Q`。
⚠ 计数输出参数"先读后写"会触发未初始化警告 → 用静态缓存 + 输出映射(见模板库经验)。

## 3. 数学与转换指令

**运算符**:`+` `-` `*` `/` `MOD`(取余) `**`(幂)。除法除数不能为 0(上电默认 0,注意)。
- 参与运算的数据格式必须一致,否则编译报错。
- TIME 类型乘整数仍为 TIME;TIME 除除数只能为 INT。

**转换**(SCL 直接调用,如 `INT_TO_REAL(iRaw)`):
`INT_TO_REAL` `REAL_TO_INT` `DINT_TO_REAL` `ROUND`(取整) `CONVERT_REAL_TO_DINT` 等。
⚠ REAL→DWORD 是按位传送,取整用 ROUND/CONVERT。

**NORM_X + SCALE_X**(模拟量标准做法):
- `NORM_X`:OUT = (VALUE-MIN)/(MAX-MIN),把原始值(如 0~27648)映射到 0.0~1.0。MIN≥MAX 或 VALUE 为 NaN 时 ENO=0。
- `SCALE_X`:OUT = VALUE×(MAX-MIN)+MIN,把 0.0~1.0 映射到工程量。
- SCL 调用:`rNorm := NORM_X(MIN := 0, MAX := 27648, VALUE := iRaw); rEng := SCALE_X(MIN := 0.0, MAX := 100.0, VALUE := rNorm);`
- 等效手算:`工程值 = 工程下限 + (原始值-原始下限) × (工程上限-工程下限) / (原始上限-原始下限)`(模板 FC_模拟量标定即此式)。

**LIMIT 限值**:`LIMIT(MN := 下限, IN := 值, MX := 上限)` → 返回钳位值。

## 4. 程序控制语句

- **IF / ELSIF / ELSE / END_IF**:条件分支。
- **CASE 状态机**:`CASE iState OF 1: ...; 2: ...; ELSE ...; END_CASE;` 分支可为常数、范围(15 TO 20)、枚举组合(10、11、15 TO 20)。
- **FOR**:`FOR i := 0 TO 20 BY 1 DO ... END_FOR;`(BY 1 可省;运行变量放 Temp;循环中不能改结束值和增量;支持嵌套;CONTINUE 跳过本次,EXIT 退出循环)。
- **WHILE 先判断**:条件 True 才进入。
- **REPEAT 后判断**:至少执行一次,`UNTIL 条件 END_REPEAT`。
- **RETURN**:立即退出当前块(FC/FB);GOTO 慎用。
- ⚠ FOR 循环变量用 VAR_TEMP;循环体避免改结束值。

## 5. 移位与字逻辑

**移位**(BYTE~LWORD):`SHL`(逻辑左移,低位补 0)/ `SHR`(逻辑右移,高位补 0)/ `ROL`(循环左移)/ `ROR`(循环右移)/ `SAR`(算术右移,保持符号位)。
SCL 调用:`wOut := SHL(IN := wIn, N := 2);`

**字逻辑**:`WAND` `WOR` `WXOR`(按位与/或/异或),或直接运算符 `AND` `OR` `XOR` `NOT`(Bool 与字都可用)。
用途:AND 屏蔽位、OR 置位、XOR 取反/清零/比较。

## 6. 移动指令

- **MOVE**:SCL 直接用 `:=`。同类型传送;数组要求元素类型与个数一致(限值可不同);基本类型兼容可传(BYTE→WORD,高位丢失/补 0)。不传送 String/Variant 整体。
- **MOVE_BLK / UMOVE_BLK**:数组部分复制。`MOVE_BLK(IN := 数组[起], COUNT := n, OUT => 数组[起])`。IN/OUT 必须是数组元素;类型必须完全相同;UMOVE_BLK 不可被中断(最多 16kB);目的区不足时只传可接收部分且 ENO=0。
- **MOVE_BLK_VARIANT**:Variant 变长数组,支持 Struct/UDT 数组;COUNT≥1,SRC_INDEX/DEST_INDEX 从 0 起。
- **FILL_BLK**:单值写满数组(清零 DB 最快)。

## 7. 字符串指令

String 带长度前缀(`STRING[N]` 实际可用 N-1 字符,len 由系统维护,禁止手动赋值)。⚠ 拼接变量必须声明为 STRING,不能用 ARRAY。

| 指令 | 行为 |
|---|---|
| CONCAT(in1, in2) | 拼接两个;**不支持多参数**,多串链式调用;超长静默截断 |
| LEN(s) | 有效字符数(空串=0);MAX_STR_LEN 才是容量 |
| LEFT(s, L) / RIGHT(s, L) | 取左侧/右侧 L 字符;L>长度返整个串;L<=0 返空 |
| MID(s, pos, len) | 从 pos(从 1 起)截 len 字符 |
| FIND(s, 子串) | 返回位置,未找到=0 |
| DELETE(s, P, L) / INSERT | 删除/插入;P<=0 返空串,P>长度返原串 |
| I_String / R_String | 整数/实数 → 字符串(HMI 显示、报文拼接) |

⚠ 目标字符串预留足够长度,防截断;长文本用 STRG_CONCAT(一次拼多个)。

## 8. 通信指令

**TSEND_C / TRCV_C**(简单场景):CONT=1+REQ=0 建连;CONT=1+REQ=1 建连并发送;REQ 上升沿在已建连接上触发发送;CONT=0 终止连接。自动管理连接,配置简单。
**TCON + TSEND + TRCV + TDISCON**(持久双向):手动管理连接生命周期,支持单连接 ID 双向全双工,连接状态显式可见。

要点:
- TCON_Param 结构配置连接:interface(端口硬件标识符)、REMOTE(伙伴 IP/端口)、本地端口(被动 0=任意)。
- 连接 ID 全项目唯一;S7-1500 防火墙需放行 RFC 1006/TCP。
- S7-1500 互连建议 connectionType=1(ISO-on-TCP)。
- 错误码:8085 连接拒绝 / 8087 连接已存在 / 80B3 参数错误。

## 9. 高速计数器(HSC)

- S7-1200 最多 6 路(HSC1~6),**独立于扫描周期**;默认外设地址 HSC1=1000 起(每路 4 字节 DInt)。
- ⚠ 组态必须设置输入滤波时间(0.1~6.4μs 级),这是 HSC 不工作的最常见原因。
- 模式:单相/双相/A/B 相/A/B 四倍频;类型:计数/频率/周期。
- 三种编程方案:
  - 仅读计数/频率 → 直接读外设地址:`"当前值" := %ID1000;`(`:P` 立即读)
  - 改方向/预设/参考值 → `CTRL_HSC`(旧):`"CTRL_HSC_DB"(HSC := "Local-HSC_1", DIR := ..., NEW_DIR := 1, ...)`;STATUS 错误码 80A1~80D0。
  - 同步/捕捉/比较输出 → `CTRL_HSC_EXT`(V4.2+ 固件)。
- 当前计数值不在 CTRL_HSC 输出里,直接读组态分配的过程映像地址。

## 10. 运动控制(Motion Control,PLCopen 标准)

MC 指令均为功能块,作用于**轴工艺对象**(TO_Axis/TO_SpeedAxis/TO_PositioningAxis)。前提与铁律:
- ⚠ **必须先 MC_Power 使能轴**(Enable=TRUE 且 Status=TRUE)才能执行任何运动作业。
- ⚠ 指令必须在 OB 中**循环调用**,状态通过输出参数更新。
- ⚠ 输入参数在 **Execute 上升沿锁存传送**(除 MC_Power 的 StopMode、MC_MoveJog 的 Velocity 随时生效)。
- 上电/CPU STOP 后运动作业全部中止,必须重新使能。

| 指令 | 功能 | 关键参数 |
|---|---|---|
| MC_Power | 轴使能(最先调用) | Enable, EnablePositive/Negative, bRegulatorOn → Status/Busy/ErrorID |
| MC_Home | 回零(7 种模式) | Execute, Position, Mode → Done |
| MC_MoveAbsolute | 绝对定位 | Execute, Position, Velocity, Acc/Dec, Jerk(0=无S曲线), BufferMode |
| MC_MoveRelative | 相对定位(可负) | Execute, Distance, Velocity, Acc/Dec |
| MC_MoveVelocity | 定速连续运动 | Execute, Velocity → InVelocity |
| MC_MoveJog | 点动 | JogForward/JogBackward, Velocity |
| MC_Halt | 减速停止(保持使能) | Execute, Deceleration |
| MC_Stop | 急停(立即或减速) | Execute, StopMode |
| MC_Reset | 错误复位 | Execute → 成功后 StatusBits.Error=FALSE |

**回零 7 种模式(Mode)**:0=直接设当前值为 Position(绝对式调零) / 1=主动寻零脉冲 / 2=被动寻零 / 3=限位+零脉冲(常用) / 4=参考点开关 / 5=绝对值编码器直接读 / 6=探针。
⚠ **回零完成前不能启动绝对定位**(需 StatusBits.HomingDone=TRUE)。

**PLCopen 状态机**:Disabled →(MC_Power)→ Standstill →(MC_Home)→ Homing →(定位/定速)→ Discrete/Continuous Motion →(MC_Halt/Stop)→ Stopping → Standstill;任何错误 → ErrorStop →(MC_Reset)→ Standstill。
- Done 至少保持一个周期(Execute 保持则锁存);新命令会中止旧命令并输出 CommandAborted=TRUE(覆盖/超驰)。
- 逐步命令:等前一个 Done/StatusBits 再发下一个,否则报轴错误。
- 常见错误码:16#80A1 驱动未就绪 / 16#80A3 通信故障。

**标准流程**:MC_Power 使能 → MC_Reset 清错 → MC_Home 回零(等 Done)→ 按工艺依次 MC_MoveAbsolute/MoveRelative/MoveVelocity(每步等 Done)→ MC_Halt 停 → 断使能。

## 11. 同步控制(仅 S7-1500T,同步轴 TO_SynchronousAxis)

同步轴可建立两种主从关系:**线性关系**(齿轮比)与**函数关系**(凸轮表)。同步轴未同步时可当定位轴用。

| 指令 | 功能 | 关键参数 |
|---|---|---|
| MC_GearIn | 相对齿轮同步 | RatioNumerator(从)/RatioDenominator(主), Acc/Dec → InGear |
| MC_GearInPos | 绝对齿轮同步(仅1500T) | MasterSyncPosition/SlaveSyncPosition → InSync |
| MC_CamIn | 电子凸轮同步(仅1500T) | CamTableID, MasterOffset/SlaveOffset, Scaling, StartMode → InCam |
| MC_PhasingAbsolute/Relative | 同步中动态移相(仅1500T) | PhaseShift, Acc/Dec → Done |
| MC_GearOut / MC_CamOut | 解除同步 | Slave, Execute → Done |

**规则**:
- 主轴必须先处于运动状态(Continuous/Discrete Motion),从轴 Standstill 且已使能。
- 齿轮比注意从轴不超速;3:2 → RatioNumerator:=3.0, RatioDenominator:=2.0。
- 同步建立方式:提前同步(动态参数)/ 随后同步(主值距离)。
- MC_Phasing 必须在 GearIn/CamIn 激活后调用,用于套位调整等。
- 同步失败查 ErrorID(主轴丢失/从轴超限)→ MC_Reset 恢复。
- **CIMC 模板衔接**:`samples/FB_MotorCtrl.scl` 是上述指令的软件模拟(状态机 + 位置积分模拟),接真实轴时把对应状态替换为 MC_Power/MC_Home/MC_MoveVelocity/MC_MoveAbsolute/MC_GearIn 调用;S7-1500T 之外的 CPU 只能软件同步(手写比例跟随逻辑)。

## 12. 与实测经验的交叉规则(写程序必查)

1. ⚠ 定时器每次调用带 PT(含复位式)。
2. ⚠ 输出参数禁"先读后写" → 静态缓存 + 输出映射;条件分支先读后写同样警告 → 增量变量 + 无条件累加。
3. ⚠ FC 头写 `: Void`;FB 实例单次调用;OB 不放 FB 实例(用背景 DB)。
4. ⚠ FC 无 Static 段;定时器/计数器实例必须放 FB 的 VAR 段。
5. 中文变量/注释直接可用(worker 自动 UTF-8 BOM)。
6. 数组下标从 0 起(FOR 循环习惯);字符串索引从 1 起(MID/FIND)。
7. 除法/取余前检查除数;TIME 运算注意类型。
8. 优先用模板库(samples/library/)再按需改造,复杂工艺(交通灯等)别硬拼 LAD。
