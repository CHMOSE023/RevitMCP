using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    // ==================== 创建图纸 ====================

    public sealed class SheetSpec
    {
        [McpParam("图纸编号，如 A-101。项目内必须唯一")]
        public string Number { get; set; }

        [McpParam("图纸名称，如「一层平面图」")]
        public string Name { get; set; }

        [McpParam("标题栏类型 ID，来自 revit_list_types 查 OST_TitleBlocks。省略则用项目默认标题栏")]
        public string TitleBlockTypeId { get; set; }
    }

    public sealed class CreateSheetsInput
    {
        [McpParam("要创建的图纸，一次调用可建多张", Required = true)]
        public List<SheetSpec> Elements { get; set; }

        [McpParam("数量超过单次上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    public sealed class CreatedSheet
    {
        [McpParam("对应 elements 数组中的下标，从 0 起")]
        public int Index { get; set; }

        [McpParam("图纸 ID")]
        public string Id { get; set; }

        [McpParam("图纸编号")]
        public string Number { get; set; }

        [McpParam("图纸名称")]
        public string Name { get; set; }

        [McpParam("使用的标题栏类型名；无标题栏时为 null")]
        public string TitleBlock { get; set; }
    }

    public sealed class CreateSheetsOutput : IReportsAffectedElements
    {
        [McpParam("成功创建的图纸数")]
        public int Created { get; set; }

        [McpParam("新建的图纸，顺序与入参一致")]
        public List<CreatedSheet> Elements { get; set; } = new List<CreatedSheet>();

        int IReportsAffectedElements.AffectedElements => Created;
    }

    [McpTool("revit_create_sheets",
        Title = "创建图纸",
        Description = "批量创建图纸。整批要么全部建成、要么一张都不建，且在撤销栈里只占一步。" +
                      "编号在项目内必须唯一，重复会被 Revit 拒绝——建之前可以先用 " +
                      "revit_list_views 查 DrawingSheet 看看已有哪些编号。" +
                      "建好后用 revit_add_views_to_sheet 往上摆视图。",
        Destructive = false,
        TimeoutSeconds = 120)]
    public sealed class CreateSheetsTool : RevitTool<CreateSheetsInput, CreateSheetsOutput>
    {
        public override CreateSheetsOutput Execute(
            CreateSheetsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);
            RequireBatch(input.Elements, input.Confirm, context, "创建");

            var output = new CreateSheetsOutput();
            var total = input.Elements.Count;

            for (var index = 0; index < total; index++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var spec = input.Elements[index];
                if (spec == null)
                    throw CreateSupport.Failure(index, McpDomainError.InvalidParameter, "该项为 null。");

                output.Elements.Add(CreateOne(document, context, spec, index));
                ProgressTicker.Tick(context.Progress, index + 1, total, "已创建");
            }

            output.Created = output.Elements.Count;
            return output;
        }

        private static CreatedSheet CreateOne(
            Document document, ToolExecutionContext<UIApplication> context, SheetSpec spec, int index)
        {
            var titleBlockId = ResolveTitleBlock(document, context, spec.TitleBlockTypeId, index);

            ViewSheet sheet;
            try
            {
                sheet = ViewSheet.Create(document, titleBlockId);
            }
            catch (Exception ex)
            {
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "Revit 拒绝创建图纸：" + ex.Message);
            }

            if (sheet == null)
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "Revit 未能创建图纸，但也没有报错。");

            // 编号要在名称之前设：重复编号会被 Revit 拒绝，先撞上的话后面的活儿都免了
            if (!string.IsNullOrEmpty(spec.Number)) SetNumber(sheet, spec.Number, index);
            if (!string.IsNullOrEmpty(spec.Name)) SetName(sheet, spec.Name, context, index);

            return new CreatedSheet
            {
                Index = index,
                Id = sheet.Id.ToProtocolString(),
                Number = SafeString(() => sheet.SheetNumber),
                Name = SafeString(() => sheet.Name),
                TitleBlock = titleBlockId == ElementId.InvalidElementId
                    ? null
                    : CreateSupport.SafeName(document.GetElement(titleBlockId))
            };
        }

        /// <summary>
        /// 编号重复必须让整批失败，不能降级成警告。
        ///
        /// 图纸编号是图纸的身份，后续"把视图放到 A-101 上"全靠它。
        /// 设不上却继续，模型会拿着一个自以为是 A-101、实际是 A-102 的图纸往下走。
        /// </summary>
        private static void SetNumber(ViewSheet sheet, string number, int index)
        {
            try
            {
                var parameter = sheet.get_Parameter(BuiltInParameter.SHEET_NUMBER);
                if (parameter != null && !parameter.IsReadOnly && parameter.Set(number)) return;
            }
            catch (Exception ex)
            {
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "图纸编号设为「" + number + "」失败：" + ex.Message + "。编号可能与已有图纸重复。");
            }

            throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                "图纸编号设为「" + number + "」被 Revit 拒绝，多半是已有同号图纸。" +
                "可以用 revit_list_views 查 DrawingSheet 看看已经用了哪些编号。");
        }

        /// <summary>名称设不上只警告：图纸还是那张图纸，编号才是身份。</summary>
        private static void SetName(
            ViewSheet sheet, string name, ToolExecutionContext<UIApplication> context, int index)
        {
            try
            {
                var parameter = sheet.get_Parameter(BuiltInParameter.SHEET_NAME);
                if (parameter != null && !parameter.IsReadOnly && parameter.Set(name)) return;
            }
            catch { /* 落到下面的警告 */ }

            CreateSupport.Once(context,
                "elements[" + index + "]：图纸名称没能设成「" + name + "」，用的是 Revit 的默认名。");
        }

        private static ElementId ResolveTitleBlock(
            Document document, ToolExecutionContext<UIApplication> context, string rawId, int index)
        {
            if (!string.IsNullOrWhiteSpace(rawId))
            {
                var element = CreateSupport.RequireElement(document, rawId, index);
                var type = element as FamilySymbol;

                if (type == null || type.Category?.Id.GetValue() != (long)BuiltInCategory.OST_TitleBlocks)
                    throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                        "titleBlockTypeId " + rawId + " 不是标题栏类型。" +
                        "用 revit_list_types 查 OST_TitleBlocks 取 ID。");

                return type.Id;
            }

            var candidates = new FilteredElementCollector(document)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType()
                .ToList();

            // 标题栏类别里不只有图框，还有「修改通知单」这类东西。
            // 直接取第一个是在赌 collector 的顺序——实测同一套工具在两个模板上
            // 分别取到了「A0 公制」和「修改通知单」，后者根本不是图框。
            // 名称以图幅代号开头的优先（A0 公制、A3、A1 metric…），这个特征跨语言都成立。
            var first = candidates.FirstOrDefault(t => LooksLikeSheetFormat(CreateSupport.SafeName(t)))
                        ?? candidates.FirstOrDefault();

            if (first == null)
            {
                // 无标题栏也能建图纸，只是空白一张。说出来，别让用户以为图纸坏了
                CreateSupport.Once(context,
                    "项目里没有载入任何标题栏族，图纸会是空白的（没有图框和标题信息）。" +
                    "需要用户先在 Revit 里载入标题栏族。");
                return ElementId.InvalidElementId;
            }

            CreateSupport.Once(context, "未指定 titleBlockTypeId，使用「" + CreateSupport.SafeName(first) + "」。");
            return first.Id;
        }

        /// <summary>名称以 A0–A4 之类的图幅代号开头。</summary>
        private static bool LooksLikeSheetFormat(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;

            var text = name.TrimStart();
            return text.Length >= 2
                   && (text[0] == 'A' || text[0] == 'a')
                   && text[1] >= '0' && text[1] <= '4';
        }

        private static string SafeString(Func<string> read)
        {
            try { return read(); }
            catch { return null; }
        }
    }

    // ==================== 往图纸上摆视图 ====================

    public sealed class ViewPlacement
    {
        [McpParam("要放置的视图 ID，来自 revit_list_views", Required = true)]
        public string ViewId { get; set; }

        [McpParam("在图纸上的位置，毫米，以图纸左下角为原点。省略则自动排布")]
        public Point3D Position { get; set; }
    }

    public sealed class AddViewsToSheetInput
    {
        [McpParam("目标图纸 ID", Required = true)]
        public string SheetId { get; set; }

        [McpParam("要放上去的视图", Required = true)]
        public List<ViewPlacement> Views { get; set; }

        [McpParam("数量超过单次上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    public sealed class PlacedView
    {
        [McpParam("对应 views 数组中的下标，从 0 起")]
        public int Index { get; set; }

        [McpParam("视口 ID")]
        public string ViewportId { get; set; }

        [McpParam("视图 ID")]
        public string ViewId { get; set; }

        [McpParam("视图名")]
        public string ViewName { get; set; }

        [McpParam("视口中心在图纸上的位置，毫米")]
        public Point3D Position { get; set; }

        [McpParam("视口在图纸上实际占的范围，毫米。由 Revit 放置后回读，不是入参的回声")]
        public BoundingBoxInfo Box { get; set; }
    }

    public sealed class AddViewsToSheetOutput : IReportsAffectedElements
    {
        [McpParam("成功放置的视图数")]
        public int Placed { get; set; }

        [McpParam("图纸编号")]
        public string SheetNumber { get; set; }

        [McpParam("图纸自身的可用范围，毫米。用来判断视口有没有摆到纸外面去")]
        public BoundingBoxInfo SheetOutline { get; set; }

        [McpParam("放置结果，顺序与入参一致")]
        public List<PlacedView> Views { get; set; } = new List<PlacedView>();

        int IReportsAffectedElements.AffectedElements => Placed;
    }

    [McpTool("revit_add_views_to_sheet",
        Title = "把视图放到图纸上",
        Description = "把一批视图放到指定图纸上。整批要么全放成、要么一个都不放。" +
                      "**一个视图只能放在一张图纸上**——已经放过的会被拒绝，" +
                      "可以用 revit_list_views 看 sheetId 是否为空来确认。" +
                      "省略 position 时按网格自动排布，位置未必好看，要精确摆放就显式给坐标。" +
                      "明细表是例外：它可以同时放在多张图纸上。",
        TimeoutSeconds = 120)]
    public sealed class AddViewsToSheetTool : RevitTool<AddViewsToSheetInput, AddViewsToSheetOutput>
    {
        /// <summary>取不到图纸范围时的退路，毫米。按 A1 图纸估的。</summary>
        private const double FallbackX = 200;
        private const double FallbackY = 400;

        private const int AutoColumns = 2;

        public override AddViewsToSheetOutput Execute(
            AddViewsToSheetInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);
            RequireBatch(input.Views, input.Confirm, context, "放置");

            var sheetElement = RequireElement(document, input.SheetId);
            var sheet = sheetElement as ViewSheet;

            if (sheet == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "sheetId " + input.SheetId + " 不是图纸。用 revit_list_views 查 DrawingSheet 取图纸 ID。");

            var placement = ListViewsTool.MapViewsToSheets(document);

            var sheetBox = OutlineOf(sheet);
            var output = new AddViewsToSheetOutput
            {
                SheetNumber = SafeNumber(sheet),
                SheetOutline = sheetBox
            };
            var total = input.Views.Count;

            for (var index = 0; index < total; index++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var spec = input.Views[index];
                if (spec == null)
                    throw CreateSupport.Failure(index, McpDomainError.InvalidParameter, "该项为 null。");

                output.Views.Add(PlaceOne(document, sheet, spec, index, total, placement));
                ProgressTicker.Tick(context.Progress, index + 1, total, "已放置");
            }

            output.Placed = output.Views.Count;

            // 放完回头量一遍：视口的**整个外框**在不在纸上、彼此有没有压在一起。
            // Viewport.Create 对纸外的坐标照收不误，图纸导出来却缺内容——
            // 每一步都"成功"，产物却不能用
            foreach (var placed in output.Views)
            {
                var overflow = DescribeOverflow(placed.Box, sheetBox);
                if (overflow == null) continue;

                context.Warnings.Add(
                    "视口「" + placed.ViewName + "」有部分超出图纸范围（" + overflow +
                    "），出图时这部分看不到。显式给 position 重放一次，或把视图比例调小。");
            }

            WarnOverlaps(output.Views, context);

            return output;
        }

        private static PlacedView PlaceOne(
            Document document, ViewSheet sheet, ViewPlacement spec, int index, int total,
            Dictionary<long, ElementId> placement)
        {
            var element = CreateSupport.RequireElement(document, spec.ViewId, index);
            var view = element as View;

            if (view == null)
                throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                    "viewId " + spec.ViewId + " 不是视图。");

            if (view.IsTemplate)
                throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                    "「" + view.Name + "」是视图样板，不能放到图纸上。");

            if (view.ViewType == ViewType.DrawingSheet)
                throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                    "「" + view.Name + "」本身就是一张图纸，不能放到另一张图纸上。");

            var position = spec.Position ?? AutoPosition(sheet, index, total);
            var point = Units.Point(position.X, position.Y, 0);

            // 明细表走的是另一套 API，而且**可以放到多张图纸上**，
            // 所以下面那条"一个视图只能放一张图纸"的检查对它不适用
            var schedule = view as ViewSchedule;
            if (schedule != null) return PlaceSchedule(document, sheet, schedule, point, position, index);

            // 先查已放置：Viewport.Create 遇到这种情况只会返回 null，
            // 那时再报错就只能说"创建失败"，说不出"它已经在 A-101 上了"
            ElementId existing;
            if (placement.TryGetValue(view.Id.GetValue(), out existing))
                throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                    "视图「" + view.Name + "」已经放在图纸「" +
                    (ListViewsTool.SheetNumberOf(document, existing) ?? existing.ToProtocolString()) +
                    "」上了。一个视图只能放在一张图纸上——要挪位置得先从原图纸上删掉视口。");

            Viewport viewport;
            try
            {
                viewport = Viewport.Create(document, sheet.Id, view.Id, point);
            }
            catch (Exception ex)
            {
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "Revit 拒绝把「" + view.Name + "」放到图纸上：" + ex.Message);
            }

            if (viewport == null)
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "Revit 未能放置「" + view.Name + "」，但也没有报错。" +
                    "该视图类型可能不支持放到图纸上（如明细表要用另外的方式）。");

            // 放进去之后再登记，免得同一批里重复放同一个视图
            placement[view.Id.GetValue()] = sheet.Id;

            return new PlacedView
            {
                Index = index,
                ViewportId = viewport.Id.ToProtocolString(),
                ViewId = view.Id.ToProtocolString(),
                ViewName = SafeName(view),
                Position = position,
                Box = BoxOf(viewport)
            };
        }

        /// <summary>
        /// 自动排布。**图纸有多大，问图纸自己**——
        /// 原来用的是按 A1 估的固定坐标，碰上 A4 的小图幅，视口就摆到纸外面去了。
        /// 标题栏决定图纸尺寸，而标题栏是用户选的，猜不得。
        /// </summary>
        /// <summary>
        /// 明细表上图纸用 <c>ScheduleSheetInstance</c>，不是 <c>Viewport</c>。
        /// 这不是可以抹平的差异：明细表能同时出现在多张图纸上，视图不能。
        /// </summary>
        private static PlacedView PlaceSchedule(
            Document document, ViewSheet sheet, ViewSchedule schedule, XYZ point, Point3D position, int index)
        {
            ScheduleSheetInstance instance;
            try
            {
                instance = ScheduleSheetInstance.Create(document, sheet.Id, schedule.Id, point);
            }
            catch (Exception ex)
            {
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "Revit 拒绝把明细表「" + SafeName(schedule) + "」放到图纸上：" + ex.Message);
            }

            if (instance == null)
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "Revit 未能放置明细表「" + SafeName(schedule) + "」，但也没有报错。");

            return new PlacedView
            {
                Index = index,
                ViewportId = instance.Id.ToProtocolString(),
                ViewId = schedule.Id.ToProtocolString(),
                ViewName = SafeName(schedule),
                Position = position,
                Box = ScheduleBoxOf(document, sheet, instance)
            };
        }

        /// <summary>
        /// 明细表在图纸上的实际范围。
        ///
        /// 明细表实例没有 <c>GetBoxOutline</c>，尺寸由表格内容决定——但它有包围盒，
        /// 只是要先 <c>Regenerate</c> 才算得出来。以前这里直接返回 null，
        /// 于是明细表连"在不在纸上"的检查都绕过了：一张越界的明细表，回执里看不出任何异常。
        /// </summary>
        private static BoundingBoxInfo ScheduleBoxOf(
            Document document, ViewSheet sheet, ScheduleSheetInstance instance)
        {
            try
            {
                document.Regenerate();

                var box = instance.get_BoundingBox(sheet);
                if (box?.Min == null || box.Max == null) return null;

                return ToBox(
                    Units.FromFeet(box.Min.X), Units.FromFeet(box.Min.Y),
                    Units.FromFeet(box.Max.X), Units.FromFeet(box.Max.Y));
            }
            catch { return null; }
        }

        private static Point3D AutoPosition(ViewSheet sheet, int index, int total)
        {
            BoundingBoxUV outline = null;
            try { outline = sheet.Outline; }
            catch { /* 退到下面的固定坐标 */ }

            if (outline?.Min == null || outline.Max == null)
                return new Point3D { X = FallbackX, Y = FallbackY };

            var minX = Units.FromFeet(outline.Min.U);
            var minY = Units.FromFeet(outline.Min.V);
            var width = Units.FromFeet(outline.Max.U) - minX;
            var height = Units.FromFeet(outline.Max.V) - minY;

            // 右侧通常是标题栏，往左让出四分之一；上下各留一点边
            var usableWidth = width * 0.72;
            var rows = Math.Max(1, (total + AutoColumns - 1) / AutoColumns);
            var columns = Math.Min(AutoColumns, Math.Max(1, total));

            var cellWidth = usableWidth / columns;
            var cellHeight = height * 0.86 / rows;

            var column = index % AutoColumns;
            var row = index / AutoColumns;

            return new Point3D
            {
                X = Units.Round(minX + width * 0.04 + cellWidth * (column + 0.5)),
                Y = Units.Round(minY + height * 0.93 - cellHeight * (row + 0.5))
            };
        }

        /// <summary>视口放置后的实际范围。<c>get_BoundingBox</c> 对视口返回 null，只能问视口自己。</summary>
        private static BoundingBoxInfo BoxOf(Viewport viewport)
        {
            try { return ToBox(viewport.GetBoxOutline()); }
            catch { return null; }
        }

        private static BoundingBoxInfo OutlineOf(ViewSheet sheet)
        {
            try
            {
                var outline = sheet.Outline;
                if (outline?.Min == null || outline.Max == null) return null;

                return ToBox(
                    Units.FromFeet(outline.Min.U), Units.FromFeet(outline.Min.V),
                    Units.FromFeet(outline.Max.U), Units.FromFeet(outline.Max.V));
            }
            catch
            {
                return null;
            }
        }

        private static BoundingBoxInfo ToBox(Outline outline)
        {
            if (outline == null) return null;

            var min = outline.MinimumPoint;
            var max = outline.MaximumPoint;

            return ToBox(
                Units.FromFeet(min.X), Units.FromFeet(min.Y),
                Units.FromFeet(max.X), Units.FromFeet(max.Y));
        }

        private static BoundingBoxInfo ToBox(double minX, double minY, double maxX, double maxY)
        {
            return new BoundingBoxInfo
            {
                Min = new Point3D { X = Units.Round(minX), Y = Units.Round(minY), Z = 0 },
                Max = new Point3D { X = Units.Round(maxX), Y = Units.Round(maxY), Z = 0 },
                Center = new Point3D
                {
                    X = Units.Round((minX + maxX) / 2),
                    Y = Units.Round((minY + maxY) / 2),
                    Z = 0
                },
                SizeXMm = Units.Round(maxX - minX),
                SizeYMm = Units.Round(maxY - minY),
                SizeZMm = 0
            };
        }

        /// <summary>
        /// 视口越出图纸多少。判断不了时返回 null（不乱报警）。
        ///
        /// **量的是整个外框，不是中心点。** 只看中心点的话，一个比图纸还大的视口
        /// 只要中心落在纸内就被判为"没问题"——实测放一张未裁剪的三维视图，
        /// 右边越界 63 毫米、下边越界 31 毫米，中心点检查一声不吭。
        /// </summary>
        private static string DescribeOverflow(BoundingBoxInfo box, BoundingBoxInfo sheet)
        {
            if (box?.Min == null || box.Max == null || sheet?.Min == null || sheet.Max == null) return null;

            var parts = new List<string>();

            if (sheet.Min.X - box.Min.X > EdgeToleranceMm) parts.Add("左 " + Amount(sheet.Min.X - box.Min.X));
            if (box.Max.X - sheet.Max.X > EdgeToleranceMm) parts.Add("右 " + Amount(box.Max.X - sheet.Max.X));
            if (sheet.Min.Y - box.Min.Y > EdgeToleranceMm) parts.Add("下 " + Amount(sheet.Min.Y - box.Min.Y));
            if (box.Max.Y - sheet.Max.Y > EdgeToleranceMm) parts.Add("上 " + Amount(box.Max.Y - sheet.Max.Y));

            return parts.Count == 0 ? null : string.Join("、", parts);
        }

        /// <summary>
        /// 两两之间压没压上。
        ///
        /// 这里只报视口之间的重叠，不报"压住标题栏"——标题栏族的包围盒通常就是整张图纸，
        /// 拿它当禁区会把每一个视口都判成违规。真要管标题栏，需要一份可配置的图面布局区，
        /// 那是另一件事，不能靠猜。
        /// </summary>
        private static void WarnOverlaps(List<PlacedView> views, ToolExecutionContext<UIApplication> context)
        {
            for (var i = 0; i < views.Count; i++)
            {
                for (var j = i + 1; j < views.Count; j++)
                {
                    if (!Overlaps(views[i].Box, views[j].Box)) continue;

                    context.Warnings.Add(
                        "视口「" + views[i].ViewName + "」和「" + views[j].ViewName +
                        "」在图纸上互相重叠，打印出来会压在一起。");
                }
            }
        }

        private static bool Overlaps(BoundingBoxInfo a, BoundingBoxInfo b)
        {
            if (a?.Min == null || a.Max == null || b?.Min == null || b.Max == null) return false;

            return a.Min.X < b.Max.X - EdgeToleranceMm && b.Min.X < a.Max.X - EdgeToleranceMm &&
                   a.Min.Y < b.Max.Y - EdgeToleranceMm && b.Min.Y < a.Max.Y - EdgeToleranceMm;
        }

        private static string Amount(double millimeters)
        {
            return Units.Round(millimeters).ToString("0.#", CultureInfo.InvariantCulture) + " 毫米";
        }

        /// <summary>图面上 1 毫米以内的出入不值得报警：视口外框自带标题与边线，本来就不是精确到丝的东西。</summary>
        private const double EdgeToleranceMm = 1.0;

        private static string SafeNumber(ViewSheet sheet)
        {
            try { return sheet.SheetNumber; }
            catch { return null; }
        }

        private static string SafeName(View view)
        {
            try { return view.Name; }
            catch { return null; }
        }
    }
}
