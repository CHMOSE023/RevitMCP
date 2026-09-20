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
    public sealed class SetViewExtentInput
    {
        [McpParam("要调整的视图 ID，来自 revit_list_views。省略则用活动视图")]
        public string ViewId { get; set; }

        [McpParam("取景范围按这些构件算，ID 来自 revit_query_elements。" +
                  "省略则按该视图里**所有可见的模型构件**算——那通常就是「把图拉满」想要的效果")]
        public List<string> ElementIds { get; set; }

        [McpParam("四周留白，毫米，默认 1000。按模型尺寸给，不是图纸尺寸")]
        public double? PaddingMm { get; set; }

        [McpParam("三维视图是否连剖切框一起收紧，默认 true。" +
                  "剖切框会**隐藏框外的构件**，这正是三维视图里「只看这一部分」的做法；" +
                  "设 false 则只裁剪画面、不剖切。二维视图忽略这个参数")]
        public bool? UseSectionBox { get; set; }

        [McpParam("是否在这个视图里隐藏标高线、轴网与剖切框，默认 true。" +
                  "**基准图元不受剖切框约束**——它们有自己的三维延伸范围，" +
                  "收紧了取景它们照样从画面这头拉到那头，建筑缩在角落里。" +
                  "要出一张给人看的图就关掉它们；要保留轴网标注则设 false")]
        public bool? HideDatums { get; set; }

        [McpParam("设为 false 表示关掉裁剪、恢复成不裁剪的状态，并把隐藏的基准图元恢复回来" +
                  "（此时其余参数都忽略）。默认 true")]
        public bool? CropActive { get; set; }
    }

    public sealed class SetViewExtentOutput : IReportsAffectedElements
    {
        [McpParam("视图 ID")]
        public string ViewId { get; set; }

        [McpParam("视图名")]
        public string ViewName { get; set; }

        [McpParam("Revit 报的视图类型")]
        public string ViewType { get; set; }

        [McpParam("裁剪是否处于开启状态")]
        public bool CropActive { get; set; }

        [McpParam("三维视图的剖切框是否处于开启状态。非三维视图为 null")]
        public bool? SectionBoxActive { get; set; }

        [McpParam("本次是否在这个视图里隐藏了标高线/轴网/剖切框")]
        public bool DatumsHidden { get; set; }

        [McpParam("取景依据的构件数")]
        public int BasedOnElements { get; set; }

        [McpParam("取景范围（模型坐标，毫米）。**核对它**——范围空了说明这个视图里根本没有可见构件")]
        public BoundingBoxInfo Extent { get; set; }

        int IReportsAffectedElements.AffectedElements => 1;
    }

    /// <summary>
    /// 把视图收到构件上。
    ///
    /// 补这个工具是因为导出的图基本是空白：一个三维视图里，标高线会一直延伸到很远，
    /// 建筑本身只占画面的四分之一——`revit_export_image` 按视图的实际范围出图，
    /// 而视图的实际范围没有人收过。README 把"取景控制"列为未完成项，说的就是这件事。
    ///
    /// **刻意做成独立的写工具，而不是给 export_image 加参数。**
    /// 收紧取景会真的改视图（裁剪框、剖切框），用户下次打开这个视图看到的就是收过的样子。
    /// 把它藏在一个只读的导出工具里，等于让"只读"这个承诺不再成立；
    /// 做成写工具则受写保护管辖、在撤销栈里占一步、用户能一步撤回。
    /// </summary>
    [McpTool("revit_set_view_extent",
        Toolsets = new[] { Toolsets.Documentation },
        Title = "调整视图取景范围",
        Description = "把视图的裁剪范围（三维再加剖切框）收到指定构件或全部可见构件上，" +
                      "**出图前用它**——未收过的视图里标高线会延伸很远，" +
                      "导出的图里建筑往往只占一小块，其余全是空白。" +
                      "这是一次真实的视图改动（用户下次打开看到的就是收过的样子），" +
                      "在撤销栈里占一步。cropActive: false 可以还原成不裁剪。",
        Destructive = false,
        TimeoutSeconds = 120)]
    public sealed class SetViewExtentTool : RevitTool<SetViewExtentInput, SetViewExtentOutput>
    {
        private const double DefaultPaddingMm = 1000.0;

        public override SetViewExtentOutput Execute(
            SetViewExtentInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);
            var view = ResolveView(document, input.ViewId);

            RequireCroppable(view);

            var output = new SetViewExtentOutput
            {
                ViewId = view.Id.ToProtocolString(),
                ViewName = SafeName(view),
                ViewType = view.ViewType.ToString()
            };

            var view3D = view as View3D;

            if (input.CropActive == false)
            {
                Relax(document, view, view3D);

                output.CropActive = SafeBool(() => view.CropBoxActive);
                output.SectionBoxActive = view3D == null ? (bool?)null : SafeBool(() => view3D.IsSectionBoxActive);
                return output;
            }

            var elements = Collect(document, view, input.ElementIds);
            output.BasedOnElements = elements.Count;

            var box = Union(elements, view);
            if (box == null)
                throw new ToolFailureException(McpDomainError.ElementNotFound,
                    input.ElementIds != null && input.ElementIds.Count > 0
                        ? "给的这些构件在视图「" + output.ViewName + "」里都没有几何（可能被隐藏或不在视图范围内），算不出取景范围。"
                        : "视图「" + output.ViewName + "」里没有可见的模型构件，算不出取景范围。" +
                          "先确认这个视图能看到东西——空视图导出来也是空白的。");

            var padding = Units.ToFeet(Math.Max(0, input.PaddingMm ?? DefaultPaddingMm));
            box = Inflate(box, padding);

            Apply(view, view3D, box, input.UseSectionBox ?? true, context);

            if (input.HideDatums ?? true)
            {
                SetDatumsHidden(document, view, true);
                output.DatumsHidden = true;
            }

            output.CropActive = SafeBool(() => view.CropBoxActive);
            output.SectionBoxActive = view3D == null ? (bool?)null : SafeBool(() => view3D.IsSectionBoxActive);
            output.Extent = Describe(box);

            return output;
        }

        /// <summary>
        /// 收紧。
        ///
        /// 二维视图（平面/剖面/立面）改 <c>CropBox</c>：它带着自己的 Transform，
        /// 直接把模型坐标的框赋进去，Revit 会自己换算到视图局部系。
        ///
        /// 三维视图**优先用剖切框**：三维的 CropBox 只裁画面，
        /// 远处那条延伸出去的标高线照样在画面里；剖切框才会真的把框外的东西挡掉。
        /// </summary>
        private static void Apply(
            View view, View3D view3D, BoundingBoxXYZ box, bool useSectionBox,
            ToolExecutionContext<UIApplication> context)
        {
            if (view3D != null && useSectionBox)
            {
                try
                {
                    view3D.SetSectionBox(box);
                    view3D.IsSectionBoxActive = true;
                }
                catch (Exception ex)
                {
                    throw new ToolFailureException(McpDomainError.TransactionFailed,
                        "Revit 拒绝设置剖切框：" + ex.Message +
                        "。锁定的三维视图改不了，先解锁或复制一个视图。");
                }
            }

            try
            {
                view.CropBox = ToViewFrame(view, box);
                view.CropBoxActive = true;
                view.CropBoxVisible = false;
            }
            catch (Exception ex)
            {
                // 剖切框已经生效的话，裁剪框设不上不算致命——说一声就够了
                if (view3D != null && useSectionBox)
                {
                    context.Warnings.Add(
                        "剖切框已经收紧，但裁剪框没能设上（" + ex.Message + "）。导出的图可能仍带较多空白。");
                    return;
                }

                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "Revit 拒绝设置裁剪范围：" + ex.Message +
                    "。视图被锁定、或套着禁止裁剪的视图样板时会这样。");
            }
        }

        /// <summary>
        /// 世界坐标的框 → 视图自己坐标系里的框。
        ///
        /// **这一步不能省。** <c>View.CropBox</c> 的 Min/Max 是在它自己的 Transform 下表达的，
        /// 不是世界坐标。直接把一个世界坐标的框赋进去，Revit 会照着视图的局部系去理解那两个点——
        /// 实测（Revit 2019 三维视图）：构件被挤到画面左下角，裁剪区落在一片空地上，
        /// 而 <c>cropBoxActive</c> 照样是 true。**只查标志位的断言会全过，图却更差了。**
        ///
        /// 深度方向（局部 Z）保留视图原来的取值：那一维对平面视图是视图范围、
        /// 对剖面是可见深度，由这里的构件包围盒去改它没有道理。
        /// </summary>
        private static BoundingBoxXYZ ToViewFrame(View view, BoundingBoxXYZ world)
        {
            var current = view.CropBox;
            var transform = current?.Transform ?? Transform.Identity;
            var inverse = transform.Inverse;

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var corner in Corners(world))
            {
                var local = inverse.OfPoint(corner);

                minX = Math.Min(minX, local.X); maxX = Math.Max(maxX, local.X);
                minY = Math.Min(minY, local.Y); maxY = Math.Max(maxY, local.Y);
            }

            var box = new BoundingBoxXYZ { Transform = transform };

            var keepMinZ = current?.Min?.Z ?? 0;
            var keepMaxZ = current?.Max?.Z ?? 0;

            box.Min = new XYZ(minX, minY, keepMinZ);
            box.Max = new XYZ(maxX, maxY, keepMaxZ);

            return box;
        }

        private static IEnumerable<XYZ> Corners(BoundingBoxXYZ box)
        {
            var min = box.Min;
            var max = box.Max;

            yield return new XYZ(min.X, min.Y, min.Z);
            yield return new XYZ(max.X, min.Y, min.Z);
            yield return new XYZ(min.X, max.Y, min.Z);
            yield return new XYZ(max.X, max.Y, min.Z);
            yield return new XYZ(min.X, min.Y, max.Z);
            yield return new XYZ(max.X, min.Y, max.Z);
            yield return new XYZ(min.X, max.Y, max.Z);
            yield return new XYZ(max.X, max.Y, max.Z);
        }

        /// <summary>
        /// 标高线、轴网、剖面框这些基准图元**不受剖切框约束**：
        /// 它们有自己的三维延伸范围，收紧了剖切框，它们照样从画面这头拉到那头，
        /// 而建筑缩在角落里。想要一张"有效内容占比高"的图，就得在这个视图里把它们关掉。
        ///
        /// 只关这个视图（<c>View.SetCategoryHidden</c>），不动项目的其他地方；
        /// cropActive: false 会把它们一并恢复。
        /// </summary>
        private static void SetDatumsHidden(Document document, View view, bool hidden)
        {
            foreach (var category in DatumCategories)
            {
                try
                {
                    var id = new ElementId(category);
                    if (!view.CanCategoryBeHidden(id)) continue;

                    view.SetCategoryHidden(id, hidden);
                }
                catch { /* 某个类别在这个视图里不存在，不影响其余的 */ }
            }
        }

        private static readonly BuiltInCategory[] DatumCategories =
        {
            BuiltInCategory.OST_Levels,
            BuiltInCategory.OST_Grids,
            BuiltInCategory.OST_SectionBox
        };

        private static void Relax(Document document, View view, View3D view3D)
        {
            try { view.CropBoxActive = false; } catch { }

            SetDatumsHidden(document, view, false);

            if (view3D == null) return;

            try { view3D.IsSectionBoxActive = false; } catch { }
        }

        /// <summary>取景依据的构件。给了 ID 就用那些，否则取视图里所有可见的模型构件。</summary>
        private static List<Element> Collect(Document document, View view, List<string> ids)
        {
            if (ids != null && ids.Count > 0)
            {
                var picked = new List<Element>();
                foreach (var raw in ids)
                {
                    string problem;
                    var element = ElementRef.Resolve(document, raw, out problem);

                    if (element == null)
                        throw new ToolFailureException(McpDomainError.ElementNotFound,
                            "elementIds 里的 \"" + raw + "\"：" + problem);

                    picked.Add(element);
                }

                return picked;
            }

            return new FilteredElementCollector(document, view.Id)
                .WhereElementIsNotElementType()
                .Where(e => e.Category != null && e.Category.HasMaterialQuantities)
                .ToList();
        }

        /// <summary>
        /// 这些构件的总包围盒。
        ///
        /// 按**视图**取包围盒而不是 null：视图里被裁掉或隐藏的构件不该把范围撑大。
        /// 取不到就退到模型包围盒——总比因为一个构件算不出来就整个失败强。
        /// </summary>
        private static BoundingBoxXYZ Union(IList<Element> elements, View view)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            var any = false;

            foreach (var element in elements)
            {
                BoundingBoxXYZ box = null;
                try { box = element.get_BoundingBox(view) ?? element.get_BoundingBox(null); }
                catch { }

                if (box?.Min == null || box.Max == null) continue;

                minX = Math.Min(minX, box.Min.X); minY = Math.Min(minY, box.Min.Y); minZ = Math.Min(minZ, box.Min.Z);
                maxX = Math.Max(maxX, box.Max.X); maxY = Math.Max(maxY, box.Max.Y); maxZ = Math.Max(maxZ, box.Max.Z);
                any = true;
            }

            if (!any) return null;

            return new BoundingBoxXYZ
            {
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };
        }

        private static BoundingBoxXYZ Inflate(BoundingBoxXYZ box, double padding)
        {
            // 完全贴着构件的框会把边线自己切掉一半，看着像没画完
            return new BoundingBoxXYZ
            {
                Min = new XYZ(box.Min.X - padding, box.Min.Y - padding, box.Min.Z - padding),
                Max = new XYZ(box.Max.X + padding, box.Max.Y + padding, box.Max.Z + padding)
            };
        }

        private static void RequireCroppable(View view)
        {
            if (view.IsTemplate)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "「" + SafeName(view) + "」是视图样板，没有取景范围可调。");

            switch (view.ViewType)
            {
                case ViewType.DrawingSheet:
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "「" + SafeName(view) + "」是图纸。要调的是图纸上那些视图各自的取景范围，不是图纸本身。");

                case ViewType.Schedule:
                case ViewType.ColumnSchedule:
                case ViewType.PanelSchedule:
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "「" + SafeName(view) + "」是明细表，没有取景范围。");

                case ViewType.Legend:
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "「" + SafeName(view) + "」是图例视图，取景范围由它自己的内容决定。");
            }
        }

        private static View ResolveView(Document document, string rawId)
        {
            if (string.IsNullOrWhiteSpace(rawId))
            {
                var active = document.ActiveView;
                if (active == null)
                    throw new ToolFailureException(McpDomainError.NoActiveDocument,
                        "没有活动视图，也没给 viewId。");

                return active;
            }

            string problem;
            var element = ElementRef.Resolve(document, rawId, out problem);

            if (element == null)
                throw new ToolFailureException(McpDomainError.ElementNotFound, "viewId " + rawId + "：" + problem);

            var view = element as View;
            if (view == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "viewId " + rawId + " 不是视图，而是「" +
                    (element.Category?.Name ?? element.GetType().Name) + "」。用 revit_list_views 取视图 ID。");

            return view;
        }

        private static BoundingBoxInfo Describe(BoundingBoxXYZ box)
        {
            var minX = Units.Round(Units.FromFeet(box.Min.X));
            var minY = Units.Round(Units.FromFeet(box.Min.Y));
            var minZ = Units.Round(Units.FromFeet(box.Min.Z));
            var maxX = Units.Round(Units.FromFeet(box.Max.X));
            var maxY = Units.Round(Units.FromFeet(box.Max.Y));
            var maxZ = Units.Round(Units.FromFeet(box.Max.Z));

            return new BoundingBoxInfo
            {
                Min = new Point3D { X = minX, Y = minY, Z = minZ },
                Max = new Point3D { X = maxX, Y = maxY, Z = maxZ },
                Center = new Point3D
                {
                    X = Units.Round((minX + maxX) / 2),
                    Y = Units.Round((minY + maxY) / 2),
                    Z = Units.Round((minZ + maxZ) / 2)
                },
                SizeXMm = Units.Round(maxX - minX),
                SizeYMm = Units.Round(maxY - minY),
                SizeZMm = Units.Round(maxZ - minZ)
            };
        }

        private static bool SafeBool(Func<bool> read)
        {
            try { return read(); }
            catch { return false; }
        }

        private static string SafeName(Element element)
        {
            try { return element == null ? null : element.Name; }
            catch { return null; }
        }
    }
}
