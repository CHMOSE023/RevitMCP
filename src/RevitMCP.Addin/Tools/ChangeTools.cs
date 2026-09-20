using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Addin.Diagnostics;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    public sealed class GetModelChangesInput
    {
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档")]
        public string DocumentId { get; set; }

        [McpParam("上一次调用返回的 token。省略则只建立基线——" +
                  "返回一个 token 和空的改动列表，之后带着它回来问「从那以后改了什么」")]
        public string Since { get; set; }

        [McpParam("是否一并返回每个改动构件的名称与类别，默认 true。" +
                  "改动量很大时设 false 可以显著缩小返回值")]
        public bool? IncludeDetails { get; set; }

        [McpParam("最多返回多少个构件的明细，默认 500，上限 2000。" +
                  "ID 列表不受它限制，只影响明细")]
        public int? DetailLimit { get; set; }
    }

    public sealed class ChangedElement
    {
        [McpParam("构件 ID")]
        public string Id { get; set; }

        [McpParam("构件名。已删除的构件读不到，为 null")]
        public string Name { get; set; }

        [McpParam("所属类别。已删除的构件读不到，为 null")]
        public string Category { get; set; }
    }

    public sealed class GetModelChangesOutput
    {
        [McpParam("这次的 token。下次带着它回来，就能拿到从现在起的改动")]
        public string Token { get; set; }

        [McpParam("这次只是建立基线（没给 since）。此时三个列表都是空的")]
        public bool IsBaseline { get; set; }

        [McpParam("改动记录已经超出保留上限，早于 token 的部分已被丢弃——" +
                  "**这次的结果不完整**。需要完整状态的话请改用 revit_query_elements 重新取一遍")]
        public bool Incomplete { get; set; }

        [McpParam("新增的构件数")]
        public int AddedCount { get; set; }

        [McpParam("被修改的构件数")]
        public int ModifiedCount { get; set; }

        [McpParam("被删除的构件数")]
        public int DeletedCount { get; set; }

        [McpParam("新增的构件 ID")]
        public List<string> AddedIds { get; set; } = new List<string>();

        [McpParam("被修改的构件 ID")]
        public List<string> ModifiedIds { get; set; } = new List<string>();

        [McpParam("被删除的构件 ID。它们已经不在模型里，查不到详情")]
        public List<string> DeletedIds { get; set; } = new List<string>();

        [McpParam("新增与修改的构件明细。仅 includeDetails 为 true 时有值")]
        public List<ChangedElement> Elements { get; set; }
    }

    /// <summary>
    /// 查询模型改动。
    ///
    /// 数据来自 Revit 自己的 <c>DocumentChanged</c> 事件，从插件启动起一直在记。
    /// 覆盖的场景是"增量"：上次看过之后模型变了什么，谁改的不管，改成什么样自己再去查。
    ///
    /// **刻意不提供全模型快照落盘。** 那要把整个模型序列化一遍，
    /// 代价随模型大小线性增长；而"完整状态"这个问题 <c>revit_query_elements</c>
    /// 本来就能回答，没必要再存一份必然会过期的副本。
    ///
    /// 记录容量有限，超出后旧记录会被丢弃，此时 <c>incomplete</c> 为 true。
    /// 这一位必须看——它意味着这次拿到的改动列表是残缺的。
    /// </summary>
    [McpTool("revit_get_model_changes",
        Title = "查询模型改动",
        Description = "返回自上次 token 以来模型里新增、修改、删除了哪些构件。" +
                      "第一次调用省略 since，拿到一个基线 token；之后每次带上它。" +
                      "适合做增量同步与「我刚才那批操作到底动了什么」的复核。" +
                      "**注意 incomplete 字段**：为 true 表示改动量超出了保留上限、" +
                      "早期记录已被丢弃，这次的列表是残缺的。" +
                      "要完整状态请用 revit_query_elements 重新查。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class GetModelChangesTool : RevitTool<GetModelChangesInput, GetModelChangesOutput>
    {
        private const int DefaultDetailLimit = 500;
        private const int MaxDetailLimit = 2000;

        public override GetModelChangesOutput Execute(
            GetModelChangesInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = ResolveDocument(context, input.DocumentId);

            var tracker = App.Current?.Changes;
            if (tracker == null)
                throw new ToolFailureException(McpDomainError.ServerStopped,
                    "改动跟踪没有在运行。这通常说明插件启动时出过错，请查看 RevitMCP 日志。");

            ChangeSet changes;
            try
            {
                changes = tracker.Query(document, input.Since);
            }
            catch (ToolTokenException ex)
            {
                throw new ToolFailureException(McpDomainError.InvalidParameter, ex.Message);
            }

            var output = new GetModelChangesOutput
            {
                Token = changes.Token,
                IsBaseline = changes.IsBaseline,
                Incomplete = changes.Overflowed,
                AddedCount = changes.Added.Count,
                ModifiedCount = changes.Modified.Count,
                DeletedCount = changes.Deleted.Count,
                AddedIds = changes.Added.Select(Format).ToList(),
                ModifiedIds = changes.Modified.Select(Format).ToList(),
                DeletedIds = changes.Deleted.Select(Format).ToList()
            };

            if (changes.IsBaseline)
            {
                context.Warnings.Add(
                    "没有给 since，这次只建立了基线。带上返回的 token 再调一次，" +
                    "就能拿到这之后的改动。");
                return output;
            }

            if (changes.Overflowed)
                context.Warnings.Add(
                    "改动数量超出了保留上限，早于这个 token 的记录已被丢弃——" +
                    "**这次返回的列表不完整**，可能少了一部分改动。" +
                    "需要准确结果的话，请用 revit_query_elements 重新取一遍完整状态。");

            if (input.IncludeDetails ?? true)
                output.Elements = Describe(document, changes, input.DetailLimit, context);

            return output;
        }

        /// <summary>
        /// 给新增与修改的构件补上名称与类别。
        /// 删除的不查——它们已经不在模型里，<c>GetElement</c> 只会返回 null。
        /// </summary>
        private static List<ChangedElement> Describe(
            Document document, ChangeSet changes, int? rawLimit,
            ToolExecutionContext<UIApplication> context)
        {
            var limit = Math.Min(Math.Max(rawLimit ?? DefaultDetailLimit, 1), MaxDetailLimit);
            var results = new List<ChangedElement>();

            var candidates = changes.Added.Concat(changes.Modified).ToList();

            foreach (var raw in candidates.Take(limit))
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var element = document.GetElement(ElementIdCompat.ToElementId(raw));

                // 事件里记的是"被改过"，而它之后可能又被别的操作删掉了。
                // 查不到就跳过，ID 仍然留在上面的列表里
                if (element == null) continue;

                results.Add(new ChangedElement
                {
                    Id = Format(raw),
                    Name = AnnotationSupport.SafeName(element),
                    Category = element.Category?.Name
                });
            }

            if (candidates.Count > limit)
                context.Warnings.Add(
                    "有 " + candidates.Count + " 个构件被新增或修改，明细只给了前 " + limit +
                    " 个。完整的 ID 列表在 addedIds 与 modifiedIds 里。");

            return results;
        }

        private static string Format(long id)
        {
            return id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
