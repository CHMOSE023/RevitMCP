using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    public sealed class GetOperationStatusInput
    {
        [McpParam("操作 ID，来自写工具回执里的 operationId，或超时报错文本里的那一个")]
        public string OperationId { get; set; }

        [McpParam("幂等键。与 operationId 二选一，给了它就查这个键最近的一次调用")]
        public string RequestKey { get; set; }
    }

    public sealed class OperationStatusOutput
    {
        [McpParam("操作 ID")]
        public string OperationId { get; set; }

        [McpParam("状态：queued（排队中）、running（执行中）、committed（已提交，改动在模型里）、" +
                  "rolledBack（已回滚，模型原样）、failed（没进事务就失败）、" +
                  "cancelled（未执行）、unknown（**结果不确定**，见 nextStep）")]
        public string State { get; set; }

        [McpParam("哪个工具")]
        public string ToolName { get; set; }

        [McpParam("作用在哪个文档上")]
        public string DocumentId { get; set; }

        [McpParam("幂等键，没给过则为 null")]
        public string RequestKey { get; set; }

        [McpParam("入队时间，UTC")]
        public string QueuedAt { get; set; }

        [McpParam("开始执行时间，UTC。还没轮到它时为 null")]
        public string StartedAt { get; set; }

        [McpParam("结束时间，UTC。还没结束时为 null")]
        public string EndedAt { get; set; }

        [McpParam("失败时的领域错误码")]
        public string ErrorCode { get; set; }

        [McpParam("失败原因")]
        public string ErrorMessage { get; set; }

        [McpParam("这次操作涉及的构件 ID")]
        public List<string> AffectedElementIds { get; set; } = new List<string>();

        [McpParam("事务回滚不掉的副作用（落盘、导出写出的文件）。" +
                  "**即使状态是 rolledBack，这些也已经发生了**")]
        public List<string> SideEffects { get; set; } = new List<string>();

        [McpParam("**接下来该做什么**。这一句是本工具的重点——" +
                  "光知道状态没用，要的是「现在能不能重试」")]
        public string NextStep { get; set; }
    }

    /// <summary>
    /// 查一次调用到底走到哪一步了。
    ///
    /// 存在的理由是 `TIMEOUT`：调度器能区分"没开始"和"可能已执行"，
    /// 但此前没有任何接口能回答后者"那到底建成了没有"。
    /// 调用方于是只能猜，而两种猜法都是错的——直接重试会在模型里留下两套一模一样的构件
    /// （不报错、不产生警告、撤销栈里只是一步普通创建），当作失败继续则会让
    /// 后续构件挂在一批不存在的宿主上。
    ///
    /// **查不到 ≠ 没发生过。** 日志只在内存里、只留最近 200 条或 24 小时，
    /// 进程重启即清空。所以查不到时给的是 unknown + 查证指引，而不是"没有这个操作"。
    /// </summary>
    [McpTool("revit_get_operation_status",
        Title = "查操作状态",
        Description = "按 operationId 或 requestKey 查一次写调用到底提交了没有。" +
                      "**收到 TIMEOUT 后先调它，不要直接重试**——" +
                      "在 Revit 里重复创建最难发现：不报错、不产生警告。" +
                      "回执的 nextStep 说明接下来该做什么；查不到会返回 unknown，" +
                      "那不等于「没发生过」。",
        ReadOnly = true,
        TimeoutSeconds = 30)]
    public sealed class GetOperationStatusTool : RevitTool<GetOperationStatusInput, OperationStatusOutput>
    {
        /// <summary>由 ServerHost 在建管线时注入——工具本身拿不到管线实例。</summary>
        internal static OperationJournal Journal { get; set; }

        public override OperationStatusOutput Execute(
            GetOperationStatusInput input, ToolExecutionContext<UIApplication> context)
        {
            var hasId = !string.IsNullOrWhiteSpace(input.OperationId);
            var hasKey = !string.IsNullOrWhiteSpace(input.RequestKey);

            if (!hasId && !hasKey)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "operationId 与 requestKey 至少要给一个。" +
                    "operationId 在每个写工具的回执里，也在超时的报错文本里。");

            var journal = Journal;
            if (journal == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "这个版本没有接上操作日志，查不了状态。");

            var record = hasId ? journal.FindById(input.OperationId) : journal.FindByRequestKey(input.RequestKey);

            if (record == null) return NotFound(input, hasId);

            return new OperationStatusOutput
            {
                OperationId = record.OperationId,
                State = Name(record.State),
                ToolName = record.ToolName,
                DocumentId = record.DocumentKey,
                RequestKey = record.RequestKey,
                QueuedAt = Format(record.QueuedAtUtc),
                StartedAt = Format(record.StartedAtUtc),
                EndedAt = Format(record.EndedAtUtc),
                ErrorCode = record.ErrorCode,
                ErrorMessage = record.ErrorMessage,
                AffectedElementIds = record.AffectedElementIds,
                SideEffects = record.SideEffects,
                NextStep = NextStep(record.State)
            };
        }

        /// <summary>
        /// 查不到。**不能说"没有这个操作"**——那听起来像"它没发生过"，
        /// 而真相是"日志里没有了"：可能被淘汰，也可能 Revit 重启过。
        /// 这两种情况下模型里都可能躺着那次操作的成果。
        /// </summary>
        private static OperationStatusOutput NotFound(GetOperationStatusInput input, bool byId)
        {
            return new OperationStatusOutput
            {
                OperationId = byId ? input.OperationId : null,
                RequestKey = byId ? null : input.RequestKey,
                State = Name(OperationState.Unknown),
                NextStep =
                    "日志里没有这条记录——**这不等于它没发生过**。" +
                    "操作日志只保留最近 200 次调用或 24 小时，且 Revit 重启后清空。" +
                    "请直接去模型里核实：revit_get_model_changes（带上更早的 token），" +
                    "或按类别与位置 revit_query_elements 查那批构件。确认没有再重建。"
            };
        }

        private static string NextStep(OperationState state)
        {
            switch (state)
            {
                case OperationState.Committed:
                    return "已生效，改动在模型里。继续下一步；构件 ID 见 affectedElementIds。";

                case OperationState.RolledBack:
                    return "事务已回滚，模型原样。修正参数后可以重试" +
                           "（带同一个 requestKey 会被当成重放并返回这条失败记录，重来请换一个键）。" +
                           "注意 sideEffects 里的东西不会被回滚。";

                case OperationState.Failed:
                    return "没进到事务就失败了（参数、写保护或目标文档不符），模型一个字节都没动。" +
                           "把前置条件弄对再来。";

                case OperationState.Cancelled:
                    return "排队期间就被取消了，主线程没碰过它。可以直接重发。";

                case OperationState.Queued:
                    return "还在排队。等它结束再查，不要另发一次——那会变成两次真实的创建。";

                case OperationState.Running:
                    return "正在执行。等它结束再查，不要另发一次。";

                default:
                    return "**结果不确定**：它已经开始执行，但没能等到结果（超时或进程中断）。" +
                           "**不要直接重建。** 先用 revit_get_model_changes 或按类别、位置" +
                           "revit_query_elements 查一遍那批构件；确认没建成再重试。";
            }
        }

        private static string Name(OperationState state)
        {
            switch (state)
            {
                case OperationState.Queued: return "queued";
                case OperationState.Running: return "running";
                case OperationState.Committed: return "committed";
                case OperationState.RolledBack: return "rolledBack";
                case OperationState.Failed: return "failed";
                case OperationState.Cancelled: return "cancelled";
                default: return "unknown";
            }
        }

        private static string Format(DateTime? moment) =>
            moment.HasValue ? moment.Value.ToString("yyyy-MM-ddTHH:mm:ssZ") : null;
    }
}
