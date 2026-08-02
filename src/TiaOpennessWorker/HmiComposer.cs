using System;
using System.Collections.Generic;
using System.Text;

namespace TiaOpennessWorker
{
    /// <summary>HMI 画面元素(TextField / Button / Lamp / Rectangle)。</summary>
    public sealed class HmiItem
    {
        public string Type = "TextField";   // TextField | Button | Lamp | Rectangle
        public string Name;
        public string Text = "";
        public int Left, Top, Width = 120, Height = 40;
        public string Tag = "";             // HMI 变量名(ProcessValue 动态绑定)
        public string ActionKind = "";      // SetBit/ResetBit(Click 事件)
        public string ActionTag = "";       // 事件目标 HMI 变量
        public string ActionKindRelease = ""; // 释放事件动作(ResetBit,自复位按钮用)
    }

    /// <summary>HMI 变量表条目(连接 + PLC 符号地址)。</summary>
    public sealed class HmiTagDef
    {
        public string Name;
        public string DataType = "Bool";
        public string Connection = "";      // HMI 连接名(如 HMI_Connection_1)
        public string PlcTag = "";          // PLC 符号地址(变量表标签名或 DB_x.y)
    }

    /// <summary>HMI 画面规格(由 JSON spec 解析,供 HmiComposer 生成 SimaticML XML)。</summary>
    public sealed class HmiScreenSpec
    {
        public string Name = "Screen_1";
        public int Number = 1;
        public int Width = 640;
        public int Height = 480;
        public string BackColor = "242, 246, 250";
        public List<HmiItem> Items = new List<HmiItem>();
    }

    /// <summary>
    /// 经典 WinCC(Comfort)画面生成器:把简单规格编译为 SimaticML XML,
    /// 供 ScreenComposition.Import 导入(HMI 设备必须先存在)。
    /// 控件属性子集与 XML 结构参考社区在 V21 上验证过的写法
    /// (bulaofen0036-coder/TIA_Portal_Openness_MCP ClassicHmiScreenXmlBuilder)。
    /// 当前版本:TextField / Button / Lamp(矩形指示灯)/ Rectangle,静态无变量绑定。
    /// </summary>
    public static class HmiComposer
    {
        public static string BuildXml(HmiScreenSpec spec)
        {
            var sb = new StringBuilder(2048);
            var id = 1;

            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
            sb.Append("<Document>\n");
            sb.Append("  <Engineering version=\"V21\" />\n");
            sb.Append("  <DocumentInfo>\n");
            sb.Append("    <Created>2026-01-01T00:00:00.0000000Z</Created>\n");
            sb.Append("    <ExportSetting>WithDefaults</ExportSetting>\n");
            sb.Append("    <InstalledProducts />\n");
            sb.Append("  </DocumentInfo>\n");
            sb.Append("  <Hmi.Screen.Screen ID=\"" + (id++) + "\">\n");
            sb.Append("    <AttributeList>\n");
            sb.Append("      <ActiveLayer>0</ActiveLayer>\n");
            sb.Append($"      <BackColor>{spec.BackColor}</BackColor>\n");
            sb.Append("      <GridColor>0, 0, 0</GridColor>\n");
            sb.Append($"      <Height>{spec.Height}</Height>\n");
            sb.Append($"      <Name>{XmlEsc(spec.Name)}</Name>\n");
            sb.Append($"      <Number>{spec.Number}</Number>\n");
            sb.Append("      <Visible>true</Visible>\n");
            sb.Append($"      <Width>{spec.Width}</Width>\n");
            sb.Append("    </AttributeList>\n");
            sb.Append("    <ObjectList>\n");

            // HelpText(空)
            sb.Append($"      <MultilingualText ID=\"{id++}\" CompositionName=\"HelpText\">\n");
            sb.Append("        <ObjectList><MultilingualTextItem ID=\"" + (id++) + "\" CompositionName=\"Items\"><AttributeList><Culture>zh-CN</Culture><Text /></AttributeList></MultilingualTextItem></ObjectList>\n");
            sb.Append("      </MultilingualText>\n");

            // Layers → ScreenItems
            sb.Append($"      <Hmi.Screen.ScreenLayer ID=\"{id++}\" CompositionName=\"Layers\">\n");
            sb.Append("        <AttributeList>\n");
            sb.Append("          <Index>0</Index>\n");
            sb.Append("          <Name></Name>\n");
            sb.Append("          <VisibleES>true</VisibleES>\n");
            sb.Append("        </AttributeList>\n");
            sb.Append("        <ObjectList>\n");
            foreach (var item in spec.Items)
                AppendItem(sb, ref id, item);
            sb.Append("        </ObjectList>\n");
            sb.Append("      </Hmi.Screen.ScreenLayer>\n");

            sb.Append("    </ObjectList>\n");
            sb.Append("  </Hmi.Screen.Screen>\n");
            sb.Append("</Document>\n");
            return sb.ToString();
        }

