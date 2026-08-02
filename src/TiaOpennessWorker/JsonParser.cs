using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TiaOpennessWorker
{
    /// <summary>
    /// 迷你 JSON 解析器(net48 无内置 System.Text.Json,与 JsonWriter 配套)。
    /// 支持 null / bool / number / string / array / object,解析为
    /// object 树:null、bool、long、double、List&lt;object&gt;、Dictionary&lt;string, object&gt;。
    /// </summary>
    public static class JsonParser
    {
        public static object Parse(string json)
        {
            var pos = 0;
            var value = ParseValue(json, ref pos);
            SkipWhitespace(json, ref pos);
            if (pos < json.Length)
                throw new FormatException($"JSON 末尾有多余内容: 位置 {pos}");
            return value;
        }

        /// <summary>便捷取字符串值(不存在/非字符串返回 null)。</summary>
        public static string GetString(Dictionary<string, object> obj, string key)
        {
            return obj.TryGetValue(key, out var v) ? v as string : null;
        }

        /// <summary>便捷取字符串数组(不存在/类型不符返回 null)。</summary>
        public static List<string> GetStringList(Dictionary<string, object> obj, string key)
        {
            if (!obj.TryGetValue(key, out var v) || !(v is List<object> list)) return null;
            var result = new List<string>();
            foreach (var item in list)
                result.Add(item?.ToString());
            return result;
        }

        private static object ParseValue(string json, ref int pos)
        {
            SkipWhitespace(json, ref pos);
            if (pos >= json.Length) throw new FormatException("JSON 意外结束");

            switch (json[pos])
            {
                case '{': return ParseObject(json, ref pos);
                case '[': return ParseArray(json, ref pos);
                case '"': return ParseString(json, ref pos);
                case 't': Expect(json, ref pos, "true"); return true;
                case 'f': Expect(json, ref pos, "false"); return false;
                case 'n': Expect(json, ref pos, "null"); return null;
                default:
                    if (json[pos] == '-' || (json[pos] >= '0' && json[pos] <= '9'))
                        return ParseNumber(json, ref pos);
                    throw new FormatException($"无法解析的字符 '{json[pos]}' 位置 {pos}");
            }
        }

        private static Dictionary<string, object> ParseObject(string json, ref int pos)
        {
            pos++; // '{'
            var obj = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            SkipWhitespace(json, ref pos);
            if (pos < json.Length && json[pos] == '}') { pos++; return obj; }

            while (true)
            {
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] != '"') throw new FormatException($"对象键必须是字符串,位置 {pos}");
                var key = ParseString(json, ref pos);
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] != ':') throw new FormatException($"缺少冒号,位置 {pos}");
                pos++;
                obj[key] = ParseValue(json, ref pos);

                SkipWhitespace(json, ref pos);
                if (pos >= json.Length) throw new FormatException("JSON 意外结束");
                if (json[pos] == ',') { pos++; continue; }
                if (json[pos] == '}') { pos++; return obj; }
                throw new FormatException($"对象内意外的字符 '{json[pos]}' 位置 {pos}");
            }
        }

        private static List<object> ParseArray(string json, ref int pos)
        {
            pos++; // '['
            var list = new List<object>();
            SkipWhitespace(json, ref pos);
            if (pos < json.Length && json[pos] == ']') { pos++; return list; }

            while (true)
            {
                list.Add(ParseValue(json, ref pos));
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length) throw new FormatException("JSON 意外结束");
                if (json[pos] == ',') { pos++; continue; }
                if (json[pos] == ']') { pos++; return list; }
                throw new FormatException($"数组内意外的字符 '{json[pos]}' 位置 {pos}");
            }
        }

        private static string ParseString(string json, ref int pos)
        {
            pos++; // '"'
            var sb = new StringBuilder();
            while (pos < json.Length)
            {
                var c = json[pos++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (pos >= json.Length) break;
                    var esc = json[pos++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (pos + 4 <= json.Length)
                            {
                                sb.Append((char)int.Parse(json.Substring(pos, 4), NumberStyles.HexNumber));
                                pos += 4;
                            }
                            break;
                        default: throw new FormatException($"未知转义 '\\{esc}'");
                    }
                }
                else sb.Append(c);
            }
            throw new FormatException("字符串未闭合");
        }

        private static object ParseNumber(string json, ref int pos)
        {
            var start = pos;
            if (pos < json.Length && json[pos] == '-') pos++;
            while (pos < json.Length && (char.IsDigit(json[pos]) || json[pos] == '.' || json[pos] == 'e' || json[pos] == 'E' || json[pos] == '+' || json[pos] == '-'))
                pos++;
            var text = json.Substring(start, pos - start);
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
            return double.Parse(text, CultureInfo.InvariantCulture);
        }

        private static void Expect(string json, ref int pos, string word)
        {
            if (pos + word.Length > json.Length || json.Substring(pos, word.Length) != word)
                throw new FormatException($"意外的标记,位置 {pos}");
            pos += word.Length;
        }

        private static void SkipWhitespace(string json, ref int pos)
        {
            while (pos < json.Length && char.IsWhiteSpace(json[pos])) pos++;
        }
    }
}
