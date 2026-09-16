using System;
using System.Collections.Generic;
using RevitMCP.Protocol.Json;

namespace RevitMCP.Protocol.Mcp
{
    /// <summary>MCP 的两代协议形态。见 docs/architecture.md §3。</summary>
    public enum McpEra
    {
        /// <summary>2026-07-28 起：无状态，版本与能力随每个请求的 _meta 传递，没有 initialize 握手。</summary>
        Modern,

        /// <summary>2025-11-25 及更早：initialize 握手建立会话。</summary>
        Legacy
    }

    public static class McpProtocol
    {
        public const string Modern20260728 = "2026-07-28";
        public const string Legacy20251125 = "2025-11-25";
        public const string Legacy20250618 = "2025-06-18";
        public const string Legacy20250326 = "2025-03-26";

        /// <summary>对外声明支持的版本，新到旧。</summary>
        public static readonly string[] SupportedVersions =
        {
            Modern20260728, Legacy20251125, Legacy20250618, Legacy20250326
        };

        public static readonly string[] SupportedLegacyVersions =
        {
            Legacy20251125, Legacy20250618, Legacy20250326
        };

        /// <summary>握手未给出可用版本时的兜底。</summary>
        public const string PreferredLegacyVersion = Legacy20251125;

        // _meta 键名。规范要求带 io.modelcontextprotocol/ 前缀。
        public const string MetaProtocolVersion = "io.modelcontextprotocol/protocolVersion";
        public const string MetaClientInfo = "io.modelcontextprotocol/clientInfo";
        public const string MetaClientCapabilities = "io.modelcontextprotocol/clientCapabilities";
        public const string MetaServerInfo = "io.modelcontextprotocol/serverInfo";

        // HTTP 头（比较时必须不区分大小写）
        public const string HeaderProtocolVersion = "MCP-Protocol-Version";
        public const string HeaderMethod = "Mcp-Method";
        public const string HeaderName = "Mcp-Name";

        public static bool IsSupported(string version) =>
            Array.IndexOf(SupportedVersions, version) >= 0;

        public static bool IsLegacy(string version) =>
            Array.IndexOf(SupportedLegacyVersions, version) >= 0;

        public static JsonValue SupportedVersionsJson()
        {
            var arr = JsonValue.NewArray();
            foreach (var v in SupportedVersions) arr.Add(v);
            return arr;
        }

        /// <summary>
        /// 从 legacy 客户端请求的版本里挑一个双方都支持的。
        /// 客户端要的版本我们支持就照用，否则给出我们最新的 legacy 版本，
        /// 让客户端自己决定接受还是断开（这是 legacy 握手的既定做法）。
        /// </summary>
        public static string NegotiateLegacyVersion(string requested) =>
            !string.IsNullOrEmpty(requested) && IsLegacy(requested) ? requested : PreferredLegacyVersion;

        /// <summary>
        /// 规范中 Mcp-Name / Mcp-Param-* 头对无法用纯 ASCII 表示的值使用的哨兵编码：
        /// <c>=?base64?{值}?=</c>。比较头与消息体之前必须先解码。
        /// </summary>
        public static string DecodeHeaderValue(string value)
        {
            const string prefix = "=?base64?";
            const string suffix = "?=";

            if (value == null) return null;
            if (!value.StartsWith(prefix, StringComparison.Ordinal) ||
                !value.EndsWith(suffix, StringComparison.Ordinal) ||
                value.Length < prefix.Length + suffix.Length)
                return value;

            var encoded = value.Substring(prefix.Length, value.Length - prefix.Length - suffix.Length);
            try
            {
                return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
            catch (FormatException)
            {
                return value;   // 不是合法 base64，按字面值处理，交给后续的匹配校验去拒绝
            }
        }
    }

    /// <summary>一次请求解析出的上下文，贯穿整个分发过程。</summary>
    public sealed class McpRequestContext
    {
        public McpRequestContext(McpEra era, string protocolVersion)
        {
            Era = era;
            ProtocolVersion = protocolVersion;
        }

        public McpEra Era { get; }

        public string ProtocolVersion { get; }

        /// <summary>
        /// 2026-07-28 起，结果对象需带 resultType 字段。
        /// legacy 客户端不认识它，所以只在 modern 下添加。
        /// </summary>
        public JsonValue NewResult()
        {
            var result = JsonValue.NewObject();
            if (Era == McpEra.Modern) result.Set("resultType", "complete");
            return result;
        }
    }
}
