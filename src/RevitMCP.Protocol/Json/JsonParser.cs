using System;
using System.Globalization;
using System.Text;

namespace RevitMCP.Protocol.Json
{
    /// <summary>
    /// 递归下降 JSON 解析器。面向网络输入，因此有显式深度上限：
    /// 深层嵌套的恶意报文会打爆调用栈，而 StackOverflowException 在 .NET 上无法捕获，
    /// 会直接带走整个 Revit 进程。
    /// </summary>
    internal static class JsonParser
    {
        private const int MaxDepth = 128;

        public static JsonValue Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var pos = 0;
            SkipWhitespace(text, ref pos);
            var value = ParseValue(text, ref pos, 0);
            SkipWhitespace(text, ref pos);
            if (pos != text.Length)
                throw Error(text, pos, "文档结束后仍有多余内容");
            return value;
        }

        private static JsonValue ParseValue(string s, ref int pos, int depth)
        {
            if (depth > MaxDepth) throw new JsonException("JSON 嵌套深度超过上限 " + MaxDepth + "。");
            if (pos >= s.Length) throw Error(s, pos, "内容意外结束");

            switch (s[pos])
            {
                case '{': return ParseObject(s, ref pos, depth);
                case '[': return ParseArray(s, ref pos, depth);
                case '"': return JsonValue.String(ParseString(s, ref pos));
                case 't': Expect(s, ref pos, "true"); return JsonValue.True;
                case 'f': Expect(s, ref pos, "false"); return JsonValue.False;
                case 'n': Expect(s, ref pos, "null"); return JsonValue.Null;
                default: return ParseNumber(s, ref pos);
            }
        }

        private static JsonValue ParseObject(string s, ref int pos, int depth)
        {
            var obj = JsonValue.NewObject();
            pos++; // '{'
            SkipWhitespace(s, ref pos);
            if (Peek(s, pos) == '}') { pos++; return obj; }

            while (true)
            {
                SkipWhitespace(s, ref pos);
                if (Peek(s, pos) != '"') throw Error(s, pos, "对象的键必须是字符串");
                var key = ParseString(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (Peek(s, pos) != ':') throw Error(s, pos, "键之后应为冒号");
                pos++;
                SkipWhitespace(s, ref pos);
                obj.Set(key, ParseValue(s, ref pos, depth + 1));
                SkipWhitespace(s, ref pos);

                var c = Peek(s, pos);
                if (c == ',') { pos++; continue; }
                if (c == '}') { pos++; return obj; }
                throw Error(s, pos, "对象中应为逗号或右花括号");
            }
        }

        private static JsonValue ParseArray(string s, ref int pos, int depth)
        {
            var arr = JsonValue.NewArray();
            pos++; // '['
            SkipWhitespace(s, ref pos);
            if (Peek(s, pos) == ']') { pos++; return arr; }

            while (true)
            {
                SkipWhitespace(s, ref pos);
                arr.Add(ParseValue(s, ref pos, depth + 1));
                SkipWhitespace(s, ref pos);

                var c = Peek(s, pos);
                if (c == ',') { pos++; continue; }
                if (c == ']') { pos++; return arr; }
                throw Error(s, pos, "数组中应为逗号或右方括号");
            }
        }

        private static string ParseString(string s, ref int pos)
        {
            pos++; // 开引号
            var sb = new StringBuilder();
            while (true)
            {
                if (pos >= s.Length) throw Error(s, pos, "字符串未闭合");
                var c = s[pos];

                if (c == '"') { pos++; return sb.ToString(); }

                if (c != '\\')
                {
                    if (c < 0x20) throw Error(s, pos, "字符串中出现未转义的控制字符");
                    sb.Append(c);
                    pos++;
                    continue;
                }

                pos++; // 反斜杠
                if (pos >= s.Length) throw Error(s, pos, "转义序列未结束");
                var e = s[pos++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (pos + 4 > s.Length) throw Error(s, pos, "Unicode 转义不完整");
                        var hex = s.Substring(pos, 4);
                        if (!ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                            throw Error(s, pos, "非法的 Unicode 转义：" + hex);
                        sb.Append((char)code);   // 代理对天然保留，无需特殊处理
                        pos += 4;
                        break;
                    default:
                        throw Error(s, pos - 1, "未知的转义字符：" + e);
                }
            }
        }

        private static JsonValue ParseNumber(string s, ref int pos)
        {
            var start = pos;
            if (Peek(s, pos) == '-') pos++;

            if (!IsDigit(Peek(s, pos))) throw Error(s, pos, "数值格式非法");

            // JSON 不允许前导零（01、-007），容忍它会让 "版本号被当成数字" 这类错误悄悄溜过去
            if (Peek(s, pos) == '0')
            {
                pos++;
                if (IsDigit(Peek(s, pos))) throw Error(s, pos, "数值不能有前导零");
            }
            else
            {
                while (IsDigit(Peek(s, pos))) pos++;
            }

            if (Peek(s, pos) == '.')
            {
                pos++;
                if (!IsDigit(Peek(s, pos))) throw Error(s, pos, "小数点后应有数字");
                while (IsDigit(Peek(s, pos))) pos++;
            }

            var exp = Peek(s, pos);
            if (exp == 'e' || exp == 'E')
            {
                pos++;
                var sign = Peek(s, pos);
                if (sign == '+' || sign == '-') pos++;
                if (!IsDigit(Peek(s, pos))) throw Error(s, pos, "指数部分应有数字");
                while (IsDigit(Peek(s, pos))) pos++;
            }

            return JsonValue.RawNumber(s.Substring(start, pos - start));
        }

        private static void Expect(string s, ref int pos, string literal)
        {
            if (pos + literal.Length > s.Length ||
                string.CompareOrdinal(s, pos, literal, 0, literal.Length) != 0)
                throw Error(s, pos, "期望字面量 " + literal);
            pos += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length)
            {
                var c = s[pos];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') pos++;
                else break;
            }
        }

        private static char Peek(string s, int pos) => pos < s.Length ? s[pos] : '\0';

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        private static JsonException Error(string s, int pos, string message)
        {
            // 报位置与上下文，否则调试协议问题会非常痛苦
            var from = Math.Max(0, pos - 20);
            var len = Math.Min(40, s.Length - from);
            var context = len > 0 ? s.Substring(from, len) : string.Empty;
            return new JsonException("JSON 解析失败（位置 " + pos + "）：" + message + "。上下文: …" + context + "…");
        }
    }
}
