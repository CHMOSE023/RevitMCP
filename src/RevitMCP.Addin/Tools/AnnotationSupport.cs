using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    /// <summary>
    /// 注释类工具共用的零件。
    ///
    /// 抽出来的理由和 <see cref="CreateSupport"/> 一样：
    /// "省略 viewId 用哪个视图"、"标记放在构件的哪个点上"这类问题
    /// 在两个工具里给出不同答案，是最难查的那种 bug。
    /// </summary>
    internal static class AnnotationSupport
    {
        /// <summary>
        /// 解析注释所在的视图。省略时退到活动视图，并把选了哪个说出来。
        /// 明细表和图纸不能放注释，提前拦下来。
        /// </summary>
        public static View ResolveView(
            Document document, ToolExecutionContext<UIApplication> context, string rawId, int index)
        {
            View view;

            if (!string.IsNullOrWhiteSpace(rawId))
            {
                var element = CreateSupport.RequireElement(document, rawId, index);
                view = element as View;

                if (view == null)
                    throw AnnotationFail.At(index, McpDomainError.InvalidParameter,
                        "viewId " + rawId + " 不是视图，而是「" +
                        (element.Category?.Name ?? element.GetType().Name) +
                        "」。用 revit_list_views 取视图 ID。");
            }
            else
            {
                view = document.ActiveView;

                if (view == null)
                    throw AnnotationFail.At(index, McpDomainError.InvalidParameter,
                        "没有指定 viewId，当前也没有活动视图。");

                CreateSupport.Once(context, "未指定 viewId，注释放在活动视图「" + SafeName(view) + "」里。");
            }

            if (view.IsTemplate)
                throw AnnotationFail.At(index, McpDomainError.InvalidParameter,
                    "视图「" + SafeName(view) + "」是一个视图样板，样板里不能放注释。");

            if (view.ViewType == ViewType.Schedule || view.ViewType == ViewType.ColumnSchedule)
                throw AnnotationFail.At(index, McpDomainError.InvalidParameter,
                    "视图「" + SafeName(view) + "」是明细表，明细表里不能放注释。");

            return view;
        }

        public static XYZ RequirePosition(Point3D position, int index, string kind)
        {
            if (position == null)
                throw AnnotationFail.At(index, McpDomainError.InvalidParameter,
                    "kind 为 " + kind + " 时必须给 position（放置点，毫米）。");

            return position.ToXyz();
        }

        public static TagOrientation ParseOrientation(string value, int index)
        {
            if (string.IsNullOrWhiteSpace(value)) return TagOrientation.Horizontal;

            switch (value.Trim().ToLowerInvariant())
            {
                case "horizontal": return TagOrientation.Horizontal;
                case "vertical": return TagOrientation.Vertical;

                default:
                    throw AnnotationFail.At(index, McpDomainError.InvalidParameter,
                        "无法识别的 orientation \"" + value + "\"。可用值：horizontal、vertical。");
            }
        }

        /// <summary>
        /// 解析注释族类型。给了 ID 就校验类别，没给就取该类别的第一个可用类型。
        /// 与建模工具的 <see cref="CreateSupport.ResolveType{T}"/> 分开写，
        /// 是因为注释类型走的不是 <c>GetDefaultFamilyTypeId</c> 那套默认机制。
        /// </summary>
        public static T ResolveAnnotationType<T>(
            Document document, ToolExecutionContext<UIApplication> context,
            string rawId, BuiltInCategory category, int index, string label) where T : ElementType
        {
            if (!string.IsNullOrWhiteSpace(rawId))
            {
                var element = CreateSupport.RequireElement(document, rawId, index);
                var type = element as T;

                if (type == null)
                    throw AnnotationFail.At(index, McpDomainError.InvalidParameter,
                        "typeId " + rawId + " 不是" + label + "类型（它是「" +
                        (element.Category?.Name ?? element.GetType().Name) + "」）。");

                return type;
            }

            var candidate = new FilteredElementCollector(document)
                .OfClass(typeof(T))
                .Cast<T>()
                .FirstOrDefault();

            if (candidate == null)
                throw AnnotationFail.At(index, McpDomainError.InvalidParameter,
                    "本项目里没有任何" + label + "类型，无法创建。" +
                    "这通常说明项目样板缺了这部分内容，需要用户先在 Revit 里添加。");

            CreateSupport.Once(context, "未指定 typeId，使用" + label + "类型「" + SafeName(candidate) + "」。");
            return candidate;
        }

        public static IndependentTag CreateTag(
            Document document, View view, Element target, XYZ position,
            bool addLeader, TagOrientation orientation, int index)
        {
            try
            {
                var tag = IndependentTag.Create(
                    document, view.Id, new Reference(target), addLeader,
                    TagMode.TM_ADDBY_CATEGORY, orientation, position);

                if (tag == null)
                    throw AnnotationFail.At(index, McpDomainError.TransactionFailed,
                        "Revit 未能创建标记，但也没有报错。");

                return tag;
            }
            catch (ToolFailureException) { throw; }
            catch (Exception ex)
            {
                throw AnnotationFail.At(index, McpDomainError.TransactionFailed,
                    "Revit 拒绝给构件 " + target.Id.ToProtocolString() + "（" +
                    (target.Category?.Name ?? "未知类别") + "）创建标记：" + ex.Message +
                    "。最常见的原因是项目里没有载入这个类别的标记族——" +
                    "用 revit_list_families 确认，没有的话需要用户先在 Revit 里载入。");
            }
        }

        /// <summary>
        /// 找出一个能用来做尺寸标注的 <see cref="Reference"/>。
        ///
        /// 直接用 <c>new Reference(element)</c> 对轴网、标高这类"本身就是一条基准线"
        /// 的图元有效，但对墙、柱这些实体构件基本不管用——它们需要的是**某个面**的引用。
        /// 所以这里按"能拿到多精确的东西"依次降级，并在退到兜底方案时说出来：
        /// 一条位置不对的标注比没有标注更难发现。
        /// </summary>
        public static Reference ResolveDimensionReference(
            Element element, View view, XYZ direction, int index,
            ToolExecutionContext<UIApplication> context)
        {
            // 基准图元本身就是一条线/面，直接引用即可，也是最准的
            if (element is Grid || element is Level || element is ReferencePlane)
                return new Reference(element);

            var face = FindFaceReference(element, view, direction);
            if (face != null) return face;

            CreateSupport.Once(context,
                "构件 " + element.Id.ToProtocolString() + "（" +
                (element.Category?.Name ?? "未知类别") +
                "）没能找到与尺寸线方向垂直的面，退回按整个构件引用。" +
                "标注的位置可能不是你想要的那一侧，建完请核对一下。");

            return new Reference(element);
        }

        /// <summary>
        /// 在构件几何里找一个法线与尺寸线方向平行的平面。
        ///
        /// 尺寸量的是沿 <paramref name="direction"/> 的距离，所以要标的是
        /// 垂直于这个方向的那些面——它们的法线恰好与 direction 平行。
        /// </summary>
        private static Reference FindFaceReference(Element element, View view, XYZ direction)
        {
            GeometryElement geometry;
            try
            {
                // ComputeReferences 必须开，否则拿到的面没有可用的 Reference。
                // 绑定到视图是为了拿到这个视图里实际显示的那份几何
                geometry = element.get_Geometry(new Options
                {
                    ComputeReferences = true,
                    IncludeNonVisibleObjects = false,
                    View = view
                });
            }
            catch { return null; }

            if (geometry == null) return null;

            Reference best = null;
            var bestAlignment = 0.0;

            foreach (var face in EnumerateFaces(geometry))
            {
                var planar = face as PlanarFace;
                if (planar == null || planar.Reference == null) continue;

                var alignment = Math.Abs(planar.FaceNormal.DotProduct(direction));

                // 0.99 ≈ 8 度以内。再松就会挑到明显斜的面，标出来的读数没有意义
                if (alignment < 0.99 || alignment <= bestAlignment) continue;

                bestAlignment = alignment;
                best = planar.Reference;
            }

            return best;
        }

        private static IEnumerable<Face> EnumerateFaces(GeometryElement geometry)
        {
            foreach (var item in geometry)
            {
                var solid = item as Solid;
                if (solid != null)
                {
                    foreach (Face face in solid.Faces) yield return face;
                    continue;
                }

                // 族实例的几何包在一层实例变换里，要再往下走一层
                var instance = item as GeometryInstance;
                if (instance == null) continue;

                GeometryElement nested;
                try { nested = instance.GetInstanceGeometry(); }
                catch { continue; }

                if (nested == null) continue;

                foreach (var inner in nested)
                {
                    var innerSolid = inner as Solid;
                    if (innerSolid == null) continue;

                    foreach (Face face in innerSolid.Faces) yield return face;
                }
            }
        }

        /// <summary>把一串点连成闭合折线。最后一点与第一点重合时不再多加一段。</summary>
        public static IList<Curve> BuildClosedLoop(IList<Point3D> boundary, int index)
        {
            var points = boundary.Select(p =>
            {
                if (p == null)
                    throw AnnotationFail.At(index, McpDomainError.InvalidParameter,
                        "boundary 里有 null 点。");
                return p.ToXyz();
            }).ToList();

            // 首尾点重合时去掉重复的那个，否则下面会生成一段零长度曲线
            if (points.Count > 1 && points[0].DistanceTo(points[points.Count - 1]) < Units.ToFeet(Units.MinLength))
                points.RemoveAt(points.Count - 1);

            if (points.Count < 3)
                throw AnnotationFail.At(index, McpDomainError.InvalidParameter,
                    "去掉重复点后 boundary 只剩 " + points.Count + " 个点，围不成一个闭合区域。");

            var curves = new List<Curve>();

            for (var i = 0; i < points.Count; i++)
            {
                var from = points[i];
                var to = points[(i + 1) % points.Count];

                if (from.DistanceTo(to) < Units.ToFeet(Units.MinLength))
                    throw AnnotationFail.At(index, McpDomainError.InvalidParameter,
                        "boundary 的第 " + (i + 1) + " 段太短（两点几乎重合），Revit 无法创建曲线。");

                curves.Add(Line.CreateBound(from, to));
            }

            return curves;
        }

        /// <summary>这个视图里已经被标记过的构件。</summary>
        public static HashSet<long> AlreadyTaggedIn(Document document, View view)
        {
            var tagged = new HashSet<long>();

            foreach (var tag in new FilteredElementCollector(document, view.Id)
                         .OfClass(typeof(IndependentTag))
                         .OfType<IndependentTag>())
            {
                foreach (var id in TagCompat.TaggedElementIds(tag))
                {
                    if (id != null && id != ElementId.InvalidElementId) tagged.Add(id.GetValue());
                }
            }

            return tagged;
        }

        /// <summary>
        /// 构件用来挂标记的锚点：点定位构件取那个点，线定位构件取定位线中点，
        /// 都没有就退到包围盒中心。三条都不成立时返回 null——那种构件标不了。
        /// </summary>
        public static XYZ AnchorOf(Element element)
        {
            try
            {
                var point = element.Location as LocationPoint;
                if (point != null) return point.Point;

                var curve = element.Location as LocationCurve;
                if (curve?.Curve != null) return curve.Curve.Evaluate(0.5, true);
            }
            catch { /* 落到下面试包围盒 */ }

            try
            {
                var box = element.get_BoundingBox(null);
                if (box != null) return (box.Min + box.Max) / 2;
            }
            catch { /* 确实没有可用的锚点 */ }

            return null;
        }

        public static string TypeNameOf(Document document, Element element)
        {
            try
            {
                var typeId = element.GetTypeId();
                if (typeId == null || typeId == ElementId.InvalidElementId) return null;

                return SafeName(document.GetElement(typeId));
            }
            catch { return null; }
        }

        public static string SafeName(Element element)
        {
            try { return element?.Name; }
            catch { return null; }
        }
    }
}
