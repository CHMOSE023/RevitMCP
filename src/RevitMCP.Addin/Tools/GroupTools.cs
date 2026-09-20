using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    // ==================== 钉固 ====================

    public sealed class SetPinnedInput
    {
        [McpParam("要操作的构件 ID 列表。ElementId 与 uniqueId 两种写法都接受", Required = true)]
        public List<string> ElementIds { get; set; }

        [McpParam("true 表示钉住（锁定位置），false 表示解钉", Required = true)]
        public bool Pinned { get; set; }

        [McpParam("影响构件数超过上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    public sealed class PinChange
    {
        [McpParam("构件 ID")]
        public string Id { get; set; }

        [McpParam("构件名称")]
        public string Name { get; set; }

        [McpParam("原来的钉固状态")]
        public bool WasPinned { get; set; }

        [McpParam("这次是否真的改变了状态。本来就是目标状态的为 false")]
        public bool Changed { get; set; }
    }

    public sealed class SetPinnedOutput : IReportsAffectedElements
    {
        [McpParam("目标状态")]
        public bool Pinned { get; set; }

        [McpParam("状态被改变的构件数")]
        public int Changed { get; set; }

        [McpParam("本来就是目标状态、这次没动的构件数")]
        public int AlreadySet { get; set; }

        [McpParam("逐个构件的结果")]
        public List<PinChange> Elements { get; set; } = new List<PinChange>();

        int IReportsAffectedElements.AffectedElements => Changed;
    }

    /// <summary>
    /// 钉住 / 解钉。钉住的构件不能被移动或删除，是防误改的常规手段——
    /// 也是 <c>revit_transform_elements</c> 最常见的失败原因。
    /// </summary>
    [McpTool("revit_set_elements_pinned",
        Toolsets = new[] { Toolsets.Authoring },
        Title = "钉住/解钉构件",
        Description = "把一批构件钉住或解钉。钉住的构件在 Revit 里不能被移动、旋转或删除。" +
                      "轴网和标高通常是钉住的，移动它们之前要先解钉。" +
                      "整批要么全成、要么全不动。",
        Destructive = false,
        TimeoutSeconds = 60)]
    public sealed class SetPinnedTool : RevitTool<SetPinnedInput, SetPinnedOutput>
    {
        public override SetPinnedOutput Execute(
            SetPinnedInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);

            if (input.ElementIds == null || input.ElementIds.Count == 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "elementIds 不能为空，至少要给一个构件。");

            GuardScale(input.ElementIds.Count, input.Confirm, context,
                input.Pinned ? "钉住" : "解钉",
                "否则请分批调用，每批不超过 " + context.MaxElementsPerWrite + " 个。");

            var output = new SetPinnedOutput { Pinned = input.Pinned };

            foreach (var rawId in input.ElementIds)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var element = RequireElement(document, rawId);
                var change = new PinChange
                {
                    Id = element.Id.ToProtocolString(),
                    Name = SafeName(element)
                };

                try
                {
                    change.WasPinned = element.Pinned;

                    if (change.WasPinned != input.Pinned)
                    {
                        element.Pinned = input.Pinned;
                        change.Changed = true;
                    }
                }
                catch (Exception ex)
                {
                    // 有些构件（如组内成员、依附构件）根本没有可写的钉固状态。
                    // 整批回滚而不是跳过：模型以为钉住了 10 个、实际只钉住 8 个，
                    // 这种偏差会一路传下去
                    throw new ToolFailureException(McpDomainError.TransactionFailed,
                        "构件 " + change.Id + "（" + (change.Name ?? "无名") +
                        "）的钉固状态无法修改：" + ex.Message + "（整批未改动）。");
                }

                output.Elements.Add(change);
            }

            output.Changed = output.Elements.Count(e => e.Changed);
            output.AlreadySet = output.Elements.Count - output.Changed;
            return output;
        }

        private static string SafeName(Element element)
        {
            try { return element.Name; }
            catch { return null; }
        }
    }

    // ==================== 编组 ====================

    public sealed class GroupElementsInput
    {
        [McpParam("操作类型：create（把构件打成组）、ungroup（把组打散）", Required = true,
                  AllowedValues = new[] { "create", "ungroup" })]
        public string Operation { get; set; }

        [McpParam("要打成组的构件 ID 列表。operation 为 create 时必填。ElementId 与 uniqueId 两种写法都接受")]
        public List<string> ElementIds { get; set; }

        [McpParam("要打散的组实例 ID 列表。operation 为 ungroup 时必填，来自 revit_list_groups。ElementId 与 uniqueId 两种写法都接受")]
        public List<string> GroupIds { get; set; }

        [McpParam("新组的名称。仅 create 有效，省略则由 Revit 自动命名（「组 1」这种）")]
        public string Name { get; set; }

        [McpParam("影响构件数超过上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    public sealed class GroupResult
    {
        [McpParam("组实例 ID。create 时是新建的组，ungroup 时是被打散的那个")]
        public string GroupId { get; set; }

        [McpParam("组名")]
        public string Name { get; set; }

        [McpParam("组内成员数")]
        public int MemberCount { get; set; }

        [McpParam("打散后释放出来的构件 ID。仅 ungroup 有值")]
        public List<string> MemberIds { get; set; }
    }

    public sealed class GroupElementsOutput : IReportsAffectedElements
    {
        [McpParam("实际执行的操作")]
        public string Operation { get; set; }

        [McpParam("受影响的组数")]
        public int Affected { get; set; }

        [McpParam("逐个组的结果")]
        public List<GroupResult> Groups { get; set; } = new List<GroupResult>();

        int IReportsAffectedElements.AffectedElements => Affected;
    }

    /// <summary>
    /// 打组 / 打散。
    ///
    /// Revit 的「组」是复用单元：改一个实例，同一类型的其他实例跟着变。
    /// 这个联动是组的全部价值，也是它最容易造成意外的地方——
    /// 所以 ungroup 不视为破坏性操作，而 create 之后要提醒模型这层联动存在。
    /// </summary>
    [McpTool("revit_group_elements",
        Toolsets = new[] { Toolsets.Authoring },
        Title = "打组/打散",
        Description = "把一批构件打成组，或把组打散。组是 Revit 的复用单元：" +
                      "同一组类型的多个实例会联动——改其中一个，其他的跟着变。" +
                      "整批要么全成、要么全不动。",
        Destructive = false,
        TimeoutSeconds = 120)]
    public sealed class GroupElementsTool : RevitTool<GroupElementsInput, GroupElementsOutput>
    {
        public override GroupElementsOutput Execute(
            GroupElementsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);
            var operation = (input.Operation ?? string.Empty).Trim().ToLowerInvariant();

            switch (operation)
            {
                case "create": return Create(document, input, context);
                case "ungroup": return Ungroup(document, input, context);

                default:
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "无法识别的 operation \"" + input.Operation +
                        "\"。可用值：create（打成组）、ungroup（打散）。");
            }
        }

        private static GroupElementsOutput Create(
            Document document, GroupElementsInput input, ToolExecutionContext<UIApplication> context)
        {
            if (input.ElementIds == null || input.ElementIds.Count == 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "operation 为 create 时必须给 elementIds。");

            GuardScale(input.ElementIds.Count, input.Confirm, context, "打成组",
                "否则请分批调用，每批不超过 " + context.MaxElementsPerWrite + " 个。");

            var ids = new List<ElementId>();
            var seen = new HashSet<long>();

            foreach (var rawId in input.ElementIds)
            {
                var element = RequireElement(document, rawId);
                if (seen.Add(element.Id.GetValue())) ids.Add(element.Id);
            }

            Group group;
            try
            {
                group = document.Create.NewGroup(ids);
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "Revit 拒绝把这批构件打成组：" + ex.Message + "（未做任何改动）。" +
                    "常见原因：构件跨了工作平面或视图、已属于另一个组、或包含不可编组的类别（如房间标记与其房间分处不同组）。");
            }

            if (group == null)
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "Revit 未能创建组，但也没有报错。请检查这批构件是否可以编组。");

            Rename(group, input.Name, context);

            return Single(document, "create", group, includeMembers: false);
        }

        private static GroupElementsOutput Ungroup(
            Document document, GroupElementsInput input, ToolExecutionContext<UIApplication> context)
        {
            if (input.GroupIds == null || input.GroupIds.Count == 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "operation 为 ungroup 时必须给 groupIds。用 revit_list_groups 查组的 ID。");

            GuardScale(input.GroupIds.Count, input.Confirm, context, "打散",
                "否则请分批调用，每批不超过 " + context.MaxElementsPerWrite + " 个。");

            var output = new GroupElementsOutput { Operation = "ungroup" };

            foreach (var rawId in input.GroupIds)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var element = RequireElement(document, rawId);
                var group = element as Group;

                if (group == null)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "ID " + rawId + " 不是组实例，而是「" +
                        (element.Category?.Name ?? element.GetType().Name) +
                        "」。用 revit_list_groups 取组的 ID。");

                var name = SafeName(group);

                ICollection<ElementId> members;
                try
                {
                    members = group.UngroupMembers();
                }
                catch (Exception ex)
                {
                    throw new ToolFailureException(McpDomainError.TransactionFailed,
                        "Revit 拒绝打散组「" + (name ?? rawId) + "」：" + ex.Message + "（整批未改动）。");
                }

                output.Groups.Add(new GroupResult
                {
                    GroupId = rawId,
                    Name = name,
                    MemberCount = members.Count,
                    MemberIds = members.Select(id => id.ToProtocolString()).ToList()
                });
            }

            output.Affected = output.Groups.Count;
            return output;
        }

        /// <summary>
        /// 给组类型改名。
        /// 改的是 <c>GroupType</c> 而不是实例——Revit 里组名属于类型，
        /// 同类型的所有实例共用一个名字。
        /// </summary>
        private static void Rename(Group group, string name, ToolExecutionContext<UIApplication> context)
        {
            if (string.IsNullOrWhiteSpace(name)) return;

            try
            {
                group.GroupType.Name = name.Trim();
            }
            catch (Exception ex)
            {
                // 改名失败不该让"组已经建好了"这个事实白白回滚掉。
                // 说出来，让模型决定要不要再单独改一次
                context.Warnings.Add(
                    "组已创建，但改名为「" + name + "」失败：" + ex.Message +
                    "。组名在项目内必须唯一，可能是重名了。");
            }
        }

        private static GroupElementsOutput Single(
            Document document, string operation, Group group, bool includeMembers)
        {
            var result = new GroupResult
            {
                GroupId = group.Id.ToProtocolString(),
                Name = SafeName(group)
            };

            try
            {
                var members = group.GetMemberIds();
                result.MemberCount = members.Count;
                if (includeMembers)
                    result.MemberIds = members.Select(id => id.ToProtocolString()).ToList();
            }
            catch { /* 拿不到成员不值得让整个操作失败 */ }

            return new GroupElementsOutput
            {
                Operation = operation,
                Affected = 1,
                Groups = new List<GroupResult> { result }
            };
        }

        private static string SafeName(Element element)
        {
            try { return element.Name; }
            catch { return null; }
        }
    }

    // ==================== 查组 ====================

    public sealed class ListGroupsInput
    {
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档")]
        public string DocumentId { get; set; }

        [McpParam("只查这个组实例的成员。省略则列出模型里所有的组")]
        public string GroupId { get; set; }

        [McpParam("是否一并返回每个组的成员 ID 列表，默认 false。" +
                  "组多且成员多时这会是个很大的返回值，按需开启")]
        public bool? IncludeMembers { get; set; }
    }

    public sealed class GroupInfo
    {
        [McpParam("组实例 ID")]
        public string Id { get; set; }

        [McpParam("组名（属于组类型，同类型的实例共用）")]
        public string Name { get; set; }

        [McpParam("组类型 ID。同一类型的组实例会联动")]
        public string TypeId { get; set; }

        [McpParam("这是模型组还是详图组：Model / Detail")]
        public string Kind { get; set; }

        [McpParam("成员数")]
        public int MemberCount { get; set; }

        [McpParam("同类型的组实例总数。大于 1 说明改这一个会影响其他几个")]
        public int InstanceCount { get; set; }

        [McpParam("成员 ID 列表。仅 includeMembers 为 true 时有值")]
        public List<string> MemberIds { get; set; }
    }

    public sealed class ListGroupsOutput
    {
        [McpParam("组实例总数")]
        public int Total { get; set; }

        [McpParam("组列表")]
        public List<GroupInfo> Groups { get; set; } = new List<GroupInfo>();
    }

    [McpTool("revit_list_groups",
        Toolsets = new[] { Toolsets.Authoring },
        Title = "列出组",
        Description = "列出模型中的组实例及其名称、成员数、同类型实例数。" +
                      "instanceCount 大于 1 表示改这一个组会联动改到其他几个——" +
                      "编辑组内构件之前值得先看一眼这个数。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class ListGroupsTool : RevitTool<ListGroupsInput, ListGroupsOutput>
    {
        public override ListGroupsOutput Execute(
            ListGroupsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = ResolveDocument(context, input.DocumentId);
            var includeMembers = input.IncludeMembers ?? false;

            var groups = new List<Group>();

            if (!string.IsNullOrWhiteSpace(input.GroupId))
            {
                var element = RequireElement(document, input.GroupId);
                var group = element as Group;

                if (group == null)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "ID " + input.GroupId + " 不是组实例，而是「" +
                        (element.Category?.Name ?? element.GetType().Name) + "」。");

                groups.Add(group);

                // 单个组是"我要看它内部"，成员列表正是要看的东西
                includeMembers = input.IncludeMembers ?? true;
            }
            else
            {
                groups.AddRange(new FilteredElementCollector(document)
                    .OfClass(typeof(Group))
                    .Cast<Group>());
            }

            // 同类型实例数要先统计一遍，逐个去数是 O(n²)
            var instanceCounts = new Dictionary<long, int>();
            foreach (var group in new FilteredElementCollector(document).OfClass(typeof(Group)).Cast<Group>())
            {
                var typeId = group.GetTypeId();
                if (typeId == null || typeId == ElementId.InvalidElementId) continue;

                var key = typeId.GetValue();
                instanceCounts.TryGetValue(key, out var count);
                instanceCounts[key] = count + 1;
            }

            var output = new ListGroupsOutput();

            foreach (var group in groups)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var typeId = group.GetTypeId();
                var info = new GroupInfo
                {
                    Id = group.Id.ToProtocolString(),
                    Name = SafeName(group),
                    TypeId = typeId?.ToProtocolString(),
                    Kind = KindOf(group)
                };

                if (typeId != null && instanceCounts.TryGetValue(typeId.GetValue(), out var instances))
                    info.InstanceCount = instances;

                try
                {
                    var members = group.GetMemberIds();
                    info.MemberCount = members.Count;
                    if (includeMembers)
                        info.MemberIds = members.Select(id => id.ToProtocolString()).ToList();
                }
                catch { /* 拿不到成员时留 0，不影响其他组 */ }

                output.Groups.Add(info);
            }

            output.Groups = output.Groups
                .OrderBy(g => g.Name, StringComparer.CurrentCulture)
                .ToList();

            output.Total = output.Groups.Count;
            return output;
        }

        private static string KindOf(Group group)
        {
            var categoryId = group.Category?.Id;
            if (categoryId == null) return null;

            return (BuiltInCategory)categoryId.GetValue() == BuiltInCategory.OST_IOSDetailGroups
                ? "Detail"
                : "Model";
        }

        private static string SafeName(Element element)
        {
            try { return element.Name; }
            catch { return null; }
        }
    }
}
