using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    public sealed class SurfaceBoundary
    {
        [McpParam("外轮廓，首尾相接的线段数组，至少 3 段。最后一段的终点必须回到第一段的起点", Required = true)]
        public List<LocationLine> OuterLoop { get; set; }

        [McpParam("内环（洞口），每个内环的写法和 outerLoop 一样：首尾相接、至少 3 段。" +
                  "梯井、管井、天井用它——**不要靠把楼板拆成几块来留洞**，" +
                  "那样会留下一堆「楼板重叠」警告，而且洞的位置一改就得重建所有板")]
        public List<List<LocationLine>> InnerLoops { get; set; }
    }

    public sealed class SurfaceBasedElementSpec
    {
        [McpParam("BuiltInCategory 名。支持 OST_Floors（楼板）、OST_Roofs（屋顶）、" +
                  "OST_Ceilings（天花，需要 Revit 2022 及以上）", Required = true)]
        public string Category { get; set; }

        [McpParam("族类型 ID，来自 revit_list_types。省略则用该类别的默认类型。" +
                  "板厚由类型决定，改不了——要 150 厚的板就挑 thicknessMm 为 150 的类型")]
        public string TypeId { get; set; }

        [McpParam("边界，坐标用毫米。Z 坐标会被忽略，竖向位置由 levelId 与 baseOffset 决定", Required = true)]
        public SurfaceBoundary Boundary { get; set; }

        [McpParam("标高 ID，来自 revit_list_levels。省略则用活动视图所在标高")]
        public string LevelId { get; set; }

        [McpParam("相对标高的偏移，毫米，默认 0")]
        public double? BaseOffset { get; set; }

        [McpParam("是否为结构板。仅楼板使用，默认 false")]
        public bool? Structural { get; set; }
    }

    public sealed class CreateSurfaceBasedInput
    {
        [McpParam("要创建的构件，一次调用可建多个", Required = true)]
        public List<SurfaceBasedElementSpec> Elements { get; set; }

        [McpParam("数量超过单次上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    [McpTool("revit_create_surface_based_elements",
        Title = "创建面定位构件",
        Description = "按闭合边界批量创建楼板、屋顶或天花。坐标一律用毫米。" +
                      "整批要么全部建成、要么一个都不建，且在撤销栈里只占一步。" +
                      "边界必须首尾相接形成闭合环；不闭合会被直接拒绝，并告诉你断在哪一段。" +
                      "**要留洞（梯井、管井、天井）就用 boundary.innerLoops**，" +
                      "不要把一块板拆成几块去绕开——那样会留下一串「楼板重叠」警告。",
        Destructive = false,
        TimeoutSeconds = 120)]
    public sealed class CreateSurfaceBasedTool : RevitTool<CreateSurfaceBasedInput, CreateElementsOutput>
    {
        /// <summary>边界闭合判定的容差。比 Revit 自己的短曲线下限略松，够容纳浮点误差又不至于放过真的缺口。</summary>
        private const double ClosureToleranceMm = 1.0;

        private const int MinSegments = 3;

        public override CreateElementsOutput Execute(
            CreateSurfaceBasedInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);
            RequireBatch(input.Elements, input.Confirm, context, "创建");

            var output = new CreateElementsOutput();
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

        private static CreatedElement CreateOne(
            Document document, ToolExecutionContext<UIApplication> context,
            SurfaceBasedElementSpec spec, int index)
        {
            var category = ParseCategoryAt(spec.Category, index);
            var level = CreateSupport.ResolveLevel(document, context, spec.LevelId, index);
            var boundary = BuildBoundary(spec.Boundary, context, index);
            var innerLoops = BuildInnerLoops(spec.Boundary, context, index);
            var offsetMm = spec.BaseOffset ?? 0;

            SurfaceCompat.CreateSurfaceResult result;
            ElementType usedType;
            BuiltInParameter offsetParameter;

            switch (category)
            {
                case BuiltInCategory.OST_Floors:
                    usedType = CreateSupport.ResolveType<FloorType>(
                        document, context, spec.TypeId, BuiltInCategory.OST_Floors, index);
                    result = Invoke(index, "楼板", () => SurfaceCompat.CreateFloor(
                        document, boundary, innerLoops, usedType, level, spec.Structural ?? false));
                    offsetParameter = BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM;
                    break;

                case BuiltInCategory.OST_Roofs:
                    usedType = CreateSupport.ResolveType<RoofType>(
                        document, context, spec.TypeId, BuiltInCategory.OST_Roofs, index);
                    result = Invoke(index, "屋顶", () => SurfaceCompat.CreateRoof(
                        document, boundary, innerLoops, (RoofType)usedType, level));
                    offsetParameter = BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM;
                    break;

                case BuiltInCategory.OST_Ceilings:
                    if (!SurfaceCompat.CanCreateCeiling)
                        throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                            "Revit " + RevitVersionInfo.Year + " 的 API 不提供创建天花的入口" +
                            "（2022 才加入）。本版本上天花只能由用户在 Revit 界面里画。");

                    usedType = CreateSupport.ResolveType<ElementType>(
                        document, context, spec.TypeId, BuiltInCategory.OST_Ceilings, index);
                    result = Invoke(index, "天花", () => SurfaceCompat.CreateCeiling(
                        document, boundary, innerLoops, usedType, level));
                    offsetParameter = BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM;
                    break;

                default:
                    throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                        "面定位建模只支持 OST_Floors、OST_Roofs、OST_Ceilings，收到 " + category + "。");
            }

            var actualOffsetMm = EnforceOffset(result.Element, offsetParameter, offsetMm, index);

            // 洞是轮廓自带的还是事后开的，在后续查询里是两种东西：
            // 后者会多出 Opening 构件，按类别查楼板时看不见它们
            if (result.OpeningsCreated > 0)
                CreateSupport.Once(context,
                    "本版本的 API 不支持带洞轮廓，" + result.OpeningsCreated +
                    " 个洞是建完之后单独开的，模型里会多出同样数量的「洞口」构件。" +
                    "删除宿主时它们会跟着走，但按类别查询时不会出现在楼板/屋顶里。");

            var levelElevationMm = Units.Round(Units.FromFeet(level.Elevation));

            return new CreatedElement
            {
                Index = index,
                Id = result.Element.Id.ToProtocolString(),
                Category = category.ToString(),
                Type = CreateSupport.SafeName(usedType),
                Level = CreateSupport.SafeName(level),
                LevelElevationMm = levelElevationMm,
                BaseOffsetMm = actualOffsetMm,
                ElevationMm = actualOffsetMm.HasValue
                    ? (double?)Units.Round(levelElevationMm + actualOffsetMm.Value)
                    : null
            };
        }

        /// <summary>
        /// 把线段数组变成 Revit 能接受的边界，顺便替 Revit 把话说清楚。
        ///
        /// 不闭合的边界 Revit 只回一句笼统的失败，模型拿着没法改。
        /// 自己先查一遍，就能指出"断在哪两段之间、差了多少毫米"——
        /// 这正是模型改得动的那种信息。
        /// </summary>
        private static List<Curve> BuildBoundary(
            SurfaceBoundary boundary, ToolExecutionContext<UIApplication> context, int index)
        {
            if (boundary?.OuterLoop == null || boundary.OuterLoop.Count == 0)
                throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                    "缺少 boundary.outerLoop。");

            return BuildLoop(boundary.OuterLoop, "outerLoop", context, index);
        }

        /// <summary>
        /// 内环就是洞。校验规则和外轮廓**完全一样**，所以走同一段代码——
        /// 两套校验迟早会长歪：一边容忍 1 毫米的缺口另一边不容忍，
        /// 这种差异没人查得出来。
        /// </summary>
        private static List<IList<Curve>> BuildInnerLoops(
            SurfaceBoundary boundary, ToolExecutionContext<UIApplication> context, int index)
        {
            var loops = new List<IList<Curve>>();
            if (boundary?.InnerLoops == null || boundary.InnerLoops.Count == 0) return loops;

            for (var i = 0; i < boundary.InnerLoops.Count; i++)
            {
                var segments = boundary.InnerLoops[i];
                if (segments == null || segments.Count == 0)
                    throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                        "boundary.innerLoops[" + i + "] 是空的。不需要洞就别给这一项。");

                loops.Add(BuildLoop(segments, "innerLoops[" + i + "]", context, index));
            }

            return loops;
        }

        /// <param name="label">出错时指回入参的路径。批量创建是全有全无的，
        /// 模型必须知道是哪个环的哪一段出的问题。</param>
        private static List<Curve> BuildLoop(
            List<LocationLine> segments, string label,
            ToolExecutionContext<UIApplication> context, int index)
        {
            if (segments.Count < MinSegments)
                throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                    "boundary." + label + " 至少要有 " + MinSegments + " 段才能围成一个环，收到 " +
                    segments.Count + " 段。");

            var flattened = false;
            var curves = new List<Curve>(segments.Count);

            for (var i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                if (segment?.P0 == null || segment.P1 == null)
                    throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                        "boundary." + label + "[" + i + "] 需要 p0 和 p1 两个点。");

                if ((segment.P0.Z ?? 0) != 0 || (segment.P1.Z ?? 0) != 0) flattened = true;

                var lengthMm = CreateSupport.Distance(segment.P0, segment.P1);
                if (lengthMm < Units.MinLength)
                    throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                        "boundary." + label + "[" + i + "] 的两端相距 " +
                        lengthMm.ToString("0.###", CultureInfo.InvariantCulture) +
                        " 毫米，太短，Revit 无法接受。");

                // Z 一律压平：面的竖向位置由 levelId + baseOffset 决定，
                // 边界带着不同的 Z 会让 Revit 认为这个环不共面
                curves.Add(Line.CreateBound(
                    Units.Point(segment.P0.X, segment.P0.Y, 0),
                    Units.Point(segment.P1.X, segment.P1.Y, 0)));
            }

            RequireClosed(segments, label, index);

            if (flattened)
                CreateSupport.Once(context,
                    "边界点的 Z 坐标已被忽略（面的竖向位置由 levelId 与 baseOffset 决定）。");

            return curves;
        }

        private static void RequireClosed(List<LocationLine> segments, string label, int index)
        {
            for (var i = 0; i < segments.Count; i++)
            {
                var current = segments[i];
                var next = segments[(i + 1) % segments.Count];

                var gapMm = CreateSupport.Distance(current.P1, next.P0);
                if (gapMm <= ClosureToleranceMm) continue;

                var isWrap = i == segments.Count - 1;
                var what = isWrap
                    ? "边界没有闭合：最后一段 " + label + "[" + i + "] 的终点没有回到第一段的起点"
                    : "边界断开：" + label + "[" + i + "] 的终点与 " + label + "[" + (i + 1) + "] 的起点不相接";

                throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                    what + "——" + current.P1 + " 与 " + next.P0 + " 相距 " +
                    gapMm.ToString("0.###", CultureInfo.InvariantCulture) + " 毫米。" +
                    "线段必须首尾相接，前一段的 p1 就是后一段的 p0。");
            }
        }

        private static SurfaceCompat.CreateSurfaceResult Invoke(
            int index, string what, Func<SurfaceCompat.CreateSurfaceResult> create)
        {
            SurfaceCompat.CreateSurfaceResult result;
            try
            {
                result = create();
            }
            catch (Exception ex)
            {
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "Revit 拒绝创建该" + what + "：" + ex.Message);
            }

            if (result?.Element == null)
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "Revit 未能创建" + what + "，但也没有报错。请检查边界是否自相交。");

            return result;
        }

        /// <summary>
        /// 偏移**必须无条件写一遍，且写完读回来核对**。
        ///
        /// 这里曾经只在 <c>offsetMm != 0</c> 时才写，于是最常见的那种调用
        /// （省略 baseOffset，指望构件落在标高上）反而是错的：
        /// 边界被压平到项目绝对 Z=0 交给 <c>NewFloor</c>，Revit 便把
        /// "草图平面与标高的高差"记成 <c>自标高的高度偏移 = 0 − 标高高程</c>，
        /// 二层的板于是静默地落在零标高上——标高参数还是对的，几何却不对，
        /// 查询接口和实际位置互相矛盾，只有量几何才看得出来。
        ///
        /// 高程不是可以"尽力而为"的属性：位置错了的板，比建不出来的板危险得多。
        /// 所以写不上、或者读回来对不上，都让整批回滚。
        /// </summary>
        /// <returns>读回来的实际偏移（毫米）。参数不存在时为 null。</returns>
        private static double? EnforceOffset(
            Element element, BuiltInParameter id, double offsetMm, int index)
        {
            var actualMm = CreateSupport.SetLengthVerified(element, id, offsetMm, "标高偏移", index);

            // 参数不存在：这个类别/版本不按"标高 + 偏移"定位。
            // 此时只有 offsetMm == 0 才谈得上"已经放对了"，非零请求必须报错而不是悄悄忽略。
            if (!actualMm.HasValue && Math.Abs(offsetMm) > CreateSupport.LengthToleranceMm)
                throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                    "这个构件没有标高偏移参数，baseOffset " +
                    offsetMm.ToString("0.###", CultureInfo.InvariantCulture) +
                    " 毫米无处可写。请把竖向位置改由 levelId 表达。");

            return actualMm;
        }

        private static BuiltInCategory ParseCategoryAt(string raw, int index)
        {
            try
            {
                return ParseCategory(raw);
            }
            catch (ToolFailureException ex)
            {
                throw CreateSupport.Failure(index, ex.Code, ex.Message);
            }
        }
    }
}
