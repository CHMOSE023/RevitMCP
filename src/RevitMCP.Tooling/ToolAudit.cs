using System;
using System.Globalization;
using System.Text;
using RevitMCP.Protocol.Json;

namespace RevitMCP.Tooling
{
    /// <summary>一次 tools/call 的结局。</summary>
    public enum ToolOutcome
    {
        /// <summary>工具跑完并返回了结果。</summary>
        Succeeded,

        /// <summary>工具跑起来了但失败了——参数非法、构件不存在、事务回滚等。</summary>
        Failed,

        /// <summary>压根没跑：写保护、入参绑定失败、Revit 忙。模型没被碰过。</summary>
        Rejected
    }

    /// <summary>
    /// 一条审计记录。每次 tools/call 一条，无论成败。
    ///
    /// 审计存在的理由是事后回答"模型到底对这个项目做了什么"——
    /// 所以失败与被拒的调用同样要记：一串被写保护拒掉的写请求，
    /// 本身就是值得看见的信号。
    /// </summary>
    public sealed class ToolAuditEntry
    {
        public string ToolName { get; set; }

        /// <summary>入参摘要，已截断。完整参数可能有几百个 ID，照抄进日志没人看得下去。</summary>
        public string Arguments { get; set; }

        public bool ReadOnly { get; set; }

        public ToolOutcome Outcome { get; set; }

        /// <summary>领域错误码，成功时为 null。</summary>
        public string ErrorCode { get; set; }

        /// <summary>影响的构件数；工具没报告时为 -1。见 <see cref="IReportsAffectedElements"/>。</summary>
        public int AffectedElements { get; set; } = -1;

        public int WarningCount { get; set; }

        public long DurationMs { get; set; }

        /// <summary>拼成一行。字段顺序固定，便于事后 grep 与肉眼扫读。</summary>
        public override string ToString()
        {
            var text = new StringBuilder();
            text.Append(ToolName)
                .Append(ReadOnly ? " [只读] " : " [写] ")
                .Append(Describe(Outcome));

            if (!string.IsNullOrEmpty(ErrorCode)) text.Append('/').Append(ErrorCode);

            text.Append(" · ").Append(DurationMs.ToString(CultureInfo.InvariantCulture)).Append("ms");

            if (AffectedElements >= 0)
                text.Append(" · 影响 ").Append(AffectedElements.ToString(CultureInfo.InvariantCulture)).Append(" 个构件");

            if (WarningCount > 0)
                text.Append(" · ").Append(WarningCount.ToString(CultureInfo.InvariantCulture)).Append(" 条警告");

            if (!string.IsNullOrEmpty(Arguments)) text.Append(" · ").Append(Arguments);

            return text.ToString();
        }

        private static string Describe(ToolOutcome outcome)
        {
            switch (outcome)
            {
                case ToolOutcome.Succeeded: return "成功";
                case ToolOutcome.Failed: return "失败";
                default: return "被拒";
            }
        }
    }

    /// <summary>
    /// 输出 DTO 实现它来告诉审计"这次动了几个构件"。
    ///
    /// 做成输出上的可选接口，而不是让工具往上下文里回填一个计数：
    /// 输出本来就知道这个数（<c>Changed</c>、<c>Total</c> 之类），
    /// 再要求工具多调一次 API 只会出现漏调而审计悄悄记 0 的情况。
    /// </summary>
    public interface IReportsAffectedElements
    {
        int AffectedElements { get; }
    }

    /// <summary>
    /// 把入参压成一行摘要。
    ///
    /// 审计要能回答"改的是哪些构件"，但 500 个 ID 原样写进日志等于没写。
    /// 折中：短值原样保留，长数组只记条数与头几项——足够事后对照，又不会把日志淹了。
    /// </summary>
    public static class ArgumentSummary
    {
        private const int MaxTotalLength = 220;
        private const int MaxStringLength = 48;
        private const int MaxArrayItemsShown = 3;

        public static string Of(JsonValue arguments)
        {
            if (arguments == null || !arguments.IsObject || arguments.Count == 0) return "(无参数)";

            var text = new StringBuilder();
            var first = true;

            foreach (var key in arguments.Keys)
            {
                if (!first) text.Append(", ");
                first = false;

                text.Append(key).Append('=').Append(Value(arguments[key]));

                if (text.Length > MaxTotalLength)
                {
                    text.Length = MaxTotalLength;
                    text.Append('…');
                    break;
                }
            }

            return text.ToString();
        }

        private static string Value(JsonValue value)
        {
            if (value == null || value.Kind == JsonKind.Null) return "null";

            switch (value.Kind)
            {
                case JsonKind.String:
                    return Quote(value.AsString);

                case JsonKind.Array:
                    return Array(value);

                case JsonKind.Object:
                    return "{" + value.Count.ToString(CultureInfo.InvariantCulture) + " 字段}";

                default:
                    return value.ToJson();
            }
        }

        private static string Array(JsonValue value)
        {
            var count = value.Count;
            if (count == 0) return "[]";

            var text = new StringBuilder("[");
            var shown = 0;

            foreach (var item in value.Items)
            {
                if (shown == MaxArrayItemsShown) break;
                if (shown > 0) text.Append(',');
                text.Append(Value(item));
                shown++;
            }

            if (count > shown)
                text.Append(",…共 ").Append(count.ToString(CultureInfo.InvariantCulture)).Append(" 项");

            return text.Append(']').ToString();
        }

        private static string Quote(string value)
        {
            if (value == null) return "null";

            var single = value.Replace("\r", " ").Replace("\n", " ");
            return single.Length <= MaxStringLength
                ? "\"" + single + "\""
                : "\"" + single.Substring(0, MaxStringLength) + "…\"";
        }
    }
}
