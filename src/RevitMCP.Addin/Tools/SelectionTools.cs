using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    // ==================== 读取选择集 ====================

    public sealed class GetSelectionInput
    {
        [McpParam("最多返回多少条摘要，默认 200")]
        public int? Limit { get; set; }
    }

    public sealed class GetSelectionOutput
    {
        [McpParam("当前选中的构件数")]
        public int Count { get; set; }

        [McpParam("选中的构件摘要")]
        public List<ElementSummary> Elements { get; set; } = new List<ElementSummary>();

        [McpParam("是否因 limit 而被截断")]
        public bool Truncated { get; set; }
    }

    [McpTool("revit_get_selection",
        Title = "读取当前选择集",
        Description = "返回用户此刻在 Revit 里选中的构件。" +
                      "用户说「处理这些」「这几个有问题」时，先调用它把「这些」变成具体的 ID。",
        ReadOnly = true,
        TimeoutSeconds = 30)]
    public sealed class GetSelectionTool : RevitTool<GetSelectionInput, GetSelectionOutput>
    {
        private const int DefaultLimit = 200;

        public override GetSelectionOutput Execute(
            GetSelectionInput input, ToolExecutionContext<UIApplication> context)
        {
            var uiDocument = RequireUiDocument(context);
            var document = uiDocument.Document;
            var limit = Math.Max(input.Limit ?? DefaultLimit, 1);

            var selected = uiDocument.Selection.GetElementIds();

            var output = new GetSelectionOutput
            {
                Count = selected.Count,
                Truncated = selected.Count > limit
            };

            foreach (var id in selected.Take(limit))
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var element = document.GetElement(id);
                if (element != null) output.Elements.Add(Summarize(element));
            }

            if (output.Count == 0)
                context.Warnings.Add("用户当前没有在 Revit 里选中任何构件。");

            return output;
        }
    }

    // ==================== 设置选择集 ====================

    public sealed class SetSelectionInput
    {
        [McpParam("要选中的构件 ID 列表。传空数组表示清空选择", Required = true)]
        public List<string> ElementIds { get; set; }

        [McpParam("true 表示追加到现有选择，默认 false（替换）")]
        public bool? Add { get; set; }
    }

    public sealed class SetSelectionOutput
    {
        [McpParam("设置后选中的构件数")]
        public int Count { get; set; }

        [McpParam("设置前选中的构件数")]
        public int PreviousCount { get; set; }
    }

    [McpTool("revit_set_selection",
        Title = "设置当前选择集",
        Description = "在 Revit 里把指定构件选中，用户屏幕上会直接高亮显示。" +
                      "查出一批有问题的构件后用它交回给用户，比报一串 ID 让人自己找有用得多。" +
                      "它不修改模型，因此写入模式关闭时也可用；但它会改变用户眼前的状态，" +
                      "所以别在用户没要求时随手改选择集。",
        ReadOnly = true,
        TimeoutSeconds = 30)]
    public sealed class SetSelectionTool : RevitTool<SetSelectionInput, SetSelectionOutput>
    {
        public override SetSelectionOutput Execute(
            SetSelectionInput input, ToolExecutionContext<UIApplication> context)
        {
            var uiDocument = RequireUiDocument(context);
            var document = uiDocument.Document;

            if (input.ElementIds == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "elementIds 不能为 null。要清空选择请传空数组 []。");

            var previous = uiDocument.Selection.GetElementIds();

            var ids = new List<ElementId>();
            var seen = new HashSet<long>();

            if (input.Add == true)
                foreach (var id in previous)
                    if (seen.Add(id.GetValue())) ids.Add(id);

            foreach (var rawId in input.ElementIds)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                // 找不到就失败而不是跳过：选中 4 个却以为选中了 5 个，
                // 接下来对"选中项"做的每一步都会差那一个
                var element = RequireElement(document, rawId);
                if (seen.Add(element.Id.GetValue())) ids.Add(element.Id);
            }

            try
            {
                uiDocument.Selection.SetElementIds(ids);
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "Revit 拒绝设置选择集：" + ex.Message +
                    "。部分构件可能不在当前视图中，或不可被选择。");
            }

            if (ids.Count == 0)
                context.Warnings.Add("已清空 Revit 中的选择集。");

            return new SetSelectionOutput
            {
                Count = ids.Count,
                PreviousCount = previous.Count
            };
        }
    }
}
