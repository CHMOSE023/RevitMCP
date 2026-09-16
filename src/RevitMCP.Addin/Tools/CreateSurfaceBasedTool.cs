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
                      "边界必须首尾相接形成闭合环；不闭合会被直接拒绝，并告诉你断在哪一段。",
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
            var offsetMm = spec.BaseOffset ?? 0;

            Element created;
            ElementType usedType;

            switch (category)
            {
                case BuiltInCategory.OST_Floors:
                    usedType = CreateSupport.ResolveType<FloorType>(
                        document, context, spec.TypeId, BuiltInCategory.OST_Floors, index);
                    created = Invoke(index, "楼板", () => SurfaceCompat.CreateFloor(
                        document, boundary, usedType, level, spec.Structural ?? false));
                    if (offsetMm != 0)
                        ApplyOffset(created, BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM, offsetMm, context);
                    break;

                case BuiltInCategory.OST_Roofs:
                    usedType = CreateSupport.ResolveType<RoofType>(
                        document, context, spec.TypeId, BuiltInCategory.OST_Roofs, index);
                    created = Invoke(index, "屋顶", () => SurfaceCompat.CreateRoof(
                        document, boundary, (RoofType)usedType, level));
                    if (offsetMm != 0)
                        ApplyOffset(created, BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM, offsetMm, context);
                    break;

                case BuiltInCategory.OST_Ceilings:
                    if (!SurfaceCompat.CanCreateCeiling)
                        throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                            "Revit " + RevitVersionInfo.Year + " 的 API 不提供创建天花的入口" +
                            "（2022 才加入）。本版本上天花只能由用户在 Revit 界面里画。");

                    usedType = CreateSupport.ResolveType<ElementType>(
                        document, context, spec.TypeId, BuiltInCategory.OST_Ceilings, index);
                    created = Invoke(index, "天花", () => SurfaceCompat.CreateCeiling(
                        document, boundary, usedType, level));
                    if (offsetMm != 0)
                        ApplyOffset(created, BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM, offsetMm, context);
                    break;

                default:
                    throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                        "面定位建模只支持 OST_Floors、OST_Roofs、OST_Ceilings，收到 " + category + "。");
            }

            return new CreatedElement
            {
                Index = index,
                Id = created.Id.ToProtocolString(),
                Category = category.ToString(),
                Type = CreateSupport.SafeName(usedType),
                Level = CreateSupport.SafeName(level)
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

            var segments = boundary.OuterLoop;

            if (segments.Count < MinSegments)
                throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                    "boundary.outerLoop 至少要有 " + MinSegments + " 段才能围成一个面，收到 " +
                    segments.Count + " 段。");

            var flattened = false;
            var curves = new List<Curve>(segments.Count);

            for (var i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                if (segment?.P0 == null || segment.P1 == null)
                    throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                        "boundary.outerLoop[" + i + "] 需要 p0 和 p1 两个点。");

                if ((segment.P0.Z ?? 0) != 0 || (segment.P1.Z ?? 0) != 0) flattened = true;

                var lengthMm = CreateSupport.Distance(segment.P0, segment.P1);
                if (lengthMm < Mm.MinLength)
                    throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                        "boundary.outerLoop[" + i + "] 的两端相距 " +
                        lengthMm.ToString("0.###", CultureInfo.InvariantCulture) +
                        " 毫米，太短，Revit 无法接受。");

                // Z 一律压平：面的竖向位置由 levelId + baseOffset 决定，
                // 边界带着不同的 Z 会让 Revit 认为这个环不共面
                curves.Add(Line.CreateBound(
                    Mm.Point(segment.P0.X, segment.P0.Y, 0),
                    Mm.Point(segment.P1.X, segment.P1.Y, 0)));
            }

            RequireClosed(segments, index);

            if (flattened)
                CreateSupport.Once(context,
                    "边界点的 Z 坐标已被忽略（面的竖向位置由 levelId 与 baseOffset 决定）。");

            return curves;
        }

        private static void RequireClosed(List<LocationLine> segments, int index)
        {
            for (var i = 0; i < segments.Count; i++)
            {
                var current = segments[i];
                var next = segments[(i + 1) % segments.Count];

                var gapMm = CreateSupport.Distance(current.P1, next.P0);
                if (gapMm <= ClosureToleranceMm) continue;

                var isWrap = i == segments.Count - 1;
                var what = isWrap
                    ? "边界没有闭合：最后一段 outerLoop[" + i + "] 的终点没有回到第一段的起点"
                    : "边界断开：outerLoop[" + i + "] 的终点与 outerLoop[" + (i + 1) + "] 的起点不相接";

                throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                    what + "——" + current.P1 + " 与 " + next.P0 + " 相距 " +
                    gapMm.ToString("0.###", CultureInfo.InvariantCulture) + " 毫米。" +
                    "线段必须首尾相接，前一段的 p1 就是后一段的 p0。");
            }
        }

        private static Element Invoke(int index, string what, Func<Element> create)
        {
            Element created;
            try
            {
                created = create();
            }
            catch (Exception ex)
            {
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "Revit 拒绝创建该" + what + "：" + ex.Message);
            }

            if (created == null)
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "Revit 未能创建" + what + "，但也没有报错。请检查边界是否自相交。");

            return created;
        }

        /// <summary>
        /// 偏移设不上只警告：面已经建在正确的平面位置上了，
        /// 为一个高度把整批回滚，代价远大于收益——但必须说出来。
        /// </summary>
        private static void ApplyOffset(
            Element element, BuiltInParameter id, double offsetMm, ToolExecutionContext<UIApplication> context)
        {
            try
            {
                var parameter = element.get_Parameter(id);
                if (parameter != null && !parameter.IsReadOnly && parameter.Set(Mm.ToFeet(offsetMm))) return;
            }
            catch { /* 落到下面的警告 */ }

            CreateSupport.Once(context,
                "构件 " + element.Id.ToProtocolString() + " 的标高偏移没能设成 " + offsetMm +
                " 毫米，它建在了标高平面上。");
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
