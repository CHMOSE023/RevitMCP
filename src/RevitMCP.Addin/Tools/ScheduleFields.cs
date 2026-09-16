using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    // ==================== 可用字段 ====================

    public sealed class ListSchedulableFieldsInput
    {
        [McpParam("BuiltInCategory 名，如 OST_Doors、OST_Rooms。可省略 OST_ 前缀", Required = true)]
        public string Category { get; set; }

        [McpParam("按字段名过滤（不区分大小写的子串匹配）")]
        public string NameContains { get; set; }
    }

    public sealed class SchedulableFieldInfo
    {
        [McpParam("字段名。revit_create_schedule 的 fields 用的就是这个名字")]
        public string Name { get; set; }

        [McpParam("字段来源：Instance（实例参数）、ElementType（类型参数）、Count（计数）等")]
        public string FieldType { get; set; }

        [McpParam("这个名字是否在本类别下不唯一。为 true 时按名字选会选中第一个——" +
                  "Revit 允许不同来源的参数重名（实测墙上有两个「备注」）")]
        public bool Duplicate { get; set; }
    }

    public sealed class ListSchedulableFieldsOutput
    {
        [McpParam("类别")]
        public string Category { get; set; }

        [McpParam("可用字段数")]
        public int Total { get; set; }

        [McpParam("可用字段，按名称排列")]
        public List<SchedulableFieldInfo> Fields { get; set; } = new List<SchedulableFieldInfo>();
    }

    [McpTool("revit_list_schedulable_fields",
        Title = "列出明细表可用字段",
        Description = "列出某个类别做明细表时能选哪些字段。" +
                      "建明细表之前先用它——字段名是 Revit 按项目语言给的（中文项目里就是中文），" +
                      "凭猜是猜不中的。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class ListSchedulableFieldsTool
        : RevitTool<ListSchedulableFieldsInput, ListSchedulableFieldsOutput>
    {
        public override ListSchedulableFieldsOutput Execute(
            ListSchedulableFieldsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);
            var category = ParseCategory(input.Category);

            var fields = ScheduleSupport.ProbeFields(document, category, input.NameContains);

            var output = new ListSchedulableFieldsOutput
            {
                Category = category.ToString(),
                Total = fields.Count,
                Fields = fields
            };

            if (output.Total == 0)
                context.Warnings.Add(
                    "类别 " + category + " 没有可用于明细表的字段，做不了明细表。" +
                    "注释符号、导入的图元这类没有可统计参数的类别会这样。");

            return output;
        }
    }

    // ==================== 创建明细表 ====================

    public sealed class CreateScheduleInput
    {
        [McpParam("要统计的类别，如 OST_Doors、OST_Rooms、OST_Walls", Required = true)]
        public string Category { get; set; }

        [McpParam("明细表名称。省略则用 Revit 的默认名")]
        public string Name { get; set; }

        [McpParam("要显示的字段名，按列的先后顺序。来自 revit_list_schedulable_fields", Required = true)]
        public List<string> Fields { get; set; }

        [McpParam("按哪个字段排序，必须是 fields 里的一个")]
        public string SortBy { get; set; }

        [McpParam("true 为降序，默认升序")]
        public bool? SortDescending { get; set; }

        [McpParam("true 时每个构件占一行（默认）；false 时相同的行合并成一条汇总")]
        public bool? Itemize { get; set; }
    }

    public sealed class CreateScheduleOutput : IReportsAffectedElements
    {
        [McpParam("明细表的视图 ID。可用 revit_read_schedule 读它，或放到图纸上")]
        public string Id { get; set; }

        [McpParam("明细表名")]
        public string Name { get; set; }

        [McpParam("实际加上的字段，按列序")]
        public List<string> Fields { get; set; } = new List<string>();

        [McpParam("表体行数（含列标题那一行）。**只有 1 行说明模型里这个类别一个构件都没有**")]
        public int RowCount { get; set; }

        int IReportsAffectedElements.AffectedElements => 1;
    }

    [McpTool("revit_create_schedule",
        Title = "创建明细表",
        Description = "按类别创建明细表（门窗表、房间面积表这类）。" +
                      "字段名先用 revit_list_schedulable_fields 查，别猜。" +
                      "建好后可以用 revit_read_schedule 读内容，也可以用 " +
                      "revit_add_views_to_sheet 放到图纸上。" +
                      "**回执里的 rowCount 是关键**：只有 1 行（列标题）说明模型里这个类别没有构件。",
        Destructive = false,
        TimeoutSeconds = 120)]
    public sealed class CreateScheduleTool : RevitTool<CreateScheduleInput, CreateScheduleOutput>
    {
        public override CreateScheduleOutput Execute(
            CreateScheduleInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);
            var category = ParseCategory(input.Category);

            if (input.Fields == null || input.Fields.Count == 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "fields 不能为空——一张没有列的明细表没有意义。" +
                    "用 revit_list_schedulable_fields 查 " + category + " 有哪些字段可选。");

            var categoryId = ScheduleSupport.RequireCategoryId(document, category);

            ViewSchedule schedule;
            try
            {
                schedule = ViewSchedule.CreateSchedule(document, categoryId);
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "Revit 拒绝为类别 " + category + " 创建明细表：" + ex.Message +
                    "。有些类别（基准图元、注释符号）不支持做明细表。");
            }

            if (schedule == null)
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "Revit 未能创建明细表，但也没有报错。");

            var definition = schedule.Definition;
            var added = ScheduleSupport.AddFields(document, definition, input.Fields, category, context);

            if (input.Itemize.HasValue)
            {
                try { definition.IsItemized = input.Itemize.Value; }
                catch { context.Warnings.Add("这张明细表不支持切换「逐项显示」，用的是默认设置。"); }
            }

            if (!string.IsNullOrWhiteSpace(input.SortBy))
                ScheduleSupport.ApplySort(definition, added, input.SortBy, input.SortDescending == true, context);

            if (!string.IsNullOrEmpty(input.Name)) ScheduleSupport.SetName(schedule, input.Name, context);

            // 行数要在字段和排序都设完之后读：Revit 需要一次重算才知道表里有什么
            document.Regenerate();

            var rowCount = ScheduleSupport.BodyRowCount(schedule);

            // 建出来不等于有数据——和房间"放置了不等于围上了"是同一类问题。
            // 表结构是对的，只是模型里没东西可统计，这话必须说出来
            if (rowCount <= 1)
                context.Warnings.Add(
                    "明细表建好了，但表体只有 " + rowCount + " 行（第一行是列标题），" +
                    "说明模型里 " + category + " 类别下没有构件。" +
                    "表本身是对的，等模型里有了构件它会自动填上。");

            return new CreateScheduleOutput
            {
                Id = schedule.Id.ToProtocolString(),
                Name = ScheduleSupport.SafeName(schedule),
                Fields = added.Select(f => f.Name).ToList(),
                RowCount = rowCount
            };
        }
    }

    // ==================== 共用零件 ====================

    internal sealed class AddedField
    {
        public string Name;
        public ScheduleFieldId Id;
    }

    internal static class ScheduleSupport
    {
        public static ElementId RequireCategoryId(Document document, BuiltInCategory category)
        {
            var id = Category.GetCategory(document, category)?.Id;

            if (id == null || id == ElementId.InvalidElementId)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "类别 " + category + " 在当前文档里不可用。");

            return id;
        }

        /// <summary>
        /// 问某个类别有哪些可选字段。
        ///
        /// 办法是建一张临时明细表、问完再回滚——`GetSchedulableFields` 挂在 ScheduleDefinition 上，
        /// 而 definition 只有明细表实例才有，**没有任何入口能在不建表的情况下拿到这份清单**。
        ///
        /// 这里自己开 <see cref="Transaction"/> 而不是 <see cref="SubTransaction"/>：
        /// 调用它的是只读工具，管线不给只读工具开事务，而子事务必须活在一个打开的事务里
        /// （实测报错："A sub-transaction can only be active inside an open Transaction"）。
        ///
        /// 只读工具里开事务听着别扭，但"只读"承诺的是**模型不被改变**，而不是"不碰事务"——
        /// finally 里的回滚保证了前者，回滚的事务也不会进用户的撤销栈。
        /// 这是现有标志体系没覆盖到的第四种组合：不受写保护管辖，却需要事务。
        /// </summary>
        public static List<SchedulableFieldInfo> ProbeFields(
            Document document, BuiltInCategory category, string nameContains)
        {
            var categoryId = RequireCategoryId(document, category);

            if (document.IsReadOnly)
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "当前文档是只读的，无法查询明细表字段——" +
                    "这个查询需要在内部建一张临时表再回滚，只读文档开不了事务。");

            var fields = new List<SchedulableFieldInfo>();

            using (var probe = new Transaction(document, "RevitMCP: 查询明细表可用字段"))
            {
                if (probe.Start() != TransactionStatus.Started)
                    throw new ToolFailureException(McpDomainError.TransactionFailed,
                        "无法开启事务来查询可用字段，Revit 可能正处于另一个事务或特殊模式中。");

                try
                {
                    var schedule = ViewSchedule.CreateSchedule(document, categoryId);
                    if (schedule != null) fields = Describe(document, schedule, nameContains);
                }
                catch (ToolFailureException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "类别 " + category + " 不支持做明细表：" + ex.Message);
                }
                finally
                {
                    // 必须回滚，不能提交：这张表只是为了问问题而建的
                    try { if (probe.GetStatus() == TransactionStatus.Started) probe.RollBack(); }
                    catch { }
                }
            }

            return fields;
        }

        private static List<SchedulableFieldInfo> Describe(
            Document document, ViewSchedule schedule, string nameContains)
        {
            var result = new List<SchedulableFieldInfo>();

            foreach (var field in schedule.Definition.GetSchedulableFields())
            {
                var name = SafeFieldName(document, field);
                if (string.IsNullOrEmpty(name)) continue;

                if (!string.IsNullOrWhiteSpace(nameContains) &&
                    name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;

                result.Add(new SchedulableFieldInfo { Name = name, FieldType = SafeFieldType(field) });
            }

            // 标出重名的。不去重——那会藏起一个真实存在的歧义，
            // 让模型以为自己选中的是唯一的那个
            foreach (var group in result.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (group.Count() <= 1) continue;
                foreach (var field in group) field.Duplicate = true;
            }

            return result.OrderBy(f => f.Name, StringComparer.CurrentCulture).ToList();
        }

        /// <summary>
        /// 按名字把字段加进明细表。
        /// 名字对不上时列出相近的候选——模型靠这个纠正，比一句"字段不存在"有用得多。
        /// </summary>
        public static List<AddedField> AddFields(
            Document document, ScheduleDefinition definition, List<string> wanted, BuiltInCategory category,
            ToolExecutionContext<UIApplication> context)
        {
            var available = definition.GetSchedulableFields()
                .Select(f => new { Field = f, Name = SafeFieldName(document, f) })
                .Where(x => !string.IsNullOrEmpty(x.Name))
                .ToList();

            var added = new List<AddedField>();

            foreach (var name in wanted)
            {
                var wantedName = name ?? string.Empty;

                var matches = available
                    .Where(x => string.Equals(x.Name, wantedName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // 重名不是错误，是 Revit 的常态（不同来源的参数可以同名）。
                // 但必须说出来：模型以为自己点的是唯一那个，实际拿到的是第一个
                if (matches.Count > 1)
                    context.Warnings.Add(
                        "字段「" + wantedName + "」在 " + category + " 上有 " + matches.Count +
                        " 个同名的，用了第一个（来源：" + (matches[0].Field == null ? "?" : SafeFieldType(matches[0].Field)) +
                        "）。要精确指定得在 Revit 里手动调整这一列。");

                var match = matches.FirstOrDefault();

                if (match == null)
                {
                    var suggestions = available
                        .Where(x => x.Name.IndexOf(wantedName, StringComparison.OrdinalIgnoreCase) >= 0
                                    || wantedName.IndexOf(x.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                        .Select(x => x.Name)
                        .Take(8)
                        .ToArray();

                    var hint = suggestions.Length > 0
                        ? "。是否想找：" + string.Join("、", suggestions)
                        : "。用 revit_list_schedulable_fields 查 " + category + " 的可用字段";

                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "字段「" + wantedName + "」在 " + category + " 上不可用" + hint + "。明细表未创建。");
                }

                try
                {
                    var field = definition.AddField(match.Field);
                    added.Add(new AddedField { Name = match.Name, Id = field.FieldId });
                }
                catch (Exception ex)
                {
                    throw new ToolFailureException(McpDomainError.TransactionFailed,
                        "字段「" + wantedName + "」加不进这张明细表：" + ex.Message);
                }
            }

            return added;
        }

        /// <summary>
        /// 排序设不上只警告：表已经建好、字段也对，为一个排序把整张表回滚不划算。
        /// 但必须说出来——模型以为排好序了，读出来却是乱的。
        /// </summary>
        public static void ApplySort(
            ScheduleDefinition definition, List<AddedField> added, string sortBy, bool descending,
            ToolExecutionContext<UIApplication> context)
        {
            var target = added.FirstOrDefault(
                f => string.Equals(f.Name, sortBy, StringComparison.OrdinalIgnoreCase));

            if (target == null)
            {
                context.Warnings.Add(
                    "sortBy「" + sortBy + "」不在 fields 里，没有排序。排序字段必须是已显示的列之一。");
                return;
            }

            try
            {
                if (!definition.CanSortByField(target.Id))
                {
                    context.Warnings.Add("字段「" + sortBy + "」不支持排序，明细表按默认顺序排列。");
                    return;
                }

                definition.AddSortGroupField(new ScheduleSortGroupField(
                    target.Id,
                    descending ? ScheduleSortOrder.Descending : ScheduleSortOrder.Ascending));
            }
            catch (Exception ex)
            {
                context.Warnings.Add("按「" + sortBy + "」排序失败（" + ex.Message + "），明细表按默认顺序排列。");
            }
        }

        public static void SetName(
            ViewSchedule schedule, string name, ToolExecutionContext<UIApplication> context)
        {
            try
            {
                schedule.Name = name;
            }
            catch (Exception ex)
            {
                context.Warnings.Add(
                    "明细表名称没能设成「" + name + "」（" + ex.Message + "），用的是 Revit 的默认名。" +
                    "名称可能与已有视图重复。");
            }
        }

        /// <summary>表体行数。读不出来时返回 0——宁可报空，也别报一个编出来的数。</summary>
        public static int BodyRowCount(ViewSchedule schedule)
        {
            try { return schedule.GetTableData().GetSectionData(SectionType.Body).NumberOfRows; }
            catch { return 0; }
        }

        public static string SafeName(ViewSchedule schedule)
        {
            try { return schedule.Name; }
            catch { return null; }
        }

        private static string SafeFieldName(Document document, SchedulableField field)
        {
            try { return field.GetName(document); }
            catch { return null; }
        }

        private static string SafeFieldType(SchedulableField field)
        {
            try { return field.FieldType.ToString(); }
            catch { return null; }
        }
    }
}
