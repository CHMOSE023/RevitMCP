using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RevitMCP.Protocol.Json
{
    /// <summary>
    /// 规范化序列化：同样的内容，无论 key 是什么顺序进来的，都得到同一个字符串。
    ///
    /// 用途只有一个——**给幂等键算哈希**。客户端重试时把同一份参数重新序列化一遍，
    /// key 的顺序很可能变（多数语言的字典不保证顺序），
    /// 拿普通的 ToJson 去比就会把"同一次请求"判成"不同的请求"，幂等直接失效。
    ///
    /// 不是给人读的，所以不缩进；也**不参与协议输出**，
    /// 协议那边仍然按插入顺序序列化（那样人读起来顺）。
    /// </summary>
    public static class JsonCanonical
    {
        /// <summary>递归按 key 排序、无空白的序列化。</summary>
        public static string Write(JsonValue value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value);
            return sb.ToString();
        }

        /// <summary>
        /// 把若干片段算成一个稳定的指纹（SHA-256，前 16 字节的 base64url）。
        ///
        /// 截断到 16 字节：这是幂等键的去重标识，不是安全凭证——
        /// 128 位对"同一个客户端在一天之内的几百次调用"绰绰有余，
        /// 而短一点的字符串在日志和报错里读得过来。
        /// </summary>
        public static string Fingerprint(params string[] parts)
        {
            var joined = string.Join("", parts.Select(p => p ?? string.Empty).ToArray());

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(joined));
                var head = new byte[16];
                Array.Copy(hash, head, head.Length);

                return Convert.ToBase64String(head)
                    .Replace('+', '-').Replace('/', '_').TrimEnd('=');
            }
        }

        private static void WriteValue(StringBuilder sb, JsonValue value)
        {
            if (value == null) { sb.Append("null"); return; }

            switch (value.Kind)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    return;

                case JsonKind.Bool:
                    sb.Append(value.AsBool ? "true" : "false");
                    return;

                case JsonKind.Number:
                    // 走 JsonValue 自己的数字格式化，保证与协议输出一致
                    sb.Append(value.ToJson());
                    return;

                case JsonKind.String:
                    sb.Append(value.ToJson());
                    return;

                case JsonKind.Array:
                    sb.Append('[');
                    var first = true;
                    foreach (var item in value.Items)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteValue(sb, item);
                    }
                    sb.Append(']');
                    return;

                case JsonKind.Object:
                    WriteObject(sb, value);
                    return;

                default:
                    sb.Append("null");
                    return;
            }
        }

        private static void WriteObject(StringBuilder sb, JsonValue value)
        {
            sb.Append('{');

            // Ordinal 排序：按码位排，不受系统区域设置影响。
            // 用 CurrentCulture 的话，同一份参数在不同机器上可能排出不同顺序
            var keys = value.Keys.ToList();
            keys.Sort(StringComparer.Ordinal);

            var first = true;
            foreach (var key in keys)
            {
                JsonValue member;
                if (!value.TryGet(key, out member)) continue;

                if (!first) sb.Append(',');
                first = false;

                sb.Append(JsonValue.String(key).ToJson());
                sb.Append(':');
                WriteValue(sb, member);
            }

            sb.Append('}');
        }
    }
}
