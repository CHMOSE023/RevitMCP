using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    // ==================== 样板清单 ====================

    public sealed class ListViewTemplatesInput
    {
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档")]
        public string DocumentId { get; set; }

        [McpParam("按样板名过滤（不区分大小写的子串匹配）")]
        public string NameContains { get; set; }

        [McpParam("只返回适用于这种视图类型的样板：floorPlan、ceilingPlan、section、" +
                  "elevation、threeD、schedule、drafting、areaPlan。省略则全给",
                  AllowedValues = new[] { "floorPlan", "ceilingPlan", "section", "elevation", "threeD", "schedule", "drafting", "areaPlan" })]
        public string ViewType { get; set; }
    }

    public sealed class ViewTemplateInfo
    {
        [McpParam("样板 ID")]
        public string Id { get; set; }

        [McpParam("样板名")]
        public string Name { get; set; }

        [McpParam("适用的视图类型。样板只能套到同类型的视图上")]
        public string ViewType { get; set; }

        [McpParam("样板设定的视图比例分母。三维等无比例的视图为 null")]
        public int? Scale { get; set; }

        [McpParam("当前有多少个视图套着这个样板")]
        public int AppliedCount { get; set; }
    }

    public sealed class ListViewTemplatesOutput
    {
        [McpParam("样板总数")]
        public int Total { get; set; }

        [McpParam("样板列表")]
        public List<ViewTemplateInfo> Templates { get; set; } = new List<ViewTemplateInfo>();
    }

    [McpTool("revit_list_view_templates",
        Toolsets = new[] { Toolsets.Documentation },
        Title = "列出视图样板",
        Description = "列出项目里的视图样板及其适用的视图类型。" +
                      "样板只能套到同类型的视图上——平面的样板套不到剖面。" +
                      "拿到 ID 后用 revit_apply_view_template 批量套用。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class ListViewTemplatesTool : RevitTool<ListViewTemplatesInput, ListViewTemplatesOutput>
    {
        public override ListViewTemplatesOutput Execute(
            ListViewTemplatesInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = ResolveDocument(context, input.DocumentId);

            var views = new FilteredElementCollector(document)
                .OfClass(typeof(View))
                .Cast<View>()
                .ToList();

            // 先数一遍"谁套了谁"。逐个样板去 collect 是 O(样板数 × 视图数)
            var appliedCounts = new Dictionary<long, int>();
            foreach (var view in views)
            {
                if (view.IsTemplate) continue;

                ElementId templateId;
                try { templateId = view.ViewTemplateId; }
                catch { continue; }

                if (templateId == null || templateId == ElementId.InvalidElementId) continue;

                var key = templateId.GetValue();
                appliedCounts.TryGetValue(key, out var count);
                appliedCounts[key] = count + 1;
            }

            var wanted = ParseViewType(input.ViewType);
            var results = new List<ViewTemplateInfo>();

            foreach (var template in views.Where(v => v.IsTemplate))
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var name = SafeName(template);

                if (!string.IsNullOrWhiteSpace(input.NameContains) &&
                    (name == null ||
                     name.IndexOf(input.NameContains, StringComparison.OrdinalIgnoreCase) < 0))
                    continue;

                if (wanted != null && template.ViewType != wanted.Value) continue;

                var info = new ViewTemplateInfo
                {
                    Id = template.Id.ToProtocolString(),
                    Name = name,
                    ViewType = template.ViewType.ToString()
                };

                // 三维、明细表这些没有比例，读 Scale 会抛
                try { info.Scale = template.Scale > 0 ? template.Scale : (int?)null; }
                catch { info.Scale = null; }

                if (appliedCounts.TryGetValue(template.Id.GetValue(), out var applied))
                    info.AppliedCount = applied;

                results.Add(info);
            }

            results = results
                .OrderBy(t => t.ViewType, StringComparer.Ordinal)
                .ThenBy(t => t.Name, StringComparer.CurrentCulture)
                .ToList();

            return new ListViewTemplatesOutput { Total = results.Count, Templates = results };
        }

        /// <summary>把对外的视图类型名解析成 Revit 的 ViewType。</summary>
        private static ViewType? ParseViewType(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            switch (value.Trim().ToLowerInvariant())
            {
                case "floorplan": return Autodesk.Revit.DB.ViewType.FloorPlan;
                case "ceilingplan": return Autodesk.Revit.DB.ViewType.CeilingPlan;
                case "section": return Autodesk.Revit.DB.ViewType.Section;
                case "elevation": return Autodesk.Revit.DB.ViewType.Elevation;
                case "threed":
                case "3d": return Autodesk.Revit.DB.ViewType.ThreeD;
                case "schedule": return Autodesk.Revit.DB.ViewType.Schedule;
                case "drafting": return Autodesk.Revit.DB.ViewType.DraftingView;
                case "areaplan": return Autodesk.Revit.DB.ViewType.AreaPlan;

                default:
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "无法识别的 viewType \"" + value +
                        "\"。可用值：floorPlan、ceilingPlan、section、elevation、threeD、" +
                        "schedule、drafting、areaPlan。");
            }
        }

        private static string SafeName(Element element)
        {
            try { return element?.Name; }
            catch { return null; }
        }
    }

    // ==================== 套样板 ====================

    public sealed class ApplyViewTemplateInput
    {
        [McpParam("要套样板的视图 ID 列表，来自 revit_list_views。ElementId 与 uniqueId 两种写法都接受", Required = true)]
        public List<string> ViewIds { get; set; }

        [McpParam("视图样板 ID，来自 revit_list_view_templates。" +
                  "传 null 或空字符串表示**取消**套用，把视图还原成独立可调的状态")]
        public string TemplateId { get; set; }

        [McpParam("影响视图数超过上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    public sealed class TemplateApplication
    {
        [McpParam("视图 ID")]
        public string Id { get; set; }

        [McpParam("视图名")]
        public string Name { get; set; }

        [McpParam("视图类型")]
        public string ViewType { get; set; }

        [McpParam("原来套的样板名，没套则为 null")]
        public string OldTemplate { get; set; }

        [McpParam("现在套的样板名，取消套用则为 null")]
        public string NewTemplate { get; set; }
    }

    public sealed class ApplyViewTemplateOutput : IReportsAffectedElements
    {
        [McpParam("套用的样板名。取消套用时为 null")]
        public string Template { get; set; }

        [McpParam("成功改动的视图数")]
        public int Changed { get; set; }

        [McpParam("本来就是这个状态、这次没动的视图数")]
        public int AlreadySet { get; set; }

        [McpParam("逐个视图的结果")]
        public List<TemplateApplication> Views { get; set; } = new List<TemplateApplication>();

        int IReportsAffectedElements.AffectedElements => Changed;
    }

    /// <summary>
    /// 批量套 / 取消视图样板。
    ///
    /// 套样板会把视图的一大堆设置（可见性、比例、详细程度、过滤器）交给样板管，
    /// 并让这些设置在视图属性里变灰不可改。这是企业标准落地最直接的手段，
    /// 也是"我明明改了视图设置却没生效"最常见的原因。
    /// </summary>
    [McpTool("revit_apply_view_template",
        Toolsets = new[] { Toolsets.Documentation },
        Title = "套用视图样板",
        Description = "给一批视图套上视图样板，或取消套用（templateId 传空）。" +
                      "套上之后，样板管辖的那些设置在视图里会变成只读——" +
                      "这既是统一出图标准的手段，也是改不动视图设置时该先查的地方。" +
                      "**Revit 允许把样板套到不同类型的视图上**（平面的样板能套到三维上），" +
                      "此时只有两者都有的那些设置会生效，其余的被忽略，不会报错。" +
                      "所以请自己用 revit_list_view_templates 的 viewType 选对样板——" +
                      "套错了不会失败，只会悄悄少生效一部分设置。",
        TimeoutSeconds = 120)]
    public sealed class ApplyViewTemplateTool : RevitTool<ApplyViewTemplateInput, ApplyViewTemplateOutput>
    {
        public override ApplyViewTemplateOutput Execute(
            ApplyViewTemplateInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);

            if (input.ViewIds == null || input.ViewIds.Count == 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "viewIds 不能为空，至少要给一个视图。");

            var clearing = string.IsNullOrWhiteSpace(input.TemplateId);
            View template = null;

            if (!clearing)
            {
                template = CreateViewsTool.RequireTemplate(document, input.TemplateId, -1);
            }

            GuardScale(input.ViewIds.Count, input.Confirm, context,
                clearing ? "取消样板套用于" : "套用样板于");

            var output = new ApplyViewTemplateOutput
            {
                Template = clearing ? null : SafeName(template)
            };

            var targetId = clearing ? ElementId.InvalidElementId : template.Id;

            foreach (var rawId in input.ViewIds)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var element = RequireElement(document, rawId);
                var view = element as View;

                if (view == null)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "ID " + rawId + " 不是视图，而是「" +
                        (element.Category?.Name ?? element.GetType().Name) +
                        "」。用 revit_list_views 取视图 ID。");

                if (view.IsTemplate)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "ID " + rawId + "（" + SafeName(view) + "）本身就是一个视图样板，" +
                        "样板不能再套样板。");

                var record = new TemplateApplication
                {
                    Id = rawId,
                    Name = SafeName(view),
                    ViewType = view.ViewType.ToString(),
                    OldTemplate = TemplateNameOf(document, view),
                    NewTemplate = output.Template
                };

                ElementId currentId;
                try { currentId = view.ViewTemplateId; }
                catch { currentId = ElementId.InvalidElementId; }

                if (currentId != null && currentId.GetValue() == targetId.GetValue())
                {
                    output.AlreadySet++;
                    output.Views.Add(record);
                    continue;
                }

                try
                {
                    view.ViewTemplateId = targetId;
                }
                catch (Exception ex)
                {
                    throw new ToolFailureException(McpDomainError.TransactionFailed,
                        "Revit 拒绝给视图「" + (record.Name ?? rawId) + "」（" + record.ViewType +
                        "）" + (clearing ? "取消样板" : "套用样板「" + output.Template + "」") +
                        "：" + ex.Message + "（整批未改动）。" +
                        (clearing
                            ? ""
                            : "注意：Revit 并不因为样板与视图类型不同就拒绝，" +
                              "所以走到这里通常是别的原因——视图被锁、工作集不可编辑之类。"));
                }

                output.Changed++;
                output.Views.Add(record);
            }

            return output;
        }

        private static string TemplateNameOf(Document document, View view)
        {
            try
            {
                var templateId = view.ViewTemplateId;
                if (templateId == null || templateId == ElementId.InvalidElementId) return null;

                return SafeName(document.GetElement(templateId));
            }
            catch { return null; }
        }

        private static string SafeName(Element element)
        {
            try { return element?.Name; }
            catch { return null; }
        }
    }
}
