using System;
using System.Collections.Generic;
using System.Text;

namespace TiaOpennessWorker
{
    /// <summary>
    /// 极简 JSON 写入器(net48 无内置 System.Text.Json,为避免引入外部依赖手写)。
    /// 支持对象属性、数组及数组内嵌套对象。
    /// </summary>
    public sealed class JsonWriter
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private readonly List<bool> _containerHasItem = new List<bool>();

        public JsonWriter Property(string name, string value)
        {
            WriteName(name);
            WriteString(value);
            return this;
        }

        public JsonWriter Property(string name, int value)
        {
            WriteName(name);
            _sb.Append(value);
            return this;
        }

        public JsonWriter Property(string name, bool value)
        {
            WriteName(name);
            _sb.Append(value ? "true" : "false");
            return this;
        }

        public JsonWriter BeginObject()
        {
            WriteSeparator();
            _sb.Append('{');
            _containerHasItem.Add(false);
            return this;
        }

        public JsonWriter EndObject()
        {
            _sb.Append('}');
            _containerHasItem.RemoveAt(_containerHasItem.Count - 1);
            return this;
        }

        public JsonWriter BeginArray(string name)
        {
            WriteName(name);
            _sb.Append('[');
            _containerHasItem.Add(false);
            return this;
        }

        public JsonWriter EndArray()
        {
            _sb.Append(']');
            _containerHasItem.RemoveAt(_containerHasItem.Count - 1);
            return this;
        }

        public override string ToString() => _sb.ToString();

        private void WriteName(string name)
        {
            WriteSeparator();
            _sb.Append('"').Append(Escape(name)).Append("\":");
        }

        private void WriteSeparator()
        {
            if (_containerHasItem.Count == 0) return; // 根级,无逗号
            var idx = _containerHasItem.Count - 1;
            if (_containerHasItem[idx]) _sb.Append(',');
            else _containerHasItem[idx] = true;
        }

        private void WriteString(string value)
        {
            _sb.Append('"').Append(Escape(value)).Append('"');
        }

        /// <summary>JSON 字符串转义(供外部复用)。</summary>
        public static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