        private static void AppendItem(StringBuilder sb, ref int id, HmiItem item)
        {
            string elementName;
            switch (item.Type)
            {
                case "Button": elementName = "Hmi.Screen.Button"; break;
                case "IOField": elementName = "Hmi.Screen.IOField"; break;
                case "Lamp": elementName = "Hmi.Screen.Rectangle"; break;
                default: elementName = "Hmi.Screen.TextField"; break;
            }
            var objName = string.IsNullOrEmpty(item.Name)
                ? elementName.Split('.').GetValue(elementName.Split('.').Length - 1) + "_" + id
                : item.Name;

            sb.Append($"          <{elementName} ID=\"{id++}\" CompositionName=\"ScreenItems\">\n");
            sb.Append("            <AttributeList>\n");
            sb.Append("              <BackColor>255, 255, 255</BackColor>\n");
            sb.Append("              <BorderColor>148, 163, 184</BorderColor>\n");
            sb.Append("              <BorderWidth>1</BorderWidth>\n");
            sb.Append($"              <Height>{item.Height}</Height>\n");
            sb.Append($"              <Left>{item.Left}</Left>\n");
            sb.Append($"              <ObjectName>{XmlEsc(objName.ToString())}</ObjectName>\n");
            sb.Append($"              <Top>{item.Top}</Top>\n");
            sb.Append($"              <Width>{item.Width}</Width>\n");
            if (elementName == "Hmi.Screen.Button")
            {
                sb.Append("              <Enabled>true</Enabled>\n");
                sb.Append("              <ForeColor>30, 41, 59</ForeColor>\n");
                sb.Append("              <TabIndex>0</TabIndex>\n");
            }
            else if (elementName == "Hmi.Screen.TextField")
            {
                sb.Append("              <ForeColor>30, 41, 59</ForeColor>\n");
            }
            sb.Append("            </AttributeList>\n");

            sb.Append("            <ObjectList>\n");
            if (elementName != "Hmi.Screen.Rectangle")
            {
                // Font(宋体)
                sb.Append($"              <Hmi.Globalization.MultiLingualFont ID=\"{id++}\" CompositionName=\"Font\">\n");
                sb.Append("                <ObjectList>\n");
                sb.Append($"                  <Hmi.Globalization.FontItem ID=\"{id++}\" CompositionName=\"Items\">\n");
                sb.Append("                    <AttributeList>\n");
                sb.Append("                      <Culture>zh-CN</Culture>\n");
                sb.Append("                      <FontFamily>宋体</FontFamily>\n");
                sb.Append("                      <FontSize>14</FontSize>\n");
                sb.Append("                      <FontStyle>Regular</FontStyle>\n");
                sb.Append("                    </AttributeList>\n");
                sb.Append("                  </Hmi.Globalization.FontItem>\n");
                sb.Append("                </ObjectList>\n");
                sb.Append("              </Hmi.Globalization.MultiLingualFont>\n");
            }
            if (elementName == "Hmi.Screen.Button")
            {
                AppendText(sb, ref id, "TextOff", item.Text);
                AppendText(sb, ref id, "TextOn", item.Text);
            }
            else if (elementName == "Hmi.Screen.TextField")
            {
                AppendText(sb, ref id, "Text", item.Text);
            }

            // ProcessValue 动态绑定(指示灯/IOField/按钮连 HMI 变量)
            if (!string.IsNullOrEmpty(item.Tag))
            {
                sb.Append($"              <Hmi.Screen.Property ID=\"{id++}\" CompositionName=\"Properties\">\n");
                sb.Append("                <AttributeList>\n");
                sb.Append("                  <Name>ProcessValue</Name>\n");
                sb.Append("                </AttributeList>\n");
                sb.Append("                <ObjectList>\n");
                sb.Append($"                  <Hmi.Dynamic.TagConnectionDynamic ID=\"{id++}\" CompositionName=\"Dynamic\">\n");
                sb.Append("                    <AttributeList>\n");
                sb.Append("                      <Indirect>false</Indirect>\n");
                sb.Append("                    </AttributeList>\n");
                sb.Append("                    <LinkList>\n");
                sb.Append($"                      <Tag TargetID=\"@OpenLink\"><Name>{XmlEsc(item.Tag)}</Name></Tag>\n");
                sb.Append("                    </LinkList>\n");
                sb.Append("                  </Hmi.Dynamic.TagConnectionDynamic>\n");
                sb.Append("                </ObjectList>\n");
                sb.Append("              </Hmi.Screen.Property>\n");
            }

            // 事件动作(Click 置位/复位;ActionKindRelease 生成 Release 事件实现自复位按钮)
            var events = new[] { new[] { "Click", item.ActionKind }, new[] { "Release", item.ActionKindRelease } };
            foreach (var ev in events)
            {
                var evName = ev[0];
                var evKind = ev[1];
                if (string.IsNullOrEmpty(evKind) || string.IsNullOrEmpty(item.ActionTag)) continue;
                sb.Append($"              <Hmi.Event.Event ID=\"{id++}\" CompositionName=\"Events\">\n");
                sb.Append("                <AttributeList>\n");
                sb.Append($"                  <Name>{evName}</Name>\n");
                sb.Append("                </AttributeList>\n");
                sb.Append("                <ObjectList>\n");
                sb.Append($"                  <Hmi.Event.FunctionListEventHandler ID=\"{id++}\" CompositionName=\"EventHandler\">\n");
                sb.Append("                    <ObjectList>\n");
                sb.Append($"                      <Hmi.Event.FunctionListEntry ID=\"{id++}\" CompositionName=\"FunctionListEntries\">\n");
                sb.Append("                        <AttributeList>\n");
                sb.Append($"                          <Name>{XmlEsc(evKind)}</Name>\n");
                sb.Append("                          <Type>SystemFunction</Type>\n");
                sb.Append("                        </AttributeList>\n");
                sb.Append("                        <ObjectList>\n");
                sb.Append($"                          <Hmi.Event.FunctionListEntryParameter ID=\"{id++}\" CompositionName=\"Parameters\">\n");
                sb.Append("                            <AttributeList>\n");
                sb.Append("                              <Name>Tag</Name>\n");
                sb.Append("                            </AttributeList>\n");
                sb.Append("                            <LinkList>\n");
                sb.Append($"                              <Value TargetID=\"@OpenLink\"><Name>{XmlEsc(item.ActionTag)}</Name></Value>\n");
                sb.Append("                            </LinkList>\n");
                sb.Append("                          </Hmi.Event.FunctionListEntryParameter>\n");
                sb.Append("                        </ObjectList>\n");
                sb.Append("                      </Hmi.Event.FunctionListEntry>\n");
                sb.Append("                    </ObjectList>\n");
                sb.Append("                  </Hmi.Event.FunctionListEventHandler>\n");
                sb.Append("                </ObjectList>\n");
                sb.Append("              </Hmi.Event.Event>\n");
            }

            sb.Append("            </ObjectList>\n");
            sb.Append($"          </{elementName}>\n");
        }

