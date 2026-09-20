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
    // ==================== 共用的参数形状 ====================

    public sealed class Point3D
    {
        [McpParam("X 坐标，毫米", Required = true)]
        public double X { get; set; }

        [McpParam("Y 坐标，毫米", Required = true)]
        public double Y { get; set; }

        [McpParam("Z 坐标，毫米。相对项目基点，通常填 0 并用 levelId 指定标高")]
        public double? Z { get; set; }

        public XYZ ToXyz()
        {
            return Units.Point(X, Y, Z ?? 0);
        }

        public override string ToString()
        {
            return "(" + Format(X) + ", " + Format(Y) + ", " + Format(Z ?? 0) + ")";
        }

        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    public sealed class LocationLine
    {
        [McpParam("起点", Required = true)]
        public Point3D P0 { get; set; }

        [McpParam("终点", Required = true)]
        public Point3D P1 { get; set; }
    }

    /// <summary>一个新建构件的回执。批量创建时 <see cref="Index"/> 把 ID 与入参对得上。</summary>
    public sealed class CreatedElement
    {
        [McpParam("对应 elements 数组中的下标，从 0 起")]
        public int Index { get; set; }

        [McpParam("新建构件的 ID")]
        public string Id { get; set; }

        [McpParam("所属类别")]
        public string Category { get; set; }

        [McpParam("使用的类型名")]
        public string Type { get; set; }

        [McpParam("所在标高名")]
        public string Level { get; set; }

        [McpParam("所在标高的高程，毫米")]
        public double? LevelElevationMm { get; set; }

        [McpParam("**写完读回来的**实际标高偏移，毫米。不是回显入参——" +
                  "它来自构件上的参数，与请求不符时本次创建已经回滚，不会出现在回执里")]
        public double? BaseOffsetMm { get; set; }

        [McpParam("标高高程 + 实际偏移，毫米。**楼板与天花是顶面、屋顶与柱底是底面**——" +
                  "Revit 的偏移参数在这几个类别上语义不同，这里照它本来的语义报。" +
                  "拿它直接和图纸标高核对，不用再查一次参数")]
        public double? ElevationMm { get; set; }

        [McpParam("长度，毫米。仅线定位构件有")]
        public double? LengthMm { get; set; }

        [McpParam("按 locationLineRef 把墙从给定定位线上整体挪开的距离，毫米。未用定位线时为 null。" +
                  "**它只说挪了多远，不说挪对了方向**——要确认内外侧，" +
                  "用 revit_get_element_geometry 量一圈墙的包围盒：" +
                  "外表面定位时它应当正好等于图纸上的外轮廓尺寸，大了一个墙厚就是方向反了")]
        public double? LocationLineShiftMm { get; set; }
    }

    public sealed class CreateElementsOutput : IReportsAffectedElements
    {
        [McpParam("成功创建的构件数")]
        public int Created { get; set; }

        [McpParam("新建的构件，顺序与入参一致")]
        public List<CreatedElement> Elements { get; set; } = new List<CreatedElement>();

        int IReportsAffectedElements.AffectedElements => Created;
    }

    // ==================== 共用的解析与校验 ====================

    /// <summary>
    /// 三个建模工具共用的零件：解析标高、解析类型、按下标包装错误。
    ///
    /// 抽出来不是为了少写几行，而是因为这些地方的行为必须完全一致——
    /// "省略 levelId 时选哪个标高"在三个工具里给出不同答案，
    /// 是模型永远调试不出来的那种 bug。
    /// </summary>
    internal static class CreateSupport
    {
        /// <summary>
        /// 解析标高。给了 ID 就用它；没给就退到活动视图的标高、再退到最低标高，
        /// 并把选了哪个通过 warnings 说出来——默认值不说出来等于没有默认值。
        /// </summary>
        public static Level ResolveLevel(
            Document document, ToolExecutionContext<UIApplication> context, string rawId, int index)
        {
            if (!string.IsNullOrWhiteSpace(rawId))
            {
                var element = RequireElement(document, rawId, index);
                var level = element as Level;

                if (level == null)
                    throw Failure(index, McpDomainError.InvalidParameter,
                        "levelId " + rawId + " 不是标高，而是「" +
                        (element.Category?.Name ?? element.GetType().Name) +
                        "」。用 revit_list_levels 取标高 ID。");

                return level;
            }

            var viewLevel = ActiveViewLevel(document);
            if (viewLevel != null)
            {
                Once(context, "未指定 levelId，使用活动视图所在标高「" + SafeName(viewLevel) + "」。");
                return viewLevel;
            }

            var lowest = new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => level.Elevation)
                .FirstOrDefault();

            if (lowest == null)
                throw Failure(index, McpDomainError.InvalidParameter, "模型里没有任何标高，无法创建构件。");

            Once(context, "未指定 levelId 且活动视图没有关联标高，使用最低标高「" + SafeName(lowest) + "」。");
            return lowest;
        }

        private static Level ActiveViewLevel(Document document)
        {
            try { return document.ActiveView == null ? null : document.ActiveView.GenLevel; }
            catch { return null; }
        }

        /// <summary>
        /// 解析类型。给了 ID 就校验它属于目标类别且类型正确；
        /// 没给就用该类别的默认类型。
        /// </summary>
        public static T ResolveType<T>(
            Document document,
            ToolExecutionContext<UIApplication> context,
            string rawId,
            BuiltInCategory category,
            int index) where T : ElementType
        {
            if (!string.IsNullOrWhiteSpace(rawId))
            {
                var element = RequireElement(document, rawId, index);
                var type = element as T;

                if (type == null)
                {
                    // 类别对、但 .NET 类型不对，要单独说。
                    // 一个类别下并非所有类型都能拿来建实例——OST_GenericModel 里就混着
                    // ModelTextType 这种东西。此时若照着"类别不符"的措辞报错，
                    // 会说出"typeId 403 不是 OST_GenericModel 可用的类型（它是「常规模型」）"
                    // 这种自相矛盾的话，模型只会反复换 ID 重试
                    var asElementType = element as ElementType;
                    if (asElementType != null && BelongsTo(asElementType, category))
                        throw Failure(index, McpDomainError.InvalidParameter,
                            "typeId " + rawId + "（" + SafeName(element) + "）确实属于 " + category +
                            "，但它是 " + element.GetType().Name + "，不是可以用来创建实例的族类型。" +
                            "同一个类别下并非所有类型都能建模——用 revit_list_types 换一个。");

                    throw Failure(index, McpDomainError.InvalidParameter,
                        "typeId " + rawId + " 不是 " + category + " 可用的类型（它是「" +
                        (element.Category?.Name ?? element.GetType().Name) +
                        "」）。用 revit_list_types 取该类别下的类型 ID。");
                }

                if (!BelongsTo(type, category))
                    throw Failure(index, McpDomainError.InvalidParameter,
                        "typeId " + rawId + "（" + SafeName(type) + "）属于类别「" +
                        (type.Category?.Name ?? "未知") + "」，与请求的 " + category + " 不符。");

                return type;
            }

            var defaultType = DefaultType<T>(document, category);
            if (defaultType == null)
                throw Failure(index, McpDomainError.InvalidParameter,
                    "未指定 typeId，且类别 " + category + " 在本项目中没有可用的默认类型。" +
                    "用 revit_list_types 查看可用类型；若一个都没有，需要用户先在 Revit 里载入对应的族。");

            Once(context, "未指定 typeId，使用 " + category + " 的默认类型「" + SafeName(defaultType) + "」。");
            return defaultType;
        }

        private static bool BelongsTo(ElementType type, BuiltInCategory category)
        {
            var typeCategory = type.Category;
            if (typeCategory == null) return true;   // 取不到类别时不拦，让 Revit 自己判断

            return typeCategory.Id.GetValue() == (long)category;
        }

        private static T DefaultType<T>(Document document, BuiltInCategory category) where T : ElementType
        {
            try
            {
                var categoryId = Category.GetCategory(document, category)?.Id;
                if (categoryId != null)
                {
                    var defaultId = document.GetDefaultFamilyTypeId(categoryId);
                    var byDefault = defaultId == ElementId.InvalidElementId
                        ? null
                        : document.GetElement(defaultId) as T;

                    if (byDefault != null) return byDefault;
                }
            }
            catch { /* 部分类别没有默认类型的概念，落到下面任取一个 */ }

            // 项目没设默认类型时任取一个同类别的。比直接失败有用，
            // 用了哪个会通过 warnings 说出来，模型不满意可以改 typeId 重来
            return new FilteredElementCollector(document)
                .OfCategory(category)
                .WhereElementIsElementType()
                .OfType<T>()
                .FirstOrDefault();
        }

        /// <summary>
        /// 族类型必须先激活才能用来创建实例。
        /// 忘了这一步的表现是 NewFamilyInstance 返回 null 且不报错——排查起来毫无线索。
        /// </summary>
        public static void EnsureActive(FamilySymbol symbol)
        {
            if (symbol == null || symbol.IsActive) return;

            symbol.Activate();

            // Activate 之后必须刷新，否则同一个事务里紧接着的 NewFamilyInstance 仍会失败
            symbol.Document.Regenerate();
        }

        public static Element RequireElement(Document document, string rawId, int index)
        {
            string problem;
            var element = ElementRef.Resolve(document, rawId, out problem);

            if (element != null) return element;

            // 与 RevitToolBase.RequireElement 保持一致：格式问题与找不到要用不同的错误码
            var code = problem != null && problem.Contains("格式")
                ? McpDomainError.InvalidParameter
                : McpDomainError.ElementNotFound;

            throw Failure(index, code, problem);
        }

        /// <summary>
        /// 构造一条带下标的失败信息。
        ///
        /// 批量创建是全有全无的，所以失败时模型必须知道**是哪一项**出的问题——
        /// 只说"创建失败"，模型只能把整批参数重新猜一遍。
        /// </summary>
        public static ToolFailureException Failure(int index, string code, string message)
        {
            return Failure(index, code, message, "elements", "整批未创建");
        }

        /// <summary>
        /// 同上，但由调用方给出**它自己的**数组参数名与回滚措辞。
        ///
        /// 这两样都不能写死。实测踩过：<c>revit_update_sheets</c> 的报错说
        /// "elements[0]：…（整批未创建）"，而它的参数叫 <c>sheets</c>、做的是修改不是创建——
        /// 调用方会去找一个不存在的数组，然后以为有什么东西没建出来。
        /// 报错里的每一个名词都必须指向调用方真的能看到的东西。
        /// </summary>
        public static ToolFailureException Failure(
            int index, string code, string message, string field, string outcome)
        {
            var prefix = index >= 0 ? field + "[" + index + "]：" : string.Empty;
            var suffix = string.IsNullOrEmpty(outcome) ? string.Empty : "（" + outcome + "）";

            return new ToolFailureException(code, prefix + message + suffix);
        }

        /// <summary>
        /// 同一句提示只说一次。
        ///
        /// 批量创建 200 面墙时，"未指定 levelId，使用标高 1" 会产生 200 条一模一样的警告，
        /// 把真正值得看的那几条冲掉。
        /// </summary>
        public static void Once(ToolExecutionContext<UIApplication> context, string warning)
        {
            if (context.Warnings.Contains(warning)) return;
            context.Warnings.Add(warning);
        }

        public static Line RequireLine(LocationLine location, int index)
        {
            if (location == null)
                throw Failure(index, McpDomainError.InvalidParameter, "缺少 locationLine。");

            if (location.P0 == null || location.P1 == null)
                throw Failure(index, McpDomainError.InvalidParameter, "locationLine 需要 p0 和 p1 两个点。");

            var lengthMm = Distance(location.P0, location.P1);
            if (lengthMm < Units.MinLength)
                throw Failure(index, McpDomainError.InvalidParameter,
                    "起点 " + location.P0 + " 与终点 " + location.P1 + " 相距 " +
                    lengthMm.ToString("0.###", CultureInfo.InvariantCulture) +
                    " 毫米，太短，Revit 无法创建构件。两点至少相距 " + Units.MinLength + " 毫米。");

            return Line.CreateBound(location.P0.ToXyz(), location.P1.ToXyz());
        }

        public static double Distance(Point3D a, Point3D b)
        {
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var dz = (b.Z ?? 0) - (a.Z ?? 0);

            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public static string SafeName(Element element)
        {
            try { return element?.Name; }
            catch { return null; }
        }

        // ==================== [M10] 标高约束 ====================

        /// <summary>
        /// 解析可选的顶部标高。没给就返回 null（表示"用 height / 类型默认高度"）。
        /// </summary>
        public static Level ResolveTopLevel(Document document, string rawId, int index)
        {
            if (string.IsNullOrWhiteSpace(rawId)) return null;

            var element = RequireElement(document, rawId, index);
            var level = element as Level;

            if (level == null)
                throw Failure(index, McpDomainError.InvalidParameter,
                    "topLevelId " + rawId + " 不是标高，而是「" +
                    (element.Category?.Name ?? element.GetType().Name) +
                    "」。用 revit_list_levels 取标高 ID。");

            return level;
        }

        /// <summary>
        /// 由"底标高 + 底偏移"和"顶标高 + 顶偏移"算出净高，顺便把顺序搞反的情况拦下来。
        ///
        /// **算出来的高度仍然要用在几何上**，而不是只把顶部约束参数设上就完事：
        /// 参数设不上的时候（某些族没有这个参数），几何至少还是对的。
        /// 反过来先设参数、指望 Revit 去调几何，参数一旦失败构件就停在默认高度，
        /// 而这个偏差不会报错——正是最难查的那一类。
        /// </summary>
        public static double RequireClearHeightMm(
            Level baseLevel, double baseOffsetMm, Level topLevel, double topOffsetMm, int index)
        {
            var bottomMm = Units.FromFeet(baseLevel.Elevation) + baseOffsetMm;
            var topMm = Units.FromFeet(topLevel.Elevation) + topOffsetMm;
            var heightMm = topMm - bottomMm;

            if (heightMm < Units.MinLength)
                throw Failure(index, McpDomainError.InvalidParameter,
                    "顶标高「" + SafeName(topLevel) + "」加偏移后在 " + Format(topMm) +
                    " 毫米，不高于底标高「" + SafeName(baseLevel) + "」加偏移后的 " + Format(bottomMm) +
                    " 毫米，算出的高度是 " + Format(heightMm) + " 毫米。" +
                    "顶标高要在底标高之上，两者至少相差 " + Units.MinLength + " 毫米。");

            return heightMm;
        }

        /// <summary>
        /// 把顶部约束写成参数，让构件真正随标高联动。
        ///
        /// 设不上只警告：几何高度已经按 <see cref="RequireClearHeightMm"/> 算对了，
        /// 构件在正确的位置上，丢的只是"改标高时自动跟着变"这件事。
        /// 为它把整批回滚，代价大于收益——但必须说出来，
        /// 否则用户调层高时会发现有几片墙没跟着动，而且不知道为什么。
        /// </summary>
        /// <param name="topOffsetMm">
        /// 顶部偏移。null 表示调用方没提，保持 Revit 自己的取值；
        /// 明确给 0 与省略不同——前者要求顶面正好落在标高上，必须真的写进去并核对。
        /// </param>
        public static void ApplyTopConstraint(
            Element element, BuiltInParameter levelParam, BuiltInParameter offsetParam,
            Level topLevel, double? topOffsetMm, ToolExecutionContext<UIApplication> context,
            int index = -1)
        {
            if (!TrySet(element, levelParam, parameter => parameter.Set(topLevel.Id)))
            {
                Once(context,
                    "构件 " + element.Id.ToProtocolString() + " 的顶部约束没能设到标高「" +
                    SafeName(topLevel) + "」。它的高度是对的，但不会随标高联动。");
                return;
            }

            if (!topOffsetMm.HasValue) return;

            var actual = SetLengthVerified(element, offsetParam, topOffsetMm.Value, "顶部偏移", index);
            if (actual.HasValue) return;

            Once(context,
                "构件 " + element.Id.ToProtocolString() + " 上没有顶部偏移参数，topOffset " +
                Format(topOffsetMm.Value) + " 毫米没有写入。");
        }

        /// <summary>
        /// 写一个长度参数，**再读回来核对**。
        ///
        /// 竖向约束（标高偏移、窗台高度、柱顶偏移）不能"尽力而为"：
        /// 位置错了的构件比建不出来的构件危险得多——它在模型里长得一模一样，
        /// 标高参数也对，只有量几何才看得出来。所以写不进去、或者写完读回来对不上，
        /// 都抛错让整批回滚，而不是留一条警告。
        ///
        /// 容差取 0.01 毫米：毫米与英尺之间来回换算必然有浮点尾巴，但真的写错至少差几十毫米。
        /// </summary>
        /// <returns>读回来的实际值（毫米）。参数在这个构件上不存在时返回 null。</returns>
        public static double? SetLengthVerified(
            Element element, BuiltInParameter id, double valueMm, string what, int index,
            string field = "elements", string outcome = "整批未创建")
        {
            Parameter parameter;
            try { parameter = element.get_Parameter(id); }
            catch (Exception ex)
            {
                throw Failure(index, McpDomainError.TransactionFailed,
                    "读不到构件 " + element.Id.ToProtocolString() + " 的" + what + "参数：" + ex.Message,
                    field, outcome);
            }

            if (parameter == null) return null;

            if (!parameter.IsReadOnly)
            {
                try { parameter.Set(Units.ToFeet(valueMm)); }
                catch (Exception ex)
                {
                    throw Failure(index, McpDomainError.TransactionFailed,
                        "构件 " + element.Id.ToProtocolString() + " 的" + what + "写不进去（目标 " +
                        Format(valueMm) + " 毫米）：" + ex.Message, field, outcome);
                }
            }

            var actualMm = Units.Round(Units.FromFeet(parameter.AsDouble()));
            if (Math.Abs(actualMm - valueMm) > LengthToleranceMm)
                throw Failure(index, McpDomainError.TransactionFailed,
                    "构件 " + element.Id.ToProtocolString() + " 的" + what + "应为 " + Format(valueMm) +
                    " 毫米，写完读回来却是 " + Format(actualMm) + " 毫米，相差 " +
                    Format(actualMm - valueMm) + " 毫米。", field, outcome);

            return actualMm;
        }

        /// <summary>竖向约束读回来比对的容差，毫米。</summary>
        public const double LengthToleranceMm = 0.01;

        private static bool TrySet(Element element, BuiltInParameter id, Func<Parameter, bool> set)
        {
            try
            {
                var parameter = element.get_Parameter(id);
                return parameter != null && !parameter.IsReadOnly && set(parameter);
            }
            catch { return false; }
        }

        internal static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
