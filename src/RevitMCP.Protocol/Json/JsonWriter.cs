using System.Globalization;
using System.Text;

namespace RevitMCP.Protocol.Json
{
    internal static class JsonWriter
    {
        public static string Write(JsonValue value, bool indented)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, indented, 0);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, JsonValue v, bool indented, int depth)
        {
            if (v == null) { sb.Append("null"); return; }

            switch (v.Kind)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    break;

                case JsonKind.Bool:
                    sb.Append(v.AsBool ? "true" : "false");
                    break;

                case JsonKind.Number:
                    sb.Append(v.NumberLiteral);
                    break;

                case JsonKind.String:
                    WriteString(sb, v.AsString);
                    break;

                case JsonKind.Array:
                    if (v.Count == 0) { sb.Append("[]"); break; }
                    sb.Append('[');
                    var firstItem = true;
                    foreach (var item in v.Items)
                    {
                        if (!firstItem) sb.Append(',');
                        firstItem = false;
                        NewLine(sb, indented, depth + 1);
                        WriteValue(sb, item, indented, depth + 1);
                    }
                    NewLine(sb, indented, depth);
                    sb.Append(']');
                    break;

                case JsonKind.Object:
                    if (v.Count == 0) { sb.Append("{}"); break; }
                    sb.Append('{');
                    var firstMember = true;
                    foreach (var key in v.Keys)
                    {
                        if (!firstMember) sb.Append(',');
                        firstMember = false;
                        NewLine(sb, indented, depth + 1);
                        WriteString(sb, key);
                        sb.Append(':');
                        if (indented) sb.Append(' ');
                        WriteValue(sb, v[key], indented, depth + 1);
                    }
                    NewLine(sb, indented, depth);
                    sb.Append('}');
                    break;
            }
        }

        private static void NewLine(StringBuilder sb, bool indented, int depth)
        {
            if (!indented) return;
            sb.Append('\n').Append(' ', depth * 2);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);   // 非 ASCII 直接输出，传输层统一按 UTF-8 编码
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
