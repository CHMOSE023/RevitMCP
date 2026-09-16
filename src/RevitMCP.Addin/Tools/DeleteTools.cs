using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    public sealed class DeleteElementsInput
    {
        [McpParam("要删除的构件 ID 列表", Required = true)]
        public List<string> ElementIds { get; set; }

        [McpParam("删除必须显式确认。第一次调用不带它，工具会告诉你实际会删掉什么（含被连带删除的构件）；" +
                  "核对无误后带 true 重新调用")]
        public bool? Confirm { get; set; }
    }

    public sealed class DeletedElement
    {
        [McpParam("构件 ID")]
        public string Id { get; set; }

        [McpParam("构件名称")]
        public string Name { get; set; }

        [McpParam("所属类别")]
        public string Category { get; set; }
    }

    public sealed class DeleteElementsOutput : IReportsAffectedElements
    {
        [McpParam("实际删除的构件总数，含被连带删除的")]
        public int Deleted { get; set; }

        [McpParam("你点名要删的数量")]
        public int Requested { get; set; }

        [McpParam("被连带删除的构件数（如删墙时墙上的门窗）")]
        public int Cascaded { get; set; }

        [McpParam("被删除构件的摘要。删除后这些构件就查不到了，这是它们最后的记录")]
        public List<DeletedElement> Elements { get; set; } = new List<DeletedElement>();

        [McpParam("摘要是否因数量过多而被截断")]
        public bool Truncated { get; set; }

        int IReportsAffectedElements.AffectedElements => Deleted;
    }

    [McpTool("revit_delete_elements",
        Title = "删除构件",
        Description = "从模型中删除指定构件。删除会连带删除依附于它们的构件——" +
                      "删一面墙，墙上的门窗一起没。" +
                      "第一次调用不带 confirm，工具会先告诉你实际会删掉哪些（包括连带的），" +
                      "核对后带 confirm: true 再调用一次才真的执行。" +
                      "整批要么全删、要么都不删，在撤销栈里只占一步。",
        TimeoutSeconds = 120)]
    public sealed class DeleteElementsTool : RevitTool<DeleteElementsInput, DeleteElementsOutput>
    {
        /// <summary>摘要最多列这么多条。删 500 个构件时列全了对模型是灾难而不是帮助。</summary>
        private const int MaxSummaries = 50;

        public override DeleteElementsOutput Execute(
            DeleteElementsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);

            if (input.ElementIds == null || input.ElementIds.Count == 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter, "elementIds 不能为空。");

            // 按 ID 的数值去重：同一个构件写成 "123" 和 "0123" 会被当成两个，
            // 让回执里的"点名 N 个"对不上账
            var requested = new List<ElementId>();
            var named = new HashSet<long>();

            foreach (var rawId in input.ElementIds)
            {
                var element = RequireElement(document, rawId);
                if (named.Add(element.Id.GetValue())) requested.Add(element.Id);
            }

            // 先试删一次再回滚，拿到真实的影响面。
            // 连带删除是删除工具最容易伤人的地方，而 Revit 没有 dry-run——
            // SubTransaction 就是那个 dry-run
            var affected = Probe(document, requested, context);

            // 回滚之后查不到的，是瞬态元素，不算数。
            //
            // Revit 在子事务里"删除"过一批只在特定状态下存在的内部元素
            // （实测：删除处于选中状态的构件时就会出现这么一批），
            // 它们被 Document.Delete 如实报告，但回滚后不会重建，真删时也不会再出现。
            // 不滤掉，预览就会虚报影响面——说要删 8 个、实际删 4 个，
            // 而那多出来的 4 个连摘要都列不出来，只会让人以为有什么东西要被误删
            var cascaded = affected
                .Where(id => !named.Contains(id.GetValue()) && document.GetElement(id) != null)
                .ToList();
            var summaries = Summarize(document, requested, cascaded);

            // 删除不设独立的规模闸：它本来就每次都要 confirm，
            // 再叠一道阈值只会让超限时弹出的是"数量太多"而不是"具体会删掉什么"——
            // 后者才是用户和模型真正需要看到的东西。阈值只作为预览里的一句提醒
            if (input.Confirm != true)
                throw new ToolFailureException(McpDomainError.ConfirmationRequired,
                    Preview(requested.Count, cascaded, summaries, context.MaxElementsPerWrite));

            ICollection<ElementId> deleted;
            try
            {
                deleted = document.Delete(requested);
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "Revit 拒绝删除：" + ex.Message + "。一个都没删。" +
                    "构件可能被固定（Pin）、属于链接模型，或正被其他构件依赖。");
            }

            if (deleted == null || deleted.Count == 0)
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "Revit 没有删除任何构件，也没有报错。它们可能被固定（Pin）了。");

            if (cascaded.Count > 0)
                context.Warnings.Add(
                    "除点名的 " + requested.Count + " 个构件外，还连带删除了 " + cascaded.Count +
                    " 个依附于它们的构件。");

            // 预演说的和实际做的对不上，必须说出来。
            // 整个"先预览再确认"的设计建立在预演可信之上——
            // 一旦不可信，用户是基于一份错误的清单点的确认，这比没有预览更危险
            var predicted = requested.Count + cascaded.Count;
            if (deleted.Count != predicted)
                context.Warnings.Add(
                    "预览算出会删除 " + predicted + " 个构件，实际删除了 " + deleted.Count +
                    " 个，以实际为准。下面的构件摘要按预览采集，可能与实际删除的不完全对应。");

            return new DeleteElementsOutput
            {
                Deleted = deleted.Count,
                Requested = requested.Count,
                Cascaded = Math.Max(deleted.Count - requested.Count, 0),
                Elements = summaries.Take(MaxSummaries).ToList(),
                Truncated = summaries.Count > MaxSummaries
            };
        }

        /// <summary>
        /// 在子事务里真删一次，记下 Revit 报告的完整影响面，然后回滚。
        ///
        /// 子事务的回滚不会进撤销栈，对用户完全不可见；
        /// 这是唯一能在动手之前知道"到底会删掉什么"的办法——
        /// Revit 不提供任何预演接口，而依附关系（墙→门窗→标记）无法靠遍历可靠推断。
        /// </summary>
        private static List<ElementId> Probe(
            Document document, List<ElementId> requested, ToolExecutionContext<UIApplication> context)
        {
            using (var probe = new SubTransaction(document))
            {
                if (probe.Start() != TransactionStatus.Started)
                    throw new ToolFailureException(McpDomainError.TransactionFailed,
                        "无法开启子事务来预演删除，模型未被修改。");

                ICollection<ElementId> deleted;
                try
                {
                    deleted = document.Delete(requested);
                }
                catch (Exception ex)
                {
                    SafeRollBack(probe);
                    throw new ToolFailureException(McpDomainError.TransactionFailed,
                        "Revit 拒绝删除：" + ex.Message + "。一个都没删。" +
                        "构件可能被固定（Pin）、属于链接模型，或正被其他构件依赖。");
                }

                var affected = deleted == null
                    ? new List<ElementId>()
                    : deleted.ToList();

                // 回滚必须成功，否则后面的摘要会读到一个已经被删空的模型。
                // 失败就直接抛：管线会回滚外层事务，模型仍是安全的
                if (probe.RollBack() != TransactionStatus.RolledBack)
                    throw new ToolFailureException(McpDomainError.TransactionFailed,
                        "预演删除后无法回滚子事务，本次调用已放弃（外层事务会一并回滚，模型未被修改）。");

                if (affected.Count == 0)
                    throw new ToolFailureException(McpDomainError.TransactionFailed,
                        "这些构件删不掉：Revit 报告没有任何构件会被删除。" +
                        "它们可能被固定（Pin）了，或属于不可编辑的工作集。");

                context.CancellationToken.ThrowIfCancellationRequested();
                return affected;
            }
        }

        private static void SafeRollBack(SubTransaction probe)
        {
            try
            {
                if (probe.GetStatus() == TransactionStatus.Started) probe.RollBack();
            }
            catch
            {
                // Dispose 还会再兜一次；这里不能让它盖住真正有信息量的那个异常
            }
        }

        /// <summary>
        /// 采集摘要——必须在真正删除之前做，删完就查不到了。
        /// 点名的排在前面，连带的排在后面：模型最需要确认的是后者。
        /// </summary>
        private static List<DeletedElement> Summarize(
            Document document, List<ElementId> requested, List<ElementId> cascaded)
        {
            var summaries = new List<DeletedElement>(requested.Count + cascaded.Count);

            foreach (var id in requested.Concat(cascaded))
            {
                var element = document.GetElement(id);
                if (element == null) continue;

                summaries.Add(new DeletedElement
                {
                    Id = id.ToProtocolString(),
                    Name = SafeName(element),
                    // 没有类别的元素（墙连接、草图这类内部元素）退到 .NET 类型名。
                    // "未知类别"等于什么都没说，而用户正要靠这一行判断该不该点确认
                    Category = element.Category?.Name ?? element.GetType().Name
                });
            }

            return summaries;
        }

        private static string Preview(
            int requestedCount, List<ElementId> cascaded, List<DeletedElement> summaries, int scaleLimit)
        {
            var total = requestedCount + cascaded.Count;
            var text = "本次将删除 " + total + " 个构件：你点名的 " + requestedCount + " 个";

            if (cascaded.Count > 0)
                text += "，外加被连带删除的 " + cascaded.Count + " 个（依附于它们的门窗、标记等）";

            text += "。\n";

            if (scaleLimit > 0 && total > scaleLimit)
                text += "注意：这超过了单次写入上限 " + scaleLimit +
                        " 个，是个值得停下来核对筛选条件的规模。\n";

            foreach (var summary in summaries.Take(MaxSummaries))
                text += "  · " + summary.Id + " " + (summary.Category ?? "未知类别") +
                        (string.IsNullOrEmpty(summary.Name) ? string.Empty : " 「" + summary.Name + "」") + "\n";

            if (summaries.Count > MaxSummaries)
                text += "  · …… 另有 " + (summaries.Count - MaxSummaries) + " 个未列出\n";

            return text + "确认无误后带 confirm: true 重新调用。模型目前未被修改。";
        }

        private static string SafeName(Element element)
        {
            try { return element.Name; }
            catch { return null; }
        }
    }
}
