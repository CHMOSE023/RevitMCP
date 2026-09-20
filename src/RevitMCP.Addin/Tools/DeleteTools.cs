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
        [McpParam("要删除的构件 ID 列表。ElementId 与 uniqueId 两种写法都接受", Required = true)]
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
            var uiDocument = RequireUiDocument(context);
            var document = uiDocument.Document;

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

            // 活动视图删不掉，这是 Revit 的硬规矩。预检比事后诊断值钱：
            // 真让 Document.Delete 去撞，它只回一句"其中一个或多个不能删除"，连是哪个都不说
            var activeViewId = SafeActiveViewId(uiDocument);
            if (activeViewId != null)
            {
                foreach (var id in requested)
                {
                    if (id != activeViewId) continue;

                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "构件 " + id.ToProtocolString() + " 是当前活动视图，Revit 不允许删除。" +
                        "请先用 revit_activate_view 切到别的视图，再删它。一个都没删。");
                }
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

                    // Revit 的原话是"其中一个或多个不能删除"——**它不说是哪个**。
                    // 批量删 50 个构件时这句话等于没说，模型只能整批放弃。
                    // 逐个试一遍找出罪魁，这点开销只发生在已经失败的路径上
                    var culprits = FindUndeletable(document, requested);

                    throw new ToolFailureException(McpDomainError.TransactionFailed,
                        "Revit 拒绝删除：" + ex.Message + "。一个都没删。" + Describe(culprits, requested.Count));
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

        /// <summary>
        /// 逐个试删，找出到底哪些删不掉。只在整批失败之后才走这条路。
        /// 上限 50：再多就该让模型自己缩小范围了，而不是在这儿耗着。
        /// </summary>
        private static List<ElementId> FindUndeletable(Document document, List<ElementId> requested)
        {
            const int maxProbes = 50;
            var culprits = new List<ElementId>();

            foreach (var id in requested.Take(maxProbes))
            {
                using (var probe = new SubTransaction(document))
                {
                    if (probe.Start() != TransactionStatus.Started) break;

                    try { document.Delete(new List<ElementId> { id }); }
                    catch { culprits.Add(id); }
                    finally { SafeRollBack(probe); }
                }
            }

            return culprits;
        }

        private static string Describe(List<ElementId> culprits, int requestedCount)
        {
            if (culprits.Count == 0)
                return "构件可能被固定（Pin）、属于链接模型，或正被其他构件依赖。";

            var ids = string.Join("、", culprits.Take(10).Select(id => id.ToProtocolString()).ToArray());
            var more = culprits.Count > 10 ? "（共 " + culprits.Count + " 个）" : string.Empty;

            return "删不掉的是：" + ids + more +
                   "。常见原因：它是当前活动视图、被固定（Pin）了、属于链接模型，" +
                   "或者是模型里最后一个同类构件（Revit 不允许删光某些东西）。" +
                   "把它们从 elementIds 里去掉，其余 " + (requestedCount - culprits.Count) + " 个就能删了。";
        }

        private static ElementId SafeActiveViewId(UIDocument uiDocument)
        {
            try { return uiDocument.ActiveView?.Id; }
            catch { return null; }
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