        /// <summary>
        /// 生成 HMI 变量表 XML(Hmi.Tag.TagTable),变量通过连接绑定 PLC 符号地址。
        /// 格式参考社区 V21 验证过的 ClassicHmiTagTableXmlBuilder。
        /// </summary>
        public static string BuildTagTableXml(string tableName, List<HmiTagDef> tags)
        {
            var sb = new StringBuilder(2048);
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
            sb.Append("<Document>\n");
            sb.Append("  <Engineering version=\"V21\" />\n");
            sb.Append("  <DocumentInfo>\n");
            sb.Append("    <Created>2026-01-01T00:00:00.0000000Z</Created>\n");
            sb.Append("    <ExportSetting>None</ExportSetting>\n");
            sb.Append("    <InstalledProducts />\n");
            sb.Append("  </DocumentInfo>\n");
            sb.Append("  <Hmi.Tag.TagTable ID=\"0\">\n");
            sb.Append("    <AttributeList>\n");
            sb.Append($"      <Name>{XmlEsc(tableName)}</Name>\n");
            sb.Append("    </AttributeList>\n");
            sb.Append("    <ObjectList>\n");
            var id = 1;
            foreach (var tag in tags)
            {
                // 最小结构(西门子官方参考):Name + AcquisitionCycle/Connection/ControllerTag 链接。
                // 多余属性(DataType/HmiDataType/AcquisitionTriggerMode 等)会导致导入报错甚至 TIA 崩溃。
                sb.Append($"      <Hmi.Tag.Tag ID=\"{id++}\" CompositionName=\"Tags\">\n");
                sb.Append("        <AttributeList>\n");
                sb.Append($"          <Name>{XmlEsc(tag.Name)}</Name>\n");
                sb.Append("        </AttributeList>\n");
                sb.Append("        <LinkList>\n");
                sb.Append("          <AcquisitionCycle TargetID=\"@OpenLink\"><Name>1 s</Name></AcquisitionCycle>\n");
                sb.Append($"          <Connection TargetID=\"@OpenLink\"><Name>{XmlEsc(tag.Connection)}</Name></Connection>\n");
                sb.Append($"          <ControllerTag TargetID=\"@OpenLink\"><Name>{XmlEsc(tag.PlcTag)}</Name></ControllerTag>\n");
                sb.Append("        </LinkList>\n");
                sb.Append("      </Hmi.Tag.Tag>\n");
            }
            sb.Append("    </ObjectList>\n");
            sb.Append("  </Hmi.Tag.TagTable>\n");
            sb.Append("</Document>\n");
            return sb.ToString();
        }

