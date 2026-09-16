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
    // ==================== 共用的回执 ====================

    /// <summary>新建的一条基准（标高或轴网）的回执。</summary>
    public sealed class CreatedDatum
    {
        [McpParam("对应入参数组中的下标，从 0 起")]
        public int Index { get; set; }

        [McpParam("新建构件的 ID。建模时把它填进 levelId")]
        public string Id { get; set; }

        [McpParam("最终名称。没指定 name 时是 Revit 自动取的")]
        public string Name { get; set; }

        [McpParam("标高高程，毫米。仅标高有")]
        public double? ElevationMm { get; set; }
    }

    public sealed class CreateDatumsOutput : IReportsAffectedElements
    {
        [McpParam("成功创建的数量")]
        public int Created { get; set; }

        [McpParam("新建的基准，顺序与入参一致")]
        public List<CreatedDatum> Datums { get; set; } = new List<CreatedDatum>();

        int IReportsAffectedElements.AffectedElements => Created;
    }

    // ==================== 标高 ====================

    public sealed class LevelSpec
    {
        [McpParam("高程，毫米。以项目基点的 ±0.000 为零点，可为负（地下室）", Required = true)]
        public double ElevationMm { get; set; }

        [McpParam("标高名，如「屋面」。省略则由 Revit 自动命名（标高 3、标高 4…）。" +
                  "**同名会被 Revit 拒绝**，先用 revit_list_levels 看看已有哪些")]
        public string Name { get; set; }
    }

    public sealed class CreateLevelsInput
    {
        [McpParam("要创建的标高，一次调用可建多条", Required = true)]
        public List<LevelSpec> Levels { get; set; }

        [McpParam("数量超过单次上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    /// <summary>
    /// 创建标高。
    ///
    /// **这是建模的前置条件，不是锦上添花。** 三个 create_* 工具全都吃 levelId，
    /// 而在此之前那个 ID 只能从样板现成的标高里挑——
    /// 项目要一条"屋面"标高，就只能拿"标高 2 + 偏移"硬凑，
    /// 之后想调层高得把所有偏移手算一遍。
    /// </summary>
    [McpTool("revit_create_levels",
        Title = "创建标高",
        Description = "按高程批量创建标高。高程用毫米，以项目基点的 ±0.000 为零点。" +
                      "整批要么全部建成、要么一条都不建，且在撤销栈里只占一步。" +
                      "**标高是建模的骨架**：建墙、建板之前先把标高立好，" +
                      "之后用 levelId 引用，比靠 baseOffset 硬凑高度可靠得多。" +
                      "新建之前先用 revit_list_levels 看看已有哪些，名字重复会被 Revit 拒绝。",
        Destructive = false,
        TimeoutSeconds = 60)]
    public sealed class CreateLevelsTool : RevitTool<CreateLevelsInput, CreateDatumsOutput>
    {
        /// <summary>两条标高高程相差小于这个值就认为是重合，值得提醒。</summary>
        private const double CoincidentToleranceMm = 1.0;

        public override CreateDatumsOutput Execute(
            CreateLevelsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);
            RequireBatch(input.Levels, input.Confirm, context, "创建");

            var existing = ExistingElevations(document);
            var output = new CreateDatumsOutput();
            var total = input.Levels.Count;

            for (var index = 0; index < total; index++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var spec = input.Levels[index];
                if (spec == null)
                    throw CreateSupport.Failure(index, McpDomainError.InvalidParameter, "该项为 null。");

                output.Datums.Add(CreateOne(document, context, spec, index, existing));
                ProgressTicker.Tick(context.Progress, index + 1, total, "已创建");
            }

            output.Created = output.Datums.Count;

            // 新建标高不会自动带出平面视图，这一点和在 Revit 界面里画标高不一样。
            // 不说出来，模型会在 revit_list_views 里找不到它以为建失败了
            context.Warnings.Add(
                "新建的标高**没有对应的平面视图**——Revit 界面里画标高会顺带生成，API 不会。" +
                "这不影响建模（levelId 照常可用），但这些标高在 revit_list_views 里看不到平面。");

            return output;
        }

        private static CreatedDatum CreateOne(
            Document document, ToolExecutionContext<UIApplication> context,
            LevelSpec spec, int index, List<KeyValuePair<string, double>> existing)
        {
            var elevationMm = spec.ElevationMm;

            var clash = existing.FirstOrDefault(
                pair => Math.Abs(pair.Value - elevationMm) < CoincidentToleranceMm);

            if (clash.Key != null)
                CreateSupport.Once(context,
                    "高程 " + Format(elevationMm) + " 毫米上已经有标高「" + clash.Key +
                    "」了。Revit 允许重合的标高，但它们在剖面里会叠在一起——确认这不是重复创建。");

            Level level;
            try
            {
                level = Level.Create(document, Units.ToFeet(elevationMm));
            }
            catch (Exception ex)
            {
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "Revit 拒绝创建高程 " + Format(elevationMm) + " 毫米的标高：" + ex.Message);
            }

            if (level == null)
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "Revit 未能创建标高，但也没有报错。");

            ApplyName(level, spec.Name, index);

            existing.Add(new KeyValuePair<string, double>(CreateSupport.SafeName(level), elevationMm));

            return new CreatedDatum
            {
                Index = index,
                Id = level.Id.ToProtocolString(),
                Name = CreateSupport.SafeName(level),
                ElevationMm = Units.Round(elevationMm)
            };
        }

        /// <summary>
        /// 改名失败要整批回滚，不能只警告。
        ///
        /// 和"标高偏移设不上"不同：偏移设不上，构件还在正确的平面位置；
        /// 而一条叫"标高 5"的标高，模型下一步就会按"屋面"去找它，找不到。
        /// 名字是这里唯一的检索手段。
        /// </summary>
        private static void ApplyName(Level level, string name, int index)
        {
            if (string.IsNullOrWhiteSpace(name)) return;

            var wanted = name.Trim();

            try
            {
                level.Name = wanted;
            }
            catch (Exception ex)
            {
                throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                    "标高改名为「" + wanted + "」失败：" + ex.Message +
                    "。最常见的原因是这个名字已经被占用了——用 revit_list_levels 查一下。");
            }
        }

        private static List<KeyValuePair<string, double>> ExistingElevations(Document document)
        {
            return new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .Select(level => new KeyValuePair<string, double>(
                    CreateSupport.SafeName(level) ?? "(未命名)", Units.FromFeet(level.Elevation)))
                .ToList();
        }

        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    // ==================== 轴网 ====================

    public sealed class GridSpec
    {
        [McpParam("轴线的平面位置，坐标用毫米。Z 会被忽略——" +
                  "轴网是竖直面，平面位置定了就够了", Required = true)]
        public LocationLine LocationLine { get; set; }

        [McpParam("轴号，如「1」「A」。省略则由 Revit 按顺序自动编号。**同名会被 Revit 拒绝**")]
        public string Name { get; set; }
    }

    public sealed class CreateGridsInput
    {
        [McpParam("要创建的轴线，一次调用可建多条", Required = true)]
        public List<GridSpec> Grids { get; set; }

        [McpParam("数量超过单次上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    /// <summary>
    /// 创建轴网。
    ///
    /// 轴网不影响几何正确性——没有它墙照样建得出来。它影响的是**可读性**：
    /// 平面图上没有轴线和轴号，图就不是给人看的图。
    /// 成本与标高几乎一样，所以和标高同批做掉。
    /// </summary>
    [McpTool("revit_create_grids",
        Title = "创建轴网",
        Description = "按定位线批量创建直线轴网。坐标用毫米。" +
                      "整批要么全部建成、要么一条都不建，且在撤销栈里只占一步——" +
                      "一个方向的轴线请一次调用传完。" +
                      "只支持直线轴网；弧线轴网请用户在 Revit 里画。",
        Destructive = false,
        TimeoutSeconds = 60)]
    public sealed class CreateGridsTool : RevitTool<CreateGridsInput, CreateDatumsOutput>
    {
        public override CreateDatumsOutput Execute(
            CreateGridsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);
            RequireBatch(input.Grids, input.Confirm, context, "创建");

            var output = new CreateDatumsOutput();
            var total = input.Grids.Count;

            for (var index = 0; index < total; index++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var spec = input.Grids[index];
                if (spec == null)
                    throw CreateSupport.Failure(index, McpDomainError.InvalidParameter, "该项为 null。");

                output.Datums.Add(CreateOne(document, context, spec, index));
                ProgressTicker.Tick(context.Progress, index + 1, total, "已创建");
            }

            output.Created = output.Datums.Count;
            return output;
        }

        private static CreatedDatum CreateOne(
            Document document, ToolExecutionContext<UIApplication> context, GridSpec spec, int index)
        {
            var line = FlatLine(spec.LocationLine, context, index);

            Grid grid;
            try
            {
                grid = Grid.Create(document, line);
            }
            catch (Exception ex)
            {
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "Revit 拒绝创建该轴线：" + ex.Message);
            }

            if (grid == null)
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "Revit 未能创建轴线，但也没有报错。");

            ApplyName(grid, spec.Name, index);

            return new CreatedDatum
            {
                Index = index,
                Id = grid.Id.ToProtocolString(),
                Name = CreateSupport.SafeName(grid)
            };
        }

        /// <summary>
        /// 轴线必须落在水平面上，Z 一律压平。
        ///
        /// 带着不同 Z 的两点传给 Grid.Create，Revit 回的是一句笼统的
        /// "Curve must be in the horizontal plane"，模型不一定看得懂是自己的 z 写错了。
        /// 先压平再说一声，比转述那句话有用。
        /// </summary>
        private static Line FlatLine(LocationLine location, ToolExecutionContext<UIApplication> context, int index)
        {
            if (location == null)
                throw CreateSupport.Failure(index, McpDomainError.InvalidParameter, "缺少 locationLine。");

            if (location.P0 == null || location.P1 == null)
                throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                    "locationLine 需要 p0 和 p1 两个点。");

            if ((location.P0.Z ?? 0) != 0 || (location.P1.Z ?? 0) != 0)
                CreateSupport.Once(context,
                    "轴线的 Z 坐标已被忽略：轴网是贯通整个模型的竖直面，只需要平面位置。");

            var lengthMm = Math.Sqrt(
                Math.Pow(location.P1.X - location.P0.X, 2) +
                Math.Pow(location.P1.Y - location.P0.Y, 2));

            if (lengthMm < Units.MinLength)
                throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                    "轴线两端在平面上相距 " + lengthMm.ToString("0.###", CultureInfo.InvariantCulture) +
                    " 毫米，太短。两点至少相距 " + Units.MinLength + " 毫米（Z 不计入）。");

            return Line.CreateBound(
                Units.Point(location.P0.X, location.P0.Y, 0),
                Units.Point(location.P1.X, location.P1.Y, 0));
        }

        private static void ApplyName(Grid grid, string name, int index)
        {
            if (string.IsNullOrWhiteSpace(name)) return;

            var wanted = name.Trim();

            try
            {
                grid.Name = wanted;
            }
            catch (Exception ex)
            {
                throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                    "轴线改名为「" + wanted + "」失败：" + ex.Message +
                    "。最常见的原因是这个轴号已经被占用了。");
            }
        }
    }
}
