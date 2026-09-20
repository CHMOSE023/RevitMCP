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
    /// <summary>
    /// 本文件的失败构造：把下标前缀写成 <c>datums[i]</c>，而不是通用的 elements[i]。
    ///
    /// 报错里出现的数组名必须是调用方真的传过的那个，否则它会去找一个不存在的参数。
    /// </summary>
    internal static class DatumFail
    {
        public static ToolFailureException At(int index, string code, string message)
        {
            return CreateSupport.Failure(index, code, message, "datums", "整批未创建");
        }
    }
    public sealed class DatumSpec
    {
        [McpParam("基准种类：grid（轴网）、level（标高）", Required = true,
                  AllowedValues = new[] { "grid", "level" })]
        public string Kind { get; set; }

        [McpParam("名称。轴网就是轴号（「1」「A」这种），标高就是标高名。" +
                  "省略则由 Revit 按现有命名规律自动续号")]
        public string Name { get; set; }

        [McpParam("轴线的两端，毫米。kind 为 grid 时必填。" +
                  "轴线是直线；要弧形轴网请另外给 arcPoint")]
        public LocationLine LocationLine { get; set; }

        [McpParam("弧形轴网经过的第三点，毫米。给了它，轴线就是过 p0、这个点、p1 的圆弧")]
        public Point3D ArcPoint { get; set; }

        [McpParam("标高高程，毫米，相对项目基点。kind 为 level 时必填")]
        public double? ElevationMm { get; set; }

        [McpParam("创建标高时是否同时建一个对应的楼层平面视图，默认 true。" +
                  "**Level.Create 本身不建视图**——只建标高不建平面，" +
                  "用户在项目浏览器里会找不到它")]
        public bool? CreatePlanView { get; set; }
    }

    public sealed class CreateDatumsInput
    {
        [McpParam("要创建的基准图元，一次调用可建多个", Required = true)]
        public List<DatumSpec> Datums { get; set; }

        [McpParam("数量超过单次上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    public sealed class CreatedDatum
    {
        [McpParam("对应 datums 数组中的下标，从 0 起")]
        public int Index { get; set; }

        [McpParam("新建图元的 ID")]
        public string Id { get; set; }

        [McpParam("种类：grid / level")]
        public string Kind { get; set; }

        [McpParam("实际的名称（重名时会与请求的不同）")]
        public string Name { get; set; }

        [McpParam("标高高程，毫米。仅 level 有值")]
        public double? ElevationMm { get; set; }

        [McpParam("一同创建的楼层平面视图 ID。仅 level 且 createPlanView 为 true 时有值")]
        public string PlanViewId { get; set; }
    }

    public sealed class CreateDatumsOutput : IReportsAffectedElements
    {
        [McpParam("成功创建的基准图元数")]
        public int Created { get; set; }

        [McpParam("新建的基准图元，顺序与入参一致")]
        public List<CreatedDatum> Datums { get; set; } = new List<CreatedDatum>();

        int IReportsAffectedElements.AffectedElements => Created;
    }

    /// <summary>
    /// 创建轴网与标高。
    ///
    /// 两者合成一个工具：它们在 Revit 里同属"基准图元"（<c>DatumPlane</c>），
    /// 都是整个模型的定位骨架，都会被后续建模大量引用。
    ///
    /// 一个容易踩的坑写在这里：<c>Level.Create</c> **只建标高，不建平面视图**。
    /// 用户在项目浏览器里看不到新标高，会以为没建成功。
    /// 所以默认顺手把楼层平面也建出来。
    /// </summary>
    [McpTool("revit_create_datums",
        Title = "创建轴网与标高",
        Description = "批量创建轴网或标高。坐标与高程一律用毫米。" +
                      "**创建标高时默认同时建一个楼层平面视图**——" +
                      "Revit 的 Level.Create 本身不建视图，只建标高的话用户在项目浏览器里找不到它。" +
                      "轴网默认是直线；给了 arcPoint 就是过三点的圆弧轴。" +
                      "整批要么全成、要么一个都不建，且在撤销栈里只占一步。",
        Destructive = false,
        TimeoutSeconds = 120)]
    public sealed class CreateDatumsTool : RevitTool<CreateDatumsInput, CreateDatumsOutput>
    {
        public override CreateDatumsOutput Execute(
            CreateDatumsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);
            RequireBatch(input.Datums, input.Confirm, context, "创建");

            var output = new CreateDatumsOutput();
            var total = input.Datums.Count;

            for (var index = 0; index < total; index++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var spec = input.Datums[index];
                if (spec == null)
                    throw DatumFail.At(index, McpDomainError.InvalidParameter, "该项为 null。");

                var kind = (spec.Kind ?? string.Empty).Trim().ToLowerInvariant();

                switch (kind)
                {
                    case "grid":
                        output.Datums.Add(CreateGrid(document, context, spec, index));
                        break;

                    case "level":
                        output.Datums.Add(CreateLevel(document, context, spec, index));
                        break;

                    default:
                        throw DatumFail.At(index, McpDomainError.InvalidParameter,
                            "无法识别的 kind \"" + spec.Kind + "\"。可用值：grid（轴网）、level（标高）。");
                }

                ProgressTicker.Tick(context.Progress, index + 1, total, "已创建");
            }

            output.Created = output.Datums.Count;
            return output;
        }

        // ==================== 轴网 ====================

        private static CreatedDatum CreateGrid(
            Document document, ToolExecutionContext<UIApplication> context, DatumSpec spec, int index)
        {
            var line = CreateSupport.RequireLine(spec.LocationLine, index);

            Grid grid;
            try
            {
                grid = spec.ArcPoint == null
                    ? Grid.Create(document, line)
                    : Grid.Create(document, BuildArc(spec, line, index));
            }
            catch (ToolFailureException) { throw; }
            catch (Exception ex)
            {
                throw DatumFail.At(index, McpDomainError.TransactionFailed,
                    "Revit 拒绝创建轴网：" + ex.Message +
                    "。轴线必须画在水平面上——两端的 z 不一致时 Revit 会拒绝。");
            }

            if (grid == null)
                throw DatumFail.At(index, McpDomainError.TransactionFailed,
                    "Revit 未能创建轴网，但也没有报错。");

            var result = new CreatedDatum { Index = index, Id = grid.Id.ToProtocolString(), Kind = "grid" };
            Rename(grid, spec.Name, index, context, "轴号");
            result.Name = AnnotationSupport.SafeName(grid);

            return result;
        }

        /// <summary>
        /// 由两端点加一个中间点构造圆弧轴线。
        /// 三点共线时 <c>Arc.Create</c> 会抛一句很难懂的话，提前拦下来。
        /// </summary>
        private static Arc BuildArc(DatumSpec spec, Line line, int index)
        {
            var p0 = line.GetEndPoint(0);
            var p1 = line.GetEndPoint(1);
            var middle = spec.ArcPoint.ToXyz();

            // 到直线的距离太小就是三点共线。1 毫米取自 Units.MinLength：
            // 比这更近的偏移，Revit 也画不出一条有意义的弧
            var direction = (p1 - p0).Normalize();
            var offset = (middle - p0) - direction * (middle - p0).DotProduct(direction);

            if (offset.GetLength() < Units.ToFeet(Units.MinLength))
                throw DatumFail.At(index, McpDomainError.InvalidParameter,
                    "arcPoint 落在 locationLine 上（三点共线），构不成圆弧。" +
                    "要直线轴网就别给 arcPoint。");

            try
            {
                return Arc.Create(p0, p1, middle);
            }
            catch (Exception ex)
            {
                throw DatumFail.At(index, McpDomainError.InvalidParameter,
                    "由三点构造圆弧失败：" + ex.Message);
            }
        }

        // ==================== 标高 ====================

        private static CreatedDatum CreateLevel(
            Document document, ToolExecutionContext<UIApplication> context, DatumSpec spec, int index)
        {
            if (spec.ElevationMm == null)
                throw DatumFail.At(index, McpDomainError.InvalidParameter,
                    "kind 为 level 时必须给 elevationMm（高程，毫米）。");

            var elevation = Units.ToFeet(spec.ElevationMm.Value);

            // 同高程已有标高时 Revit 会照建不误，得到两个在剖面里叠在一起的标高。
            //
            // 刻意只警告、不拒绝：Revit 本身允许这么做，确实也有正当用法
            // （同一高程上分建筑标高与结构标高）。但**必须点名已经在那儿的是谁**——
            // 光说"高程重复"，调用方无从判断这是不是一次重复创建。
            var duplicate = new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(l => Math.Abs(l.Elevation - elevation) < Units.ToFeet(Units.MinLength));

            if (duplicate != null)
                CreateSupport.Once(context,
                    "高程 " + Format(spec.ElevationMm.Value) + " 毫米上已经有标高「" +
                    AnnotationSupport.SafeName(duplicate) + "」（ID " +
                    duplicate.Id.ToProtocolString() + "）了。" +
                    "Revit 允许重合的标高，但它们在剖面里会叠在一起——确认这不是重复创建。");

            Level level;
            try
            {
                level = Level.Create(document, elevation);
            }
            catch (Exception ex)
            {
                throw DatumFail.At(index, McpDomainError.TransactionFailed,
                    "Revit 拒绝创建标高：" + ex.Message);
            }

            if (level == null)
                throw DatumFail.At(index, McpDomainError.TransactionFailed,
                    "Revit 未能创建标高，但也没有报错。");

            var result = new CreatedDatum
            {
                Index = index,
                Id = level.Id.ToProtocolString(),
                Kind = "level",
                ElevationMm = Units.Round(Units.FromFeet(level.Elevation))
            };

            Rename(level, spec.Name, index, context, "标高名");
            result.Name = AnnotationSupport.SafeName(level);

            if (spec.CreatePlanView ?? true)
                result.PlanViewId = CreatePlanView(document, level, index, context);

            return result;
        }

        private static string CreatePlanView(
            Document document, Level level, int index, ToolExecutionContext<UIApplication> context)
        {
            ViewFamilyType familyType;
            try
            {
                familyType = CreateViewsTool.ResolveViewFamilyType(
                    document, null, ViewFamily.FloorPlan, index);
            }
            catch (ToolFailureException)
            {
                // 项目里没有楼层平面的视图族类型。标高本身已经建好了，
                // 为这个回滚掉不划算——说出来让调用方决定
                CreateSupport.Once(context,
                    "datums[" + index + "]：标高已创建，但项目里没有楼层平面的视图族类型，" +
                    "没能一并建出平面视图。");
                return null;
            }

            try
            {
                var plan = ViewPlan.Create(document, familyType.Id, level.Id);
                return plan?.Id.ToProtocolString();
            }
            catch (Exception ex)
            {
                CreateSupport.Once(context,
                    "datums[" + index + "]：标高已创建，但楼层平面没建成：" + ex.Message);
                return null;
            }
        }

        // ==================== 共用 ====================

        /// <summary>
        /// 改名。**失败要整批回滚，不能只警告。**
        ///
        /// 和"标高偏移设不上"不同：偏移设不上，构件还在正确的位置上；
        /// 而一条本该叫「屋面」、实际叫「标高 5」的标高，调用方下一步就会按「屋面」
        /// 去找它，然后找不到。名字是基准图元唯一的检索手段——
        /// 悄悄用一个别的名字建出来，比建不出来更难查。
        /// </summary>
        private static void Rename(
            Element element, string wanted, int index,
            ToolExecutionContext<UIApplication> context, string label)
        {
            if (string.IsNullOrWhiteSpace(wanted)) return;

            var name = wanted.Trim();

            try
            {
                element.Name = name;
            }
            catch (Exception ex)
            {
                throw DatumFail.At(index, McpDomainError.InvalidParameter,
                    label + "改成「" + name + "」失败：" + ex.Message +
                    "。最常见的原因是这个名字已经被占用了——" +
                    "标高用 revit_list_levels、轴网用 revit_query_elements 查 OST_Grids 确认。");
            }
        }

        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