        /// <summary>生成 HMI 连接 XML(Hmi.Connection.Connection,默认 S7 连接)。</summary>
        public static string BuildConnectionXml(string connectionName)
        {
            var sb = new StringBuilder(1024);
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
            sb.Append("<Document>\n");
            sb.Append("  <Engineering version=\"V21\" />\n");
            sb.Append("  <DocumentInfo>\n");
            sb.Append("    <Created>2026-01-01T00:00:00.0000000Z</Created>\n");
            sb.Append("    <ExportSetting>None</ExportSetting>\n");
            sb.Append("    <InstalledProducts />\n");
            sb.Append("  </DocumentInfo>\n");
            sb.Append("  <Hmi.Connection.Connection ID=\"0\">\n");
            sb.Append("    <AttributeList>\n");
            sb.Append($"      <Name>{XmlEsc(connectionName)}</Name>\n");
            sb.Append("      <DriverType>HmiConnectionDriverType.S7Connection</DriverType>\n");
            sb.Append("    </AttributeList>\n");
            sb.Append("  </Hmi.Connection.Connection>\n");
            sb.Append("</Document>\n");
            return sb.ToString();
        }

        /// <summary>HMI 数据类型 → PLC 字节长度(V21 Classic HMI 要求 Length 与 PLC 类型一致)。</summary>
        private static string DefaultLength(string dataType)
        {
            switch ((dataType ?? "").Trim().ToUpperInvariant())
            {
                case "BOOL": return "1";
                case "BYTE": case "USINT": case "SINT": case "CHAR": return "1";
                case "WORD": case "INT": case "UINT": return "2";
                case "DWORD": case "DINT": case "UDINT": case "REAL": case "TIME": case "TOD": return "4";
                case "LWORD": case "LINT": case "ULINT": case "LREAL": case "LTIME": return "8";
                default: return "2";
            }
        }

