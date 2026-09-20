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
    /// 本文件的失败构造：把下标前缀写成 <c>views[i]</c>，而不是通用的 elements[i]。
    ///
    /// 报错里出现的数组名必须是调用方真的传过的那个，否则它会去找一个不存在的参数。
    /// </summary>
    internal static class ViewFail
    {
        public static ToolFailureException At(int index, string code, string message)
        {
            return CreateSupport.Failure(index, code, message, "views", "整批未创建");
        }
    }

    // ==================== 创建视图 ====================

    public sealed class ViewSpec
    {
        [McpParam("视图类型：floorPlan（楼层平面）、ceilingPlan（天花平面）、" +
                  "section（剖面）、elevation（立面）、threeD（三维）", Required = true,
                  AllowedValues = new[] { "floorPlan", "ceilingPlan", "section", "elevation", "threeD" })]
        public string ViewType { get; set; }

        [McpParam("视图名。省略则由 Revit 自动命名。同名视图会被 Revit 拒绝，" +
                  "重名时工具会自动加后缀并通过 warnings 告知")]
        public string Name { get; set; }

        [McpParam("标高 ID，来自 revit_list_levels。floorPlan 与 ceilingPlan 必填")]
        public string LevelId { get; set; }

        [McpParam("剖切线，毫米。section 必填：从 p0 看向 p1，视线方向为沿线前进方向右手边。" +
                  "elevation 用它的 p0 作为立面位置、p0→p1 作为视线方向")]
        public LocationLine SectionLine { get; set; }

        [McpParam("剖面的上下范围，毫米。section 用，默认 [-1000, 10000]（相对项目零点）")]
        public double? BottomMm { get; set; }

        [McpParam("剖面的上下范围上限，毫米。section 用，默认 10000")]
        public double? TopMm { get; set; }

        [McpParam("视线方向上的可见深度，毫米。section 与 elevation 用，默认 10000")]
        public double? DepthMm { get; set; }

        [McpParam("三维视图是否为透视（相机）视图，默认 false（轴测）。仅 threeD 有效")]
        public bool? Perspective { get; set; }

        [McpParam("视图比例的分母，如 100 表示 1:100。省略则用视图类型的默认比例")]
        public int? Scale { get; set; }

        [McpParam("要套用的视图样板 ID，来自 revit_list_view_templates。省略则不套")]
        public string TemplateId { get; set; }

        [McpParam("视图族类型 ID。省略则自动挑该视图类型的第一个可用族类型。" +
                  "项目里有多套视图族类型（如「建筑平面」「结构平面」）时才需要显式指定")]
        public string ViewFamilyTypeId { get; set; }
    }

    public sealed class CreateViewsInput
    {
        [McpParam("要创建的视图，一次调用可建多个", Required = true)]
        public List<ViewSpec> Views { get; set; }

        [McpParam("数量超过单次上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    public sealed class CreatedView
    {
        [McpParam("对应 views 数组中的下标，从 0 起")]
        public int Index { get; set; }

        [McpParam("新建视图的 ID")]
        public string Id { get; set; }

        [McpParam("实际的视图名（重名时会与请求的不同）")]
        public string Name { get; set; }

        [McpParam("Revit 报告的视图类型")]
        public string ViewType { get; set; }

        [McpParam("视图比例的分母")]
        public int Scale { get; set; }

        [McpParam("套用的视图样板名，没套则为 null")]
        public string Template { get; set; }
    }

    public sealed class CreateViewsOutput : IReportsAffectedElements
    {
        [McpParam("成功创建的视图数")]
        public int Created { get; set; }

        [McpParam("新建的视图，顺序与入参一致")]
        public List<CreatedView> Views { get; set; } = new List<CreatedView>();

        int IReportsAffectedElements.AffectedElements => Created;
    }

    /// <summary>
    /// 批量创建视图。
    ///
    /// 平面、剖面、立面、三维合成一个工具：它们在 Revit 里都是 <c>View</c>，
    /// 拿到之后要做的事（改比例、套样板、上图纸）完全一样。
    /// 差别只在"怎么定出这个视图看哪儿"，而那正是 <c>viewType</c> 这一个参数的职责。
    /// </summary>
    [McpTool("revit_create_views",
        Title = "创建视图",
        Description = "批量创建平面、剖面、立面或三维视图。坐标一律用毫米。" +
                      "整批要么全部建成、要么一个都不建，且在撤销栈里只占一步。" +
                      "建好后用 revit_add_views_to_sheet 摆到图纸上。" +
                      "剖面的视线方向：站在 p0 面向 p1，看的是你右手边那一侧。",
        Destructive = false,
        TimeoutSeconds = 120)]
    public sealed class CreateViewsTool : RevitTool<CreateViewsInput, CreateViewsOutput>
    {
        private const double DefaultBottomMm = -1000.0;
        private const double DefaultTopMm = 10000.0;
        private const double DefaultDepthMm = 10000.0;

        public override CreateViewsOutput Execute(
            CreateViewsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);
            RequireBatch(input.Views, input.Confirm, context, "创建");

            var output = new CreateViewsOutput();
            var total = input.Views.Count;

            for (var index = 0; index < total; index++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var spec = input.Views[index];
                if (spec == null)
                    throw ViewFail.At(index, McpDomainError.InvalidParameter, "该项为 null。");

                output.Views.Add(CreateOne(document, context, spec, index));
                ProgressTicker.Tick(context.Progress, index + 1, total, "已创建");
            }

            output.Created = output.Views.Count;
            return output;
        }

        private static CreatedView CreateOne(
            Document document, ToolExecutionContext<UIApplication> context, ViewSpec spec, int index)
        {
            var kind = (spec.ViewType ?? string.Empty).Trim().ToLowerInvariant();

            View view;
            switch (kind)
            {
                case "floorplan":
                    view = CreatePlan(document, context, spec, index, ViewFamily.FloorPlan);
                    break;

                case "ceilingplan":
                    view = CreatePlan(document, context, spec, index, ViewFamily.CeilingPlan);
                    break;

                case "section":
                    view = CreateSection(document, context, spec, index, ViewFamily.Section);
                    break;

                case "elevation":
                    view = CreateSection(document, context, spec, index, ViewFamily.Elevation);
                    break;

                case "threed":
                case "3d":
                    view = Create3D(document, context, spec, index);
                    break;

                default:
                    throw ViewFail.At(index, McpDomainError.InvalidParameter,
                        "无法识别的 viewType \"" + spec.ViewType +
                        "\"。可用值：floorPlan、ceilingPlan、section、elevation、threeD。");
            }

            if (view == null)
                throw ViewFail.At(index, McpDomainError.TransactionFailed,
                    "Revit 未能创建视图，但也没有报错。");

            var result = new CreatedView { Index = index, Id = view.Id.ToProtocolString() };

            ApplyName(view, spec.Name, index, context, result);
            ApplyScale(view, spec.Scale, index, context);
            result.Template = ApplyTemplate(document, view, spec.TemplateId, index, context);

            result.Name = SafeName(view);
            result.ViewType = view.ViewType.ToString();
            result.Scale = view.Scale;

            return result;
        }

        // ==================== 各类视图 ====================

        private static View CreatePlan(
            Document document, ToolExecutionContext<UIApplication> context,
            ViewSpec spec, int index, ViewFamily family)
        {
            if (string.IsNullOrWhiteSpace(spec.LevelId))
                throw ViewFail.At(index, McpDomainError.InvalidParameter,
                    "创建" + Label(family) + "必须给 levelId。用 revit_list_levels 取标高 ID。");

            var level = CreateSupport.ResolveLevel(document, context, spec.LevelId, index);
            var familyType = ResolveViewFamilyType(document, spec.ViewFamilyTypeId, family, index);

            try
            {
                return ViewPlan.Create(document, familyType.Id, level.Id);
            }
            catch (Exception ex)
            {
                throw ViewFail.At(index, McpDomainError.TransactionFailed,
                    "Revit 拒绝在标高「" + CreateSupport.SafeName(level) + "」上创建" + Label(family) +
                    "：" + ex.Message + "。同一标高上同一种平面视图通常只能有一个——" +
                    "已经存在时要复制现有视图，而不是新建。");
            }
        }

        /// <summary>
        /// 剖面与立面。两者在 API 里是同一个入口（<c>ViewSection.CreateSection</c>），
        /// 只是视图族类型不同。
        /// </summary>
        private static View CreateSection(
            Document document, ToolExecutionContext<UIApplication> context,
            ViewSpec spec, int index, ViewFamily family)
        {
            if (spec.SectionLine == null)
                throw ViewFail.At(index, McpDomainError.InvalidParameter,
                    "创建" + Label(family) + "必须给 sectionLine（剖切线，毫米）。");

            // 复用建模工具的线校验：太短的线在这里同样是个说不清的失败
            CreateSupport.RequireLine(spec.SectionLine, index);

            var familyType = ResolveViewFamilyType(document, spec.ViewFamilyTypeId, family, index);
            var box = BuildSectionBox(spec, index);

            try
            {
                return ViewSection.CreateSection(document, familyType.Id, box);
            }
            catch (Exception ex)
            {
                throw ViewFail.At(index, McpDomainError.TransactionFailed,
                    "Revit 拒绝创建" + Label(family) + "：" + ex.Message);
            }
        }

        /// <summary>
        /// 由剖切线算出剖面框。
        ///
        /// 这个变换必须是**右手系**，否则 Revit 会给出一个方向诡异、
        /// 甚至完全看不到东西的视图。所以 BasisZ 一律由 BasisX × BasisY 算出来，
        /// 而不是自己拼一个向量——后者极容易差一个负号，且症状只在成品视图上才看得出来。
        ///
        /// 约定：BasisX 沿 p0→p1，BasisY 朝上，于是视线方向 BasisZ 指向
        /// "站在 p0 面向 p1 时的右手边"。
        /// </summary>
        private static BoundingBoxXYZ BuildSectionBox(ViewSpec spec, int index)
        {
            var p0 = spec.SectionLine.P0.ToXyz();
            var p1 = spec.SectionLine.P1.ToXyz();

            // 剖切方向只取水平分量：一条带高差的线并不意味着用户想要一个斜剖面
            var along = new XYZ(p1.X - p0.X, p1.Y - p0.Y, 0);
            if (along.GetLength() < 1e-9)
                throw ViewFail.At(index, McpDomainError.InvalidParameter,
                    "sectionLine 的两点在平面上重合，无法确定剖切方向。");

            along = along.Normalize();

            var transform = Transform.Identity;
            transform.Origin = (p0 + p1) / 2;
            transform.BasisX = along;
            transform.BasisY = XYZ.BasisZ;
            transform.BasisZ = along.CrossProduct(XYZ.BasisZ);

            var bottomMm = spec.BottomMm ?? DefaultBottomMm;
            var topMm = spec.TopMm ?? DefaultTopMm;

            if (topMm <= bottomMm)
                throw ViewFail.At(index, McpDomainError.InvalidParameter,
                    "topMm (" + Format(topMm) + ") 必须大于 bottomMm (" + Format(bottomMm) + ")。");

            var depthMm = spec.DepthMm ?? DefaultDepthMm;
            if (depthMm < Units.MinLength)
                throw ViewFail.At(index, McpDomainError.InvalidParameter,
                    "depthMm (" + Format(depthMm) + ") 太小，视图里什么都看不到。");

            // 框的坐标是在上面那个变换的局部系里表达的：
            // X 沿剖切线、Y 朝上、Z 沿视线。原点在剖切线中点，所以 X 取正负半宽
            var halfWidth = p0.DistanceTo(p1) / 2;
            var originZmm = Units.FromFeet(transform.Origin.Z);

            return new BoundingBoxXYZ
            {
                Transform = transform,
                Min = new XYZ(-halfWidth, Units.ToFeet(bottomMm - originZmm), 0),
                Max = new XYZ(halfWidth, Units.ToFeet(topMm - originZmm), Units.ToFeet(depthMm))
            };
        }

        private static View Create3D(
            Document document, ToolExecutionContext<UIApplication> context, ViewSpec spec, int index)
        {
            var familyType = ResolveViewFamilyType(
                document, spec.ViewFamilyTypeId, ViewFamily.ThreeDimensional, index);

            try
            {
                return spec.Perspective == true
                    ? View3D.CreatePerspective(document, familyType.Id)
                    : View3D.CreateIsometric(document, familyType.Id);
            }
            catch (Exception ex)
            {
                throw ViewFail.At(index, McpDomainError.TransactionFailed,
                    "Revit 拒绝创建三维视图：" + ex.Message);
            }
        }

        // ==================== 共用零件 ====================

        internal static ViewFamilyType ResolveViewFamilyType(
            Document document, string rawId, ViewFamily family, int index)
        {
            if (!string.IsNullOrWhiteSpace(rawId))
            {
                var element = CreateSupport.RequireElement(document, rawId, index);
                var type = element as ViewFamilyType;

                if (type == null)
                    throw ViewFail.At(index, McpDomainError.InvalidParameter,
                        "viewFamilyTypeId " + rawId + " 不是视图族类型。");

                if (type.ViewFamily != family)
                    throw ViewFail.At(index, McpDomainError.InvalidParameter,
                        "viewFamilyTypeId " + rawId + "（" + CreateSupport.SafeName(type) +
                        "）属于 " + type.ViewFamily + "，与请求的 " + family + " 不符。");

                return type;
            }

            var candidate = new FilteredElementCollector(document)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(type => type.ViewFamily == family);

            if (candidate == null)
                throw ViewFail.At(index, McpDomainError.InvalidParameter,
                    "本项目里没有 " + family + " 的视图族类型，无法创建" + Label(family) + "。" +
                    "这通常说明项目样板里没带这类视图，需要用户先在 Revit 里添加。");

            return candidate;
        }

        /// <summary>
        /// 改视图名。
        ///
        /// 重名不让整批失败，而是加后缀再试一次：批量建视图时撞名是常态，
        /// 为一个名字回滚掉几十个已经建好的视图，代价和收益完全不成比例。
        /// 实际用了什么名字会通过 warnings 和回执的 name 字段说清楚。
        /// </summary>
        private static void ApplyName(
            View view, string wanted, int index,
            ToolExecutionContext<UIApplication> context, CreatedView result)
        {
            if (string.IsNullOrWhiteSpace(wanted)) return;

            var name = wanted.Trim();

            try
            {
                view.Name = name;
                return;
            }
            catch { /* 多半是重名，下面加后缀重试 */ }

            for (var suffix = 2; suffix <= 50; suffix++)
            {
                var candidate = name + " (" + suffix + ")";
                try
                {
                    view.Name = candidate;
                    context.Warnings.Add(
                        "views[" + index + "]：视图名「" + name + "」已被占用，实际用了「" + candidate + "」。");
                    return;
                }
                catch { /* 继续试下一个后缀 */ }
            }

            context.Warnings.Add(
                "views[" + index + "]：视图名「" + name + "」已被占用，加后缀也没能避开，" +
                "保留了 Revit 的自动命名「" + SafeName(view) + "」。");
        }

        private static void ApplyScale(
            View view, int? scale, int index, ToolExecutionContext<UIApplication> context)
        {
            if (scale == null) return;

            if (scale.Value <= 0)
                throw ViewFail.At(index, McpDomainError.InvalidParameter,
                    "scale 必须是正整数（比例的分母，如 100 表示 1:100），收到 " + scale.Value + "。");

            try
            {
                view.Scale = scale.Value;
            }
            catch (Exception ex)
            {
                // 三维视图没有比例的概念，套样板的视图比例也可能被样板锁住。
                // 这两种都不该让整批回滚
                context.Warnings.Add(
                    "views[" + index + "]：视图已创建，但比例设为 1:" + scale.Value +
                    " 失败：" + ex.Message);
            }
        }

        internal static string ApplyTemplate(
            Document document, View view, string rawTemplateId, int index,
            ToolExecutionContext<UIApplication> context)
        {
            if (string.IsNullOrWhiteSpace(rawTemplateId)) return null;

            var template = RequireTemplate(document, rawTemplateId, index);

            try
            {
                view.ViewTemplateId = template.Id;
                return SafeName(template);
            }
            catch (Exception ex)
            {
                context.Warnings.Add(
                    "views[" + index + "]：视图已创建，但套用样板「" + SafeName(template) +
                    "」失败：" + ex.Message + "。样板与视图类型不匹配时会这样——" +
                    "平面的样板套不到剖面上。");
                return null;
            }
        }

        internal static View RequireTemplate(Document document, string rawId, int index)
        {
            var element = CreateSupport.RequireElement(document, rawId, index);
            var template = element as View;

            if (template == null || !template.IsTemplate)
                throw ViewFail.At(index, McpDomainError.InvalidParameter,
                    "ID " + rawId + " 不是视图样板" +
                    (template != null ? "（它是一个普通视图「" + SafeName(template) + "」）" : "") +
                    "。用 revit_list_view_templates 取样板 ID。");

            return template;
        }

        private static string Label(ViewFamily family)
        {
            switch (family)
            {
                case ViewFamily.FloorPlan: return "楼层平面";
                case ViewFamily.CeilingPlan: return "天花平面";
                case ViewFamily.Section: return "剖面";
                case ViewFamily.Elevation: return "立面";
                case ViewFamily.ThreeDimensional: return "三维视图";
                default: return family.ToString();
            }
        }

        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string SafeName(Element element)
        {
            try { return element?.Name; }
            catch { return null; }
        }
    }
}
