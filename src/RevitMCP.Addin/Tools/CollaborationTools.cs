using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    // ==================== 工作集 ====================

    public sealed class ListWorksetsInput
    {
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档")]
        public string DocumentId { get; set; }

        [McpParam("工作集种类：user（用户工作集，默认）、family（族）、view（视图）、standards（标准）、all",
                  AllowedValues = new[] { "user", "family", "view", "standards", "all" })]
        public string Kind { get; set; }
    }

    public sealed class WorksetInfo
    {
        [McpParam("工作集 ID")]
        public string Id { get; set; }

        [McpParam("工作集名")]
        public string Name { get; set; }

        [McpParam("种类：UserWorkset / FamilyWorkset / ViewWorkset / StandardsWorkset")]
        public string Kind { get; set; }

        [McpParam("是否在所有视图中默认可见")]
        public bool VisibleByDefault { get; set; }

        [McpParam("是否已打开（关闭的工作集里的构件查不到，审计时要留意）")]
        public bool IsOpen { get; set; }

        [McpParam("当前的编辑权归属者。为空表示没人占用")]
        public string Owner { get; set; }

        [McpParam("这个工作集里有多少个构件")]
        public int ElementCount { get; set; }
    }

    public sealed class ListWorksetsOutput
    {
        [McpParam("是不是工作共享模型。false 时后面的列表是空的")]
        public bool IsWorkshared { get; set; }

        [McpParam("中心文件路径。非工作共享模型为 null")]
        public string CentralPath { get; set; }

        [McpParam("当前用户名——判断某个工作集是不是自己占着就靠它")]
        public string CurrentUser { get; set; }

        [McpParam("工作集总数")]
        public int Total { get; set; }

        [McpParam("工作集列表")]
        public List<WorksetInfo> Worksets { get; set; } = new List<WorksetInfo>();
    }

    /// <summary>
    /// 列出工作集。
    ///
    /// 对审计特别重要的一点：**关闭的工作集里的构件，查询根本看不见**。
    /// 一次"全模型检查"如果漏掉了半个模型，结论会是错的而且完全看不出来——
    /// 所以这个工具把每个工作集的开关状态明说，让调用方能先确认范围是不是完整的。
    /// </summary>
    [McpTool("revit_list_worksets",
        Title = "列出工作集",
        Description = "列出工作共享模型的工作集及其开关状态、编辑权归属、构件数。" +
                      "**做全模型审计前值得先调它**：关闭的工作集里的构件查询看不到，" +
                      "漏掉半个模型而结论看起来仍然正常，是最难发现的那种错误。" +
                      "非工作共享模型会返回 isWorkshared: false 和一个空列表。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class ListWorksetsTool : RevitTool<ListWorksetsInput, ListWorksetsOutput>
    {
        public override ListWorksetsOutput Execute(
            ListWorksetsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = ResolveDocument(context, input.DocumentId);
            var output = new ListWorksetsOutput();

            try { output.IsWorkshared = document.IsWorkshared; }
            catch { output.IsWorkshared = false; }

            try { output.CurrentUser = document.Application?.Username; }
            catch { /* 拿不到用户名不影响其他信息 */ }

            if (!output.IsWorkshared)
            {
                context.Warnings.Add(
                    "「" + OpenDocumentTool.SafeTitle(document) + "」不是工作共享模型，没有工作集。");
                return output;
            }

            output.CentralPath = CentralPathOf(document);

            var kinds = ParseKinds(input.Kind);
            var counts = CountByWorkset(document);

            foreach (var workset in new FilteredWorksetCollector(document).ToWorksets())
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                if (!kinds.Contains(workset.Kind)) continue;

                var info = new WorksetInfo
                {
                    Id = workset.Id.IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Name = workset.Name,
                    Kind = workset.Kind.ToString(),
                    IsOpen = workset.IsOpen,
                    VisibleByDefault = workset.IsVisibleByDefault,
                    Owner = string.IsNullOrEmpty(workset.Owner) ? null : workset.Owner
                };

                if (counts.TryGetValue(workset.Id.IntegerValue, out var count)) info.ElementCount = count;

                output.Worksets.Add(info);
            }

            output.Worksets = output.Worksets
                .OrderBy(w => w.Kind, StringComparer.Ordinal)
                .ThenBy(w => w.Name, StringComparer.CurrentCulture)
                .ToList();

            output.Total = output.Worksets.Count;

            var closed = output.Worksets.Count(w => w.Kind == "UserWorkset" && !w.IsOpen);
            if (closed > 0)
                context.Warnings.Add(
                    "有 " + closed + " 个用户工作集当前是关闭的，里面的构件用查询工具看不到。" +
                    "要做完整审计，请让用户先在 Revit 的「工作集」对话框里把它们打开。");

            return output;
        }

        private static HashSet<WorksetKind> ParseKinds(string value)
        {
            var text = (value ?? "user").Trim().ToLowerInvariant();

            switch (text)
            {
                case "user": return new HashSet<WorksetKind> { WorksetKind.UserWorkset };
                case "family": return new HashSet<WorksetKind> { WorksetKind.FamilyWorkset };
                case "view": return new HashSet<WorksetKind> { WorksetKind.ViewWorkset };
                case "standards": return new HashSet<WorksetKind> { WorksetKind.StandardWorkset };

                case "all":
                    return new HashSet<WorksetKind>(
                        Enum.GetValues(typeof(WorksetKind)).Cast<WorksetKind>());

                default:
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "无法识别的 kind \"" + value +
                        "\"。可用值：user、family、view、standards、all。");
            }
        }

        /// <summary>
        /// 每个工作集里有多少构件。一次遍历数完，而不是逐个工作集去 collect。
        /// </summary>
        private static Dictionary<int, int> CountByWorkset(Document document)
        {
            var counts = new Dictionary<int, int>();

            try
            {
                foreach (var element in new FilteredElementCollector(document).WhereElementIsNotElementType())
                {
                    var id = element.WorksetId;
                    if (id == null) continue;

                    counts.TryGetValue(id.IntegerValue, out var count);
                    counts[id.IntegerValue] = count + 1;
                }
            }
            catch { /* 数不出来就不给这一列，不影响工作集本身的信息 */ }

            return counts;
        }

        internal static string CentralPathOf(Document document)
        {
            try
            {
                var path = document.GetWorksharingCentralModelPath();
                if (path == null) return null;

                return ModelPathUtils.ConvertModelPathToUserVisiblePath(path);
            }
            catch { return null; }
        }
    }

    // ==================== 同步与放弃权限 ====================

    public sealed class SyncToCentralInput
    {
        [McpParam("操作：sync（同步到中心文件）、relinquish（只放弃编辑权，不同步）", Required = true,
                  AllowedValues = new[] { "sync", "relinquish" })]
        public string Action { get; set; }

        [McpParam("同步时写进操作记录的备注。填清楚这次同步改了什么，" +
                  "它会出现在 Revit 的同步历史里供其他人看")]
        public string Comment { get; set; }

        [McpParam("同步后是否放弃所有编辑权，默认 true。" +
                  "false 表示同步完仍然占着这些构件，别人改不了")]
        public bool? RelinquishAfter { get; set; }

        [McpParam("同步前是否先保存本地文件，默认 true")]
        public bool? SaveLocalBefore { get; set; }

        [McpParam("同步会影响所有协作者，必须显式带 true 才执行", Required = true)]
        public bool Confirm { get; set; }
    }

    public sealed class SyncToCentralOutput
    {
        [McpParam("实际执行的操作")]
        public string Action { get; set; }

        [McpParam("文档标题")]
        public string Title { get; set; }

        [McpParam("中心文件路径")]
        public string CentralPath { get; set; }

        [McpParam("操作前本地有没有未保存的改动")]
        public bool HadUnsavedChanges { get; set; }

        [McpParam("是否已放弃编辑权")]
        public bool Relinquished { get; set; }
    }

    /// <summary>
    /// 同步到中心文件 / 放弃编辑权。
    ///
    /// 这是整个服务里影响面最大的一个操作：它把改动推给**所有协作者**，
    /// 而且推出去就收不回来——撤销栈管不到中心文件。
    /// 所以 confirm 是必填的布尔值而不是"超限才要"的可选项：
    /// 其他写工具的规模闸问的是"你确定范围对吗"，这里问的是"你确定要发布吗"。
    /// </summary>
    [McpTool("revit_sync_to_central",
        Title = "同步到中心文件",
        Description = "把本地改动同步到中心文件，或放弃占用的编辑权。" +
                      "**同步会把改动推给所有协作者，且无法撤销**——" +
                      "撤销栈只管本地，管不到中心文件。所以 confirm 必须显式为 true。" +
                      "同步前请确认改动确实是要发布的；只是想存一下本地用 revit_save_document。",
        WithoutTransaction = true,
        TimeoutSeconds = 900)]
    public sealed class SyncToCentralTool : RevitTool<SyncToCentralInput, SyncToCentralOutput>
    {
        public override SyncToCentralOutput Execute(
            SyncToCentralInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);

            bool workshared;
            try { workshared = document.IsWorkshared; }
            catch { workshared = false; }

            if (!workshared)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "「" + OpenDocumentTool.SafeTitle(document) +
                    "」不是工作共享模型，没有中心文件可同步。要保存它用 revit_save_document。");

            if (!input.Confirm)
                throw new ToolFailureException(McpDomainError.ConfirmationRequired,
                    "同步会把本地改动推送给所有协作者，且无法撤销。" +
                    "确认要发布这些改动后，带上 confirm: true 重新调用。" +
                    "想先看看改了什么，可以用 revit_get_warnings 与 revit_query_elements 核对。");

            var action = (input.Action ?? string.Empty).Trim().ToLowerInvariant();

            var output = new SyncToCentralOutput
            {
                Action = action,
                Title = OpenDocumentTool.SafeTitle(document),
                CentralPath = ListWorksetsTool.CentralPathOf(document)
            };

            try { output.HadUnsavedChanges = document.IsModified; }
            catch { }

            switch (action)
            {
                case "sync":
                    Synchronize(document, input, output);
                    break;

                case "relinquish":
                    Relinquish(document, output);
                    break;

                default:
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "无法识别的 action \"" + input.Action +
                        "\"。可用值：sync（同步到中心文件）、relinquish（只放弃编辑权）。");
            }

            return output;
        }

        private static void Synchronize(
            Document document, SyncToCentralInput input, SyncToCentralOutput output)
        {
            var relinquish = input.RelinquishAfter ?? true;

            var relinquishOptions = new RelinquishOptions(false)
            {
                CheckedOutElements = relinquish,
                FamilyWorksets = relinquish,
                StandardWorksets = relinquish,
                UserWorksets = relinquish,
                ViewWorksets = relinquish
            };

            var synchronizeOptions = new SynchronizeWithCentralOptions
            {
                Comment = input.Comment ?? string.Empty,
                SaveLocalBefore = input.SaveLocalBefore ?? true,
                SaveLocalAfter = true
            };

            synchronizeOptions.SetRelinquishOptions(relinquishOptions);

            var transactOptions = new TransactWithCentralOptions();

            try
            {
                document.SynchronizeWithCentral(transactOptions, synchronizeOptions);
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "同步到中心文件失败：" + ex.Message +
                    "。常见原因：中心文件正被别人占用、网络不通、或本地有未解决的冲突。" +
                    "本地改动仍在，可以稍后重试。");
            }

            output.Relinquished = relinquish;
        }

        private static void Relinquish(Document document, SyncToCentralOutput output)
        {
            var options = new RelinquishOptions(true);

            try
            {
                WorksharingUtils.RelinquishOwnership(document, options, new TransactWithCentralOptions());
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "放弃编辑权失败：" + ex.Message +
                    "。注意：只有已经同步过的改动才能放弃权限——" +
                    "手上还有没同步的改动时，Revit 会拒绝放弃那些构件。");
            }

            output.Relinquished = true;
        }
    }

    // ==================== 链接模型 ====================

    public sealed class ListLinksInput
    {
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档")]
        public string DocumentId { get; set; }

        [McpParam("是否把 CAD 链接（DWG/DXF）也列出来，默认 false（只列 RVT 链接）")]
        public bool? IncludeCad { get; set; }
    }

    public sealed class LinkInfo
    {
        [McpParam("链接类型 ID（对应「管理链接」对话框里的一行）")]
        public string TypeId { get; set; }

        [McpParam("链接名")]
        public string Name { get; set; }

        [McpParam("链接文件的路径")]
        public string Path { get; set; }

        [McpParam("链接种类：RevitLink / CADLink")]
        public string Kind { get; set; }

        [McpParam("载入状态：Loaded（已载入）、Unloaded（已卸载）、NotFound（找不到文件）。" +
                  "不是 Loaded 的链接，它里面的构件查不到")]
        public string Status { get; set; }

        [McpParam("路径类型：Absolute / Relative / ServerPath 等")]
        public string PathType { get; set; }

        [McpParam("这个链接在模型里放了几个实例")]
        public int InstanceCount { get; set; }

        [McpParam("各个实例的 ID")]
        public List<string> InstanceIds { get; set; } = new List<string>();

        [McpParam("已载入的 RVT 链接对应的 documentId——" +
                  "拿它可以直接查链接模型里的构件")]
        public string LinkedDocumentId { get; set; }
    }

    public sealed class ListLinksOutput
    {
        [McpParam("链接总数")]
        public int Total { get; set; }

        [McpParam("链接列表")]
        public List<LinkInfo> Links { get; set; } = new List<LinkInfo>();
    }

    /// <summary>
    /// 列出链接模型。
    ///
    /// 审计时的意义和工作集一样：链接模型里的构件**不属于**当前文档，
    /// 常规查询一个都查不到。一个"墙全查了一遍"的结论，
    /// 在结构专业是链接进来的项目上会彻底跑偏。
    /// </summary>
    [McpTool("revit_list_links",
        Title = "列出链接模型",
        Description = "列出项目里链接的 Revit 模型（可选带上 CAD 链接）及其载入状态、实例数。" +
                      "**链接模型里的构件不属于当前文档，普通查询查不到**——" +
                      "要查它们，用回执里的 linkedDocumentId 作为 documentId 传给查询工具。" +
                      "状态不是 Loaded 的链接，里面的东西一样查不到。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class ListLinksTool : RevitTool<ListLinksInput, ListLinksOutput>
    {
        public override ListLinksOutput Execute(
            ListLinksInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = ResolveDocument(context, input.DocumentId);
            var includeCad = input.IncludeCad ?? false;

            var output = new ListLinksOutput();

            foreach (var linkType in new FilteredElementCollector(document)
                         .OfClass(typeof(RevitLinkType))
                         .Cast<RevitLinkType>())
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                output.Links.Add(DescribeRevitLink(document, linkType));
            }

            if (includeCad)
            {
                foreach (var linkType in new FilteredElementCollector(document)
                             .OfClass(typeof(CADLinkType))
                             .Cast<CADLinkType>())
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    output.Links.Add(DescribeCadLink(document, linkType));
                }
            }

            output.Links = output.Links
                .OrderBy(l => l.Kind, StringComparer.Ordinal)
                .ThenBy(l => l.Name, StringComparer.CurrentCulture)
                .ToList();

            output.Total = output.Links.Count;

            var broken = output.Links.Count(l => l.Status == "NotFound");
            if (broken > 0)
                context.Warnings.Add(
                    "有 " + broken + " 个链接找不到文件。它们在模型里是空的，" +
                    "任何依赖这些链接的检查都会漏掉对应专业的内容。");

            return output;
        }

        private static LinkInfo DescribeRevitLink(Document document, RevitLinkType linkType)
        {
            var info = new LinkInfo
            {
                TypeId = linkType.Id.ToProtocolString(),
                Name = AnnotationSupport.SafeName(linkType),
                Kind = "RevitLink"
            };

            FillExternalReference(document, linkType, info);
            FillInstances(document, linkType, info);

            // 已载入的链接可以直接拿到它的 Document，这样就能用 documentId 去查里面的构件
            if (info.InstanceIds.Count > 0)
            {
                try
                {
                    var instance = document.GetElement(
                        ElementIdCompat.TryParse(info.InstanceIds[0], out var id) ? id : null)
                        as RevitLinkInstance;

                    var linked = instance?.GetLinkDocument();
                    if (linked != null) info.LinkedDocumentId = DocumentRef.KeyOf(linked);
                }
                catch { /* 没载入就拿不到，状态字段已经说明了 */ }
            }

            return info;
        }

        private static LinkInfo DescribeCadLink(Document document, CADLinkType linkType)
        {
            var info = new LinkInfo
            {
                TypeId = linkType.Id.ToProtocolString(),
                Name = AnnotationSupport.SafeName(linkType),
                Kind = "CADLink"
            };

            FillExternalReference(document, linkType, info);
            FillInstances(document, linkType, info);

            return info;
        }

        private static void FillExternalReference(Document document, Element linkType, LinkInfo info)
        {
            try
            {
                if (!ExternalFileUtils.IsExternalFileReference(document, linkType.Id)) return;

                var reference = ExternalFileUtils.GetExternalFileReference(document, linkType.Id);
                if (reference == null) return;

                info.Status = reference.GetLinkedFileStatus().ToString();
                info.PathType = reference.PathType.ToString();

                var path = reference.GetAbsolutePath();
                if (path != null)
                    info.Path = ModelPathUtils.ConvertModelPathToUserVisiblePath(path);
            }
            catch { /* 云端链接等情况读不到，留空 */ }
        }

        private static void FillInstances(Document document, Element linkType, LinkInfo info)
        {
            try
            {
                var instances = new FilteredElementCollector(document)
                    .WhereElementIsNotElementType()
                    .Where(e =>
                    {
                        var typeId = e.GetTypeId();
                        return typeId != null && typeId.GetValue() == linkType.Id.GetValue();
                    })
                    .ToList();

                info.InstanceCount = instances.Count;
                info.InstanceIds = instances.Select(e => e.Id.ToProtocolString()).ToList();
            }
            catch { /* 数不出实例不影响这一条的其他信息 */ }
        }
    }

    // ==================== 阶段与设计选项 ====================

    public sealed class ListPhasesInput
    {
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档")]
        public string DocumentId { get; set; }
    }

    public sealed class PhaseInfo
    {
        [McpParam("阶段 ID。查询工具的 phaseId 参数用它")]
        public string Id { get; set; }

        [McpParam("阶段名")]
        public string Name { get; set; }

        [McpParam("在阶段序列里的次序，从 0 起。越大越晚")]
        public int Sequence { get; set; }

        [McpParam("在这个阶段被创建的构件数")]
        public int CreatedCount { get; set; }

        [McpParam("在这个阶段被拆除的构件数")]
        public int DemolishedCount { get; set; }
    }

    public sealed class PhaseFilterInfo
    {
        [McpParam("阶段过滤器 ID")]
        public string Id { get; set; }

        [McpParam("过滤器名")]
        public string Name { get; set; }
    }

    public sealed class ListPhasesOutput
    {
        [McpParam("阶段数")]
        public int Total { get; set; }

        [McpParam("阶段列表，按次序排列")]
        public List<PhaseInfo> Phases { get; set; } = new List<PhaseInfo>();

        [McpParam("项目里定义的阶段过滤器")]
        public List<PhaseFilterInfo> Filters { get; set; } = new List<PhaseFilterInfo>();
    }

    /// <summary>
    /// 列出阶段与阶段过滤器。
    ///
    /// 改造项目里，同一个位置会同时存在"现有的墙"和"新建的墙"——
    /// 不分阶段地统计，数量会直接翻倍，而结果看起来完全正常。
    /// 这个工具是把那种错误变得可见的第一步。
    /// </summary>
    [McpTool("revit_list_phases",
        Title = "列出阶段",
        Description = "列出项目的阶段序列与阶段过滤器，并给出每个阶段创建/拆除了多少构件。" +
                      "**改造类项目做统计前必须先看它**：同一位置的「现有」与「新建」是两个构件，" +
                      "不分阶段去数，数量会翻倍而结果看起来一切正常。" +
                      "拿到阶段 ID 后，用 revit_query_elements 的 phaseId 参数限定范围。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class ListPhasesTool : RevitTool<ListPhasesInput, ListPhasesOutput>
    {
        public override ListPhasesOutput Execute(
            ListPhasesInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = ResolveDocument(context, input.DocumentId);
            var output = new ListPhasesOutput();

            var created = new Dictionary<long, int>();
            var demolished = new Dictionary<long, int>();
            CountByPhase(document, created, demolished);

            var sequence = 0;
            foreach (Phase phase in document.Phases)
            {
                if (phase == null) continue;

                var info = new PhaseInfo
                {
                    Id = phase.Id.ToProtocolString(),
                    Name = AnnotationSupport.SafeName(phase),
                    Sequence = sequence++
                };

                var key = phase.Id.GetValue();
                if (created.TryGetValue(key, out var c)) info.CreatedCount = c;
                if (demolished.TryGetValue(key, out var d)) info.DemolishedCount = d;

                output.Phases.Add(info);
            }

            foreach (var filter in new FilteredElementCollector(document)
                         .OfClass(typeof(PhaseFilter))
                         .Cast<PhaseFilter>())
            {
                output.Filters.Add(new PhaseFilterInfo
                {
                    Id = filter.Id.ToProtocolString(),
                    Name = AnnotationSupport.SafeName(filter)
                });
            }

            output.Filters = output.Filters
                .OrderBy(f => f.Name, StringComparer.CurrentCulture)
                .ToList();

            output.Total = output.Phases.Count;

            if (output.Total > 1)
                context.Warnings.Add(
                    "这个项目有 " + output.Total + " 个阶段。做数量统计时请用 phaseId 限定范围，" +
                    "否则「现有」与「新建」的构件会被一起数进去。");

            return output;
        }

        private static void CountByPhase(
            Document document, Dictionary<long, int> created, Dictionary<long, int> demolished)
        {
            try
            {
                foreach (var element in new FilteredElementCollector(document).WhereElementIsNotElementType())
                {
                    Bump(created, element, BuiltInParameter.PHASE_CREATED);
                    Bump(demolished, element, BuiltInParameter.PHASE_DEMOLISHED);
                }
            }
            catch { /* 数不出来就不给这两列 */ }
        }

        private static void Bump(Dictionary<long, int> counts, Element element, BuiltInParameter which)
        {
            try
            {
                var parameter = element.get_Parameter(which);
                if (parameter == null || !parameter.HasValue) return;

                var id = parameter.AsElementId();
                if (id == null || id == ElementId.InvalidElementId) return;

                var key = id.GetValue();
                counts.TryGetValue(key, out var count);
                counts[key] = count + 1;
            }
            catch { /* 这个构件没有阶段参数，跳过 */ }
        }
    }

    public sealed class ListDesignOptionsInput
    {
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档")]
        public string DocumentId { get; set; }
    }

    public sealed class DesignOptionInfo
    {
        [McpParam("设计选项 ID")]
        public string Id { get; set; }

        [McpParam("选项名")]
        public string Name { get; set; }

        [McpParam("所属选项集名")]
        public string SetName { get; set; }

        [McpParam("所属选项集 ID")]
        public string SetId { get; set; }

        [McpParam("是不是这个选项集的主选项。非主选项里的构件默认不出现在视图里")]
        public bool IsPrimary { get; set; }

        [McpParam("这个选项里有多少构件")]
        public int ElementCount { get; set; }
    }

    public sealed class ListDesignOptionsOutput
    {
        [McpParam("设计选项总数")]
        public int Total { get; set; }

        [McpParam("不属于任何设计选项的构件数——也就是「主模型」的规模")]
        public int MainModelElementCount { get; set; }

        [McpParam("设计选项列表，按选项集分组排序")]
        public List<DesignOptionInfo> Options { get; set; } = new List<DesignOptionInfo>();
    }

    /// <summary>
    /// 列出设计选项。
    ///
    /// 和阶段是同一类问题：多方案模型里，同一个位置会有好几套构件，
    /// 不加区分地统计会把所有方案一起数进去。
    /// </summary>
    [McpTool("revit_list_design_options",
        Title = "列出设计选项",
        Description = "列出项目里的设计选项集与各个选项，以及每个选项里有多少构件。" +
                      "**多方案模型做统计前必须先看它**：几套方案的构件会被一起数进去。" +
                      "拿到选项 ID 后，用 revit_query_elements 的 designOptionId 限定范围；" +
                      "只要主模型的话传 designOptionId: \"main\"。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class ListDesignOptionsTool : RevitTool<ListDesignOptionsInput, ListDesignOptionsOutput>
    {
        public override ListDesignOptionsOutput Execute(
            ListDesignOptionsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = ResolveDocument(context, input.DocumentId);
            var output = new ListDesignOptionsOutput();

            var counts = new Dictionary<long, int>();

            try
            {
                foreach (var element in new FilteredElementCollector(document).WhereElementIsNotElementType())
                {
                    var optionId = element.DesignOption?.Id;

                    if (optionId == null || optionId == ElementId.InvalidElementId)
                    {
                        output.MainModelElementCount++;
                        continue;
                    }

                    var key = optionId.GetValue();
                    counts.TryGetValue(key, out var count);
                    counts[key] = count + 1;
                }
            }
            catch { /* 数不出来就不给构件数 */ }

            foreach (var option in new FilteredElementCollector(document)
                         .OfClass(typeof(DesignOption))
                         .Cast<DesignOption>())
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var info = new DesignOptionInfo
                {
                    Id = option.Id.ToProtocolString(),
                    Name = AnnotationSupport.SafeName(option)
                };

                try { info.IsPrimary = option.IsPrimary; }
                catch { }

                try
                {
                    var setId = option.get_Parameter(BuiltInParameter.OPTION_SET_ID)?.AsElementId();
                    if (setId != null && setId != ElementId.InvalidElementId)
                    {
                        info.SetId = setId.ToProtocolString();
                        info.SetName = AnnotationSupport.SafeName(document.GetElement(setId));
                    }
                }
                catch { /* 取不到选项集不影响选项本身 */ }

                if (counts.TryGetValue(option.Id.GetValue(), out var elements))
                    info.ElementCount = elements;

                output.Options.Add(info);
            }

            output.Options = output.Options
                .OrderBy(o => o.SetName, StringComparer.CurrentCulture)
                .ThenByDescending(o => o.IsPrimary)
                .ThenBy(o => o.Name, StringComparer.CurrentCulture)
                .ToList();

            output.Total = output.Options.Count;

            if (output.Total > 0)
                context.Warnings.Add(
                    "这个项目有 " + output.Total + " 个设计选项。做数量统计时请用 designOptionId 限定范围，" +
                    "否则几套方案的构件会被一起数进去。");

            return output;
        }
    }
}
