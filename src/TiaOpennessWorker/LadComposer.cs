using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TiaOpennessWorker
{
    /// <summary>接口段的一个成员(输入/输出/静态/临时等)。</summary>
    public sealed class LadMember
    {
        public string Name;
        public string Datatype;
    }

    /// <summary>一个梯形图网络:配方(recipe)+ 参数。</summary>
    public sealed class LadNetwork
    {
        public string Title = "";
        public string Comment = "";
        public string Recipe;                    // contact_coil / self_lock / blink / set / reset / compare
        public Dictionary<string, string> Args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>LAD 块规格(由 JSON spec 解析而来,供 LadComposer 生成 SimaticML XML)。</summary>
    public sealed class LadSpec
    {
        public string Type = "FB";               // FB | FC
        public string Name;
        public int Number = 20001;
        public string Comment = "";
        public Dictionary<string, List<LadMember>> Interface = new Dictionary<string, List<LadMember>>(StringComparer.OrdinalIgnoreCase);
        public List<LadNetwork> Networks = new List<LadNetwork>();
    }

    /// <summary>
    /// LAD 梯形图生成器:把简单的配方说明(LadSpec)编译为 SimaticML XML,
    /// 供 LadBlockImporter 导入 TIA Portal。格式基于已验证的 V21 模板
    /// (SW.Blocks.FB/FC + FlgNet v5 Parts/Wires)。
    ///
    /// 配方:
    ///   contact_coil: [operand]--(output)                     单触点输出
    ///   self_lock:    [start]--[/stop]--(output),输出自锁并联 启停自锁
    ///   blink:        [enable]--[/output]--TON(PT=period)Q--(output) 自复位闪烁
    ///   set/reset:    [op]--(S output) / (R output)           置位/复位线圈
    ///   compare:      比较盒(Eq/Ne/Gt/Ge/Lt/Le in1, in2) --(output)
    ///
    /// 操作数解析:接口成员名 → LocalVariable;T#/数字/TRUE/FALSE → TypedConstant;
    /// 其余 → GlobalVariable(绝对地址或变量表标签)。
    /// </summary>
    public static class LadComposer
    {
        private const string FlgNetNs = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5";
        private const string IfaceNs = "http://www.siemens.com/automation/Openness/SW/Interface/v5";

        private static readonly string[] TimerTypes = { "TON_TIME", "TOF_TIME", "TP_TIME" };

        // IEC 计数器实例类型(S7-1500)
        private static readonly string[] CounterTypes =
        {
            "CTU_INT", "CTU_DINT", "CTU_UDINT", "CTU_SINT", "CTU_USINT",
            "CTD_INT", "CTD_DINT", "CTD_UDINT", "CTD_SINT", "CTD_USINT",
            "CTUD_INT", "CTUD_DINT", "CTUD_UDINT", "CTUD_SINT", "CTUD_USINT",
        };

        public static string BuildXml(LadSpec spec)
        {
            var sb = new StringBuilder(4096);
            var textId = 1; // MultilingualText 文档级 ID,全局递增(网络内 UId 可复用 21+)

            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
            sb.Append("<Document>\n");
            sb.Append("  <Engineering version=\"V21\" />\n");
            sb.Append("  <DocumentInfo>\n");
            sb.Append("    <Created>2026-01-01T00:00:00.0000000Z</Created>\n");
            sb.Append("    <ExportSetting>None</ExportSetting>\n");
            sb.Append("    <InstalledProducts>\n");
            sb.Append("      <Product><DisplayName>Totally Integrated Automation Portal</DisplayName><DisplayVersion>V21</DisplayVersion></Product>\n");
            sb.Append("      <Product><DisplayName>STEP 7 Professional</DisplayName><DisplayVersion>V21</DisplayVersion></Product>\n");
            sb.Append("    </InstalledProducts>\n");
            sb.Append("  </DocumentInfo>\n");

            var blockTag = spec.Type == "FC" ? "SW.Blocks.FC" : "SW.Blocks.FB";
            sb.Append($"  <{blockTag} ID=\"0\">\n");
            sb.Append("    <AttributeList>\n");
            sb.Append("      <AutoNumber>false</AutoNumber>\n");
            sb.Append("      <Interface>\n");
            sb.Append($"        <Sections xmlns=\"{IfaceNs}\">\n");
            AppendSection(sb, "Input", Get(spec.Interface, "input"));
            AppendSection(sb, "Output", Get(spec.Interface, "output"));
            AppendSection(sb, "InOut", Get(spec.Interface, "inout"));
            if (spec.Type != "FC") // FC 块不允许 Static 段(Openness 导入会拒绝)
                AppendSection(sb, "Static", Get(spec.Interface, "static"));
            AppendSection(sb, "Temp", Get(spec.Interface, "temp"));
            AppendSection(sb, "Constant", Get(spec.Interface, "constant"));
            if (spec.Type == "FC")
                sb.Append("          <Section Name=\"Return\"><Member Name=\"Ret_Val\" Datatype=\"Void\" /></Section>\n");
            sb.Append("        </Sections>\n");
            sb.Append("      </Interface>\n");
            sb.Append("      <MemoryLayout>Optimized</MemoryLayout>\n");
            sb.Append($"      <Name>{XmlEsc(spec.Name)}</Name>\n");
            sb.Append("      <Namespace />\n");
            sb.Append($"      <Number>{spec.Number}</Number>\n");
            sb.Append("      <ProgrammingLanguage>LAD</ProgrammingLanguage>\n");
            sb.Append("      <SetENOAutomatically>false</SetENOAutomatically>\n");
            sb.Append("    </AttributeList>\n");
            sb.Append("    <ObjectList>\n");

            // 块注释(参考模板位置:在 ObjectList 第一个)
            if (!string.IsNullOrEmpty(spec.Comment))
            {
                sb.Append($"      <MultilingualText ID=\"{textId++}\" CompositionName=\"Comment\">\n");
                sb.Append("        <ObjectList>\n");
                sb.Append($"          <MultilingualTextItem ID=\"{textId++}\" CompositionName=\"Items\">\n");
                sb.Append("            <AttributeList>\n");
                sb.Append("              <Culture>zh-CN</Culture>\n");
                sb.Append($"              <Text>{XmlEsc(spec.Comment)}</Text>\n");
                sb.Append("            </AttributeList>\n");
                sb.Append("          </MultilingualTextItem>\n");
                sb.Append("        </ObjectList>\n");
                sb.Append("      </MultilingualText>\n");
            }

            // 每个网络一个 CompileUnit
            foreach (var net in spec.Networks)
            {
                var flg = BuildNetworkFlgNet(net);
                sb.Append($"      <SW.Blocks.CompileUnit ID=\"{textId++}\" CompositionName=\"CompileUnits\">\n");
                sb.Append("        <AttributeList>\n");
                sb.Append("          <NetworkSource>\n");
                sb.Append(flg);
                sb.Append("          </NetworkSource>\n");
                sb.Append("          <ProgrammingLanguage>LAD</ProgrammingLanguage>\n");
                sb.Append("        </AttributeList>\n");
                sb.Append("        <ObjectList>\n");
                sb.Append($"          <MultilingualText ID=\"{textId++}\" CompositionName=\"Comment\"><ObjectList><MultilingualTextItem ID=\"{textId++}\" CompositionName=\"Items\"><AttributeList><Culture>zh-CN</Culture><Text>{XmlEsc(net.Comment)}</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>\n");
                sb.Append($"          <MultilingualText ID=\"{textId++}\" CompositionName=\"Title\"><ObjectList><MultilingualTextItem ID=\"{textId++}\" CompositionName=\"Items\"><AttributeList><Culture>zh-CN</Culture><Text>{XmlEsc(net.Title)}</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>\n");
                sb.Append("        </ObjectList>\n");
                sb.Append("      </SW.Blocks.CompileUnit>\n");
            }

            // 块标题
            sb.Append($"      <MultilingualText ID=\"{textId++}\" CompositionName=\"Title\">\n");
            sb.Append("        <ObjectList>\n");
            sb.Append($"          <MultilingualTextItem ID=\"{textId++}\" CompositionName=\"Items\">\n");
            sb.Append("            <AttributeList>\n");
            sb.Append("              <Culture>zh-CN</Culture>\n");
            sb.Append($"              <Text>{XmlEsc(spec.Name)}</Text>\n");
            sb.Append("            </AttributeList>\n");
            sb.Append("          </MultilingualTextItem>\n");
            sb.Append("        </ObjectList>\n");
            sb.Append("      </MultilingualText>\n");

            sb.Append("    </ObjectList>\n");
            sb.Append($"  </{blockTag}>\n");
            sb.Append("</Document>\n");
            return sb.ToString();
        }

        // ---------- 接口 ----------

        private static List<LadMember> Get(Dictionary<string, List<LadMember>> iface, string key)
        {
            return iface.TryGetValue(key, out var list) ? list : new List<LadMember>();
        }

        private static void AppendSection(StringBuilder sb, string sectionName, List<LadMember> members)
        {
            sb.Append($"          <Section Name=\"{sectionName}\">\n");
            foreach (var m in members)
            {
                if (Array.IndexOf(TimerTypes, m.Datatype) >= 0 || Array.IndexOf(CounterTypes, m.Datatype) >= 0)
                {
                    // IEC 定时器/计数器实例:参考模板用 TON_TIME + SetPoint 属性
                    sb.Append($"            <Member Name=\"{XmlEsc(m.Name)}\" Datatype=\"{m.Datatype}\" Version=\"1.0\">\n");
                    sb.Append("              <AttributeList><BooleanAttribute Name=\"SetPoint\" SystemDefined=\"true\">true</BooleanAttribute></AttributeList>\n");
                    sb.Append("            </Member>\n");
                }
                else
                {
                    sb.Append($"            <Member Name=\"{XmlEsc(m.Name)}\" Datatype=\"{m.Datatype}\" />\n");
                }
            }
            sb.Append("          </Section>\n");
        }

        // ---------- 网络 ----------

        /// <summary>按配方生成一个网络的 FlgNet XML。UId 网络内从 21 起递增。</summary>
        private static string BuildNetworkFlgNet(LadNetwork net)
        {
            var b = new StringBuilder();
            var uid = 21;
            var parts = new List<string>();
            var wires = new List<string>();

            // Access 先于 Parts 输出(参考模板顺序),但 UId 分配按调用顺序
            var accesses = new List<string>();

            int NextUid() => uid++;

            void AddPart(string xml) => parts.Add($"                {xml}\n");

            void AddWire(string xml) => wires.Add($"                <Wire UId=\"{NextUid()}\">{xml}</Wire>\n");

            // 操作数判型(参考模板):
            //   T#... 时间常量 → TypedConstant(无 ConstantType)
            //   数值/TRUE/FALSE + literalType → LiteralConstant + ConstantType
            //   其余(接口成员名) → LocalVariable
            // 每个引用(每条 Wire)必须有自己的 Access 节点——
            // 同一操作数出现多次(如自锁触点和线圈都是 bRun)也必须各自新建,
            // SimaticML 校验:一个 Access 只能被一条 Wire 引用。
            string A(string operand, string literalType = null)
            {
                var u = NextUid();
                string xml;
                if (operand.StartsWith("T#") || operand.StartsWith("t#"))
                    xml = $"                <Access Scope=\"TypedConstant\" UId=\"{u}\"><Constant><ConstantValue>{XmlEsc(operand)}</ConstantValue></Constant></Access>\n";
                else if (literalType != null && IsLiteral(operand))
                    xml = $"                <Access Scope=\"LiteralConstant\" UId=\"{u}\"><Constant><ConstantType>{literalType}</ConstantType><ConstantValue>{XmlEsc(operand)}</ConstantValue></Constant></Access>\n";
                else
                    xml = $"                <Access Scope=\"LocalVariable\" UId=\"{u}\"><Symbol><Component Name=\"{XmlEsc(operand)}\" /></Symbol></Access>\n";
                accesses.Add(xml);
                return $"<IdentCon UId=\"{u}\" />";
            }

            bool IsLiteral(string operand) =>
                operand == "TRUE" || operand == "FALSE" || operand == "true" || operand == "false" ||
                (double.TryParse(operand, NumberStyles.Float, CultureInfo.InvariantCulture, out _));

            switch (net.Recipe)
            {
                case "contact_coil":
                {
                    var op = Arg(net, "operand");
                    var output = Arg(net, "output");
                    var cu = NextUid(); AddPart($"<Part Name=\"Contact\" UId=\"{cu}\" />");
                    var cou = NextUid(); AddPart($"<Part Name=\"Coil\" UId=\"{cou}\" />");
                    AddWire($"<Powerrail /><NameCon UId=\"{cu}\" Name=\"in\" />");
                    AddWire($"{A(op)}<NameCon UId=\"{cu}\" Name=\"operand\" />");
                    AddWire($"<NameCon UId=\"{cu}\" Name=\"out\" /><NameCon UId=\"{cou}\" Name=\"in\" />");
                    AddWire($"{A(output)}<NameCon UId=\"{cou}\" Name=\"operand\" />");
                    break;
                }
                case "self_lock":
                {
                    // 经典自锁:rail--([start]∥[self])--O--[stop NC]--(output)
                    // 并联分支:一条 Wire 同时带 Powerrail 和两个 Contact 的 in(参考模板写法)
                    var start = Arg(net, "start");
                    var stop = Arg(net, "stop");
                    var output = Arg(net, "output");
                    var c1 = NextUid(); AddPart($"<Part Name=\"Contact\" UId=\"{c1}\" />");           // start
                    var c2 = NextUid(); AddPart($"<Part Name=\"Contact\" UId=\"{c2}\"><Negated Name=\"operand\" /></Part>"); // stop NC
                    var c3 = NextUid(); AddPart($"<Part Name=\"Contact\" UId=\"{c3}\" />");           // self
                    var o1 = NextUid(); AddPart($"<Part Name=\"O\" UId=\"{o1}\"><TemplateValue Name=\"Card\" Type=\"Cardinality\">2</TemplateValue></Part>");
                    var co = NextUid(); AddPart($"<Part Name=\"Coil\" UId=\"{co}\" />");
                    AddWire($"<Powerrail /><NameCon UId=\"{c1}\" Name=\"in\" /><NameCon UId=\"{c3}\" Name=\"in\" />");
                    AddWire($"{A(start)}<NameCon UId=\"{c1}\" Name=\"operand\" />");
                    AddWire($"<NameCon UId=\"{c1}\" Name=\"out\" /><NameCon UId=\"{o1}\" Name=\"in1\" />");
                    AddWire($"{A(output)}<NameCon UId=\"{c3}\" Name=\"operand\" />");
                    AddWire($"<NameCon UId=\"{c3}\" Name=\"out\" /><NameCon UId=\"{o1}\" Name=\"in2\" />");
                    AddWire($"<NameCon UId=\"{o1}\" Name=\"out\" /><NameCon UId=\"{c2}\" Name=\"in\" />");
                    AddWire($"{A(stop)}<NameCon UId=\"{c2}\" Name=\"operand\" />");
                    AddWire($"<NameCon UId=\"{c2}\" Name=\"out\" /><NameCon UId=\"{co}\" Name=\"in\" />");
                    AddWire($"{A(output)}<NameCon UId=\"{co}\" Name=\"operand\" />");
                    break;
                }
                case "blink":
                {
                    var enable = Arg(net, "enable");
                    var output = Arg(net, "output");
                    var ton = Arg(net, "ton");
                    var period = Arg(net, "period");
                    var c1 = NextUid(); AddPart($"<Part Name=\"Contact\" UId=\"{c1}\" />");
                    var c2 = NextUid(); AddPart($"<Part Name=\"Contact\" UId=\"{c2}\"><Negated Name=\"operand\" /></Part>");
                    var tonUid = NextUid();
                    var inst = NextUid(); AddPart($"<Part Name=\"TON\" Version=\"1.0\" UId=\"{tonUid}\"><Instance Scope=\"LocalVariable\" UId=\"{inst}\"><Component Name=\"{XmlEsc(ton)}\" /></Instance><TemplateValue Name=\"time_type\" Type=\"Type\">Time</TemplateValue></Part>");
                    var co = NextUid(); AddPart($"<Part Name=\"Coil\" UId=\"{co}\" />");
                    var open = NextUid();
                    AddWire($"<Powerrail /><NameCon UId=\"{c1}\" Name=\"in\" />");
                    AddWire($"{A(enable)}<NameCon UId=\"{c1}\" Name=\"operand\" />");
                    AddWire($"<NameCon UId=\"{c1}\" Name=\"out\" /><NameCon UId=\"{c2}\" Name=\"in\" />");
                    AddWire($"{A(output)}<NameCon UId=\"{c2}\" Name=\"operand\" />");
                    AddWire($"<NameCon UId=\"{c2}\" Name=\"out\" /><NameCon UId=\"{tonUid}\" Name=\"IN\" />");
                    AddWire($"{A(period)}<NameCon UId=\"{tonUid}\" Name=\"PT\" />");
                    AddWire($"<NameCon UId=\"{tonUid}\" Name=\"Q\" /><NameCon UId=\"{co}\" Name=\"in\" />");
                    AddWire($"{A(output)}<NameCon UId=\"{co}\" Name=\"operand\" />");
                    AddWire($"<NameCon UId=\"{tonUid}\" Name=\"ET\" /><OpenCon UId=\"{open}\" />");
                    break;
                }
                case "tof":
                case "tp":
                {
                    // TOF/TP 定时器:与 TON 同构(IN/PT/Q/ET + 实例),配方名 tof/tp
                    var enable = Arg(net, "enable");
                    var output = Arg(net, "output");
                    var inst = Arg(net, "inst");
                    var period = Arg(net, "period");
                    var partName = net.Recipe == "tof" ? "TOF" : "TP";
                    var c1 = NextUid(); AddPart($"<Part Name=\"Contact\" UId=\"{c1}\" />");
                    var boxUid = NextUid();
                    var instUid = NextUid(); AddPart($"<Part Name=\"{partName}\" Version=\"1.0\" UId=\"{boxUid}\"><Instance Scope=\"LocalVariable\" UId=\"{instUid}\"><Component Name=\"{XmlEsc(inst)}\" /></Instance><TemplateValue Name=\"time_type\" Type=\"Type\">Time</TemplateValue></Part>");
                    var co = NextUid(); AddPart($"<Part Name=\"Coil\" UId=\"{co}\" />");
                    var open = NextUid();
                    AddWire($"<Powerrail /><NameCon UId=\"{c1}\" Name=\"in\" />");
                    AddWire($"{A(enable)}<NameCon UId=\"{c1}\" Name=\"operand\" />");
                    AddWire($"<NameCon UId=\"{c1}\" Name=\"out\" /><NameCon UId=\"{boxUid}\" Name=\"IN\" />");
                    AddWire($"{A(period)}<NameCon UId=\"{boxUid}\" Name=\"PT\" />");
                    AddWire($"<NameCon UId=\"{boxUid}\" Name=\"Q\" /><NameCon UId=\"{co}\" Name=\"in\" />");
                    AddWire($"{A(output)}<NameCon UId=\"{co}\" Name=\"operand\" />");
                    AddWire($"<NameCon UId=\"{boxUid}\" Name=\"ET\" /><OpenCon UId=\"{open}\" />");
                    break;
                }
                case "counter":
                {
                    // IEC 计数器:CTU(CU/R/PV/Q/CV)或 CTD(CD/LD/PV/Q/CV);
                    // 计数输入与复位/装载是两个独立电源输入,各接一个触点
                    var kind = Arg(net, "kind") ?? "CTU";
                    var operand = Arg(net, "operand");
                    var reset = Arg(net, "reset");
                    var output = Arg(net, "output");
                    var inst = Arg(net, "inst");
                    var pv = Arg(net, "pv") ?? "10";
                    var mainPin = kind == "CTD" ? "CD" : "CU";
                    var resetPin = kind == "CTD" ? "LD" : "R";
                    var pvType = net.Args.TryGetValue("pvType", out var pt) && !string.IsNullOrEmpty(pt) ? pt : "Int";
                    var c1 = NextUid(); AddPart($"<Part Name=\"Contact\" UId=\"{c1}\" />");
                    var c2 = NextUid(); AddPart($"<Part Name=\"Contact\" UId=\"{c2}\" />");
                    var boxUid = NextUid();
                    var instUid = NextUid(); AddPart($"<Part Name=\"{kind}\" Version=\"1.0\" UId=\"{boxUid}\"><Instance Scope=\"LocalVariable\" UId=\"{instUid}\"><Component Name=\"{XmlEsc(inst)}\" /></Instance><TemplateValue Name=\"value_type\" Type=\"Type\">{pvType}</TemplateValue></Part>");
                    var co = NextUid(); AddPart($"<Part Name=\"Coil\" UId=\"{co}\" />");
                    var open = NextUid();
                    // LAD 一个网络只能有一条电源线:CU/CD 与 R/LD 触点并联共享
                    AddWire($"<Powerrail /><NameCon UId=\"{c1}\" Name=\"in\" /><NameCon UId=\"{c2}\" Name=\"in\" />");
                    AddWire($"{A(operand)}<NameCon UId=\"{c1}\" Name=\"operand\" />");
                    AddWire($"<NameCon UId=\"{c1}\" Name=\"out\" /><NameCon UId=\"{boxUid}\" Name=\"{mainPin}\" />");
                    AddWire($"{A(reset)}<NameCon UId=\"{c2}\" Name=\"operand\" />");
                    AddWire($"<NameCon UId=\"{c2}\" Name=\"out\" /><NameCon UId=\"{boxUid}\" Name=\"{resetPin}\" />");
                    AddWire($"{A(pv, pvType)}<NameCon UId=\"{boxUid}\" Name=\"PV\" />");
                    AddWire($"<NameCon UId=\"{boxUid}\" Name=\"Q\" /><NameCon UId=\"{co}\" Name=\"in\" />");
                    AddWire($"{A(output)}<NameCon UId=\"{co}\" Name=\"operand\" />");
                    AddWire($"<NameCon UId=\"{boxUid}\" Name=\"CV\" /><OpenCon UId=\"{open}\" />");
                    break;
                }
                case "pulse":
                {
                    // 上升/下降沿:PBox/NBox,in/out 电源流 + bit 存储位(需 Static 位)
                    var kind = Arg(net, "kind") ?? "PBox";
                    var operand = Arg(net, "operand");
                    var m = Arg(net, "m");
                    var output = Arg(net, "output");
                    var c1 = NextUid(); AddPart($"<Part Name=\"Contact\" UId=\"{c1}\" />");
                    var boxUid = NextUid(); AddPart($"<Part Name=\"{kind}\" UId=\"{boxUid}\" />");
                    var co = NextUid(); AddPart($"<Part Name=\"Coil\" UId=\"{co}\" />");
                    AddWire($"<Powerrail /><NameCon UId=\"{c1}\" Name=\"in\" />");
                    AddWire($"{A(operand)}<NameCon UId=\"{c1}\" Name=\"operand\" />");
                    AddWire($"<NameCon UId=\"{c1}\" Name=\"out\" /><NameCon UId=\"{boxUid}\" Name=\"in\" />");
                    AddWire($"{A(m)}<NameCon UId=\"{boxUid}\" Name=\"bit\" />");
                    AddWire($"<NameCon UId=\"{boxUid}\" Name=\"out\" /><NameCon UId=\"{co}\" Name=\"in\" />");
                    AddWire($"{A(output)}<NameCon UId=\"{co}\" Name=\"operand\" />");
                    break;
                }
                case "set":
                case "reset":
                {
                    var op = Arg(net, "operand");
                    var output = Arg(net, "output");
                    var c1 = NextUid(); AddPart($"<Part Name=\"Contact\" UId=\"{c1}\" />");
                    var coilPart = net.Recipe == "set" ? "SCoil" : "RCoil";
                    var co = NextUid(); AddPart($"<Part Name=\"{coilPart}\" UId=\"{co}\" />");
                    AddWire($"<Powerrail /><NameCon UId=\"{c1}\" Name=\"in\" />");
                    AddWire($"{A(op)}<NameCon UId=\"{c1}\" Name=\"operand\" />");
                    AddWire($"<NameCon UId=\"{c1}\" Name=\"out\" /><NameCon UId=\"{co}\" Name=\"in\" />");
                    AddWire($"{A(output)}<NameCon UId=\"{co}\" Name=\"operand\" />");
                    break;
                }
                case "compare":
                {
                    var kind = Arg(net, "kind") ?? "Eq";
                    var srcType = Arg(net, "srcType") ?? "Int";
                    var in1 = Arg(net, "in1");
                    var in2 = Arg(net, "in2");
                    var output = Arg(net, "output");
                    var box = NextUid(); AddPart($"<Part Name=\"{kind}\" UId=\"{box}\"><TemplateValue Name=\"SrcType\" Type=\"Type\">{srcType}</TemplateValue></Part>");
                    var co = NextUid(); AddPart($"<Part Name=\"Coil\" UId=\"{co}\" />");
                    AddWire($"<Powerrail /><NameCon UId=\"{box}\" Name=\"pre\" />");
                    AddWire($"{A(in1)}<NameCon UId=\"{box}\" Name=\"in1\" />");
                    AddWire($"{A(in2)}<NameCon UId=\"{box}\" Name=\"in2\" />");
                    AddWire($"<NameCon UId=\"{box}\" Name=\"out\" /><NameCon UId=\"{co}\" Name=\"in\" />");
                    AddWire($"{A(output)}<NameCon UId=\"{co}\" Name=\"operand\" />");
                    break;
                }
                case "arith":
                {
                    // 算术盒:Add/Sub/Mul/Div/Mod。参考模板:en 接电源、in1/in2 输入、
                    // out 直接连目标变量(数据线,不用线圈),DisabledENO=true(无后续网络)
                    var kind = Arg(net, "kind") ?? "Add";
                    var srcType = Arg(net, "srcType") ?? "Int";
                    var in1 = Arg(net, "in1");
                    var in2 = Arg(net, "in2");
                    var output = Arg(net, "output");
                    // Add/Mul 必须显式 Card(实测缺失报错);Sub/Div/Mod 参考模板不带 Card
                    var cardXml = (kind == "Add" || kind == "Mul")
                        ? "<TemplateValue Name=\"Card\" Type=\"Cardinality\">2</TemplateValue>" : "";
                    var box = NextUid(); AddPart($"<Part Name=\"{kind}\" UId=\"{box}\" DisabledENO=\"true\"><TemplateValue Name=\"SrcType\" Type=\"Type\">{srcType}</TemplateValue>{cardXml}</Part>");
                    AddWire($"<Powerrail /><NameCon UId=\"{box}\" Name=\"en\" />");
                    AddWire($"{A(in1)}<NameCon UId=\"{box}\" Name=\"in1\" />");
                    AddWire($"{A(in2)}<NameCon UId=\"{box}\" Name=\"in2\" />");
                    AddWire($"<NameCon UId=\"{box}\" Name=\"out\" />{A(output)}");
                    break;
                }
                default:
                    throw new InvalidOperationException($"未知 LAD 配方: {net.Recipe} (可用: contact_coil / self_lock / blink / tof / tp / counter / pulse / set / reset / compare / arith)");
            }

            // 组装:Accesses 在前,Part 在中,Wires 在后(参考模板顺序)
            b.Append("            <FlgNet xmlns=\"" + FlgNetNs + "\">\n");
            b.Append("              <Parts>\n");
            foreach (var a in accesses) b.Append(a);
            foreach (var p in parts) b.Append(p);
            b.Append("              </Parts>\n");
            b.Append("              <Wires>\n");
            foreach (var w in wires) b.Append(w);
            b.Append("              </Wires>\n");
            b.Append("            </FlgNet>\n");
            return b.ToString();
        }

        private static string Arg(LadNetwork net, string key)
        {
            if (!net.Args.TryGetValue(key, out var v) || string.IsNullOrEmpty(v))
                throw new InvalidOperationException($"配方 {net.Recipe} 缺少参数: {key}");
            return v;
        }

        /// <summary>
        /// 从 serve/CLI 的 JSON spec(Dictionary 树)解析 LadSpec。
        /// spec: {type, name, number, comment, interface:{input|output|inout|static|temp|constant:[{name,datatype}]}, networks:[{title,comment,recipe,args:{...}}]}
        /// </summary>
        public static LadSpec ParseSpec(Dictionary<string, object> spec)
        {
            var result = new LadSpec
            {
                Type = JsonParser.GetString(spec, "type") ?? "FB",
                Name = JsonParser.GetString(spec, "name") ?? throw new InvalidOperationException("缺少参数: spec.name"),
                Number = spec.TryGetValue("number", out var num) && num is long l ? (int)l : 20001,
                Comment = JsonParser.GetString(spec, "comment") ?? "",
            };

            if (spec.TryGetValue("interface", out var ifRaw) && ifRaw is Dictionary<string, object> iface)
            {
                foreach (var kv in iface)
                {
                    var members = new List<LadMember>();
                    if (kv.Value is List<object> list)
                    {
                        foreach (var item in list)
                        {
                            if (!(item is Dictionary<string, object> o)) continue;
                            members.Add(new LadMember
                            {
                                Name = JsonParser.GetString(o, "name"),
                                Datatype = JsonParser.GetString(o, "datatype") ?? "Bool",
                            });
                        }
                    }
                    result.Interface[kv.Key] = members;
                }
            }

            if (!spec.TryGetValue("networks", out var netRaw) || !(netRaw is List<object> netList) || netList.Count == 0)
                throw new InvalidOperationException("缺少参数: spec.networks(至少一个网络)");

            foreach (var item in netList)
            {
                if (!(item is Dictionary<string, object> o))
                    throw new InvalidOperationException("networks 元素必须是对象 {title, comment, recipe, args}");
                var net = new LadNetwork
                {
                    Title = JsonParser.GetString(o, "title") ?? "",
                    Comment = JsonParser.GetString(o, "comment") ?? "",
                    Recipe = JsonParser.GetString(o, "recipe") ?? throw new InvalidOperationException("网络缺少参数: recipe"),
                };
                if (o.TryGetValue("args", out var argsRaw) && argsRaw is Dictionary<string, object> args)
                {
                    foreach (var kv in args)
                        if (kv.Value is string sv) net.Args[kv.Key] = sv;
                }
                result.Networks.Add(net);
            }
            return result;
        }

        /// <summary>XML 文本/属性转义(&amp; &lt; &gt; &quot; &apos;)。</summary>
        private static string XmlEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