        /// <summary>多语言文本:非空文字用 &lt;body&gt;&lt;p&gt; HTML 包装(V21 Classic HMI 要求)。</summary>
        private static void AppendText(StringBuilder sb, ref int id, string compositionName, string text)
        {
            sb.Append($"              <MultilingualText ID=\"{id++}\" CompositionName=\"{compositionName}\">\n");
            sb.Append("                <ObjectList>\n");
            sb.Append($"                  <MultilingualTextItem ID=\"{id++}\" CompositionName=\"Items\">\n");
            sb.Append("                    <AttributeList>\n");
            sb.Append("                      <Culture>zh-CN</Culture>\n");
            sb.Append(string.IsNullOrEmpty(text)
                ? "                      <Text />\n"
                : $"                      <Text><body><p>{XmlEsc(text)}</p></body></Text>\n");
            sb.Append("                    </AttributeList>\n");
            sb.Append("                  </MultilingualTextItem>\n");
            sb.Append("                </ObjectList>\n");
            sb.Append("              </MultilingualText>\n");
        }

        /// <summary>从 JSON spec(Dictionary 树)解析画面规格。</summary>
        public static HmiScreenSpec ParseSpec(Dictionary<string, object> spec)
        {
            var result = new HmiScreenSpec
            {
                Name = JsonParser.GetString(spec, "name") ?? "Screen_1",
            };
            if (spec.TryGetValue("number", out var num) && num is long l) result.Number = (int)l;
            if (spec.TryGetValue("width", out var w) && w is long wl) result.Width = (int)wl;
            if (spec.TryGetValue("height", out var h) && h is long hl) result.Height = (int)hl;

            if (spec.TryGetValue("items", out var raw) && raw is List<object> list)
            {
                foreach (var item in list)
                {
                    if (!(item is Dictionary<string, object> o)) continue;
                    result.Items.Add(new HmiItem
                    {
                        Type = JsonParser.GetString(o, "type") ?? "TextField",
                        Name = JsonParser.GetString(o, "name"),
                        Text = JsonParser.GetString(o, "text") ?? "",
                        Left = GetInt(o, "left"),
                        Top = GetInt(o, "top"),
                        Width = GetInt(o, "width") > 0 ? GetInt(o, "width") : 120,
                        Height = GetInt(o, "height") > 0 ? GetInt(o, "height") : 40,
                        Tag = JsonParser.GetString(o, "tag") ?? "",
                        ActionKind = JsonParser.GetString(o, "actionKind") ?? "",
                        ActionTag = JsonParser.GetString(o, "actionTag") ?? "",
                        ActionKindRelease = JsonParser.GetString(o, "actionKindRelease") ?? "",
                    });
                }
            }
            return result;
        }

        private static int GetInt(Dictionary<string, object> o, string key)
        {
            return o.TryGetValue(key, out var v) && v is long l ? (int)l : 0;
        }

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
