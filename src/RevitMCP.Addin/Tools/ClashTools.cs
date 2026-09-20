using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    // ==================== 碰撞检查 ====================

    public sealed class ClashSetSpec
    {
        [McpParam("这一组的类别，如 OST_Walls、OST_DuctCurves。可省略 OST_ 前缀。" +
                  "与 elementIds 二选一")]
        public string Category { get; set; }

        [McpParam("这一组的构件 ID。与 category 二选一。ElementId 与 uniqueId 两种写法都接受")]
        public List<string> ElementIds { get; set; }

        [McpParam("链接模型实例 ID，来自 revit_list_links。" +
                  "给了它就在那个链接模型里取构件——结构与机电分模型时靠它做专业间检查")]
        public string LinkInstanceId { get; set; }
    }

    public sealed class CheckClashesInput
    {
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档")]
        public string DocumentId { get; set; }

        [McpParam("第一组构件", Required = true)]
        public ClashSetSpec SetA { get; set; }

        [McpParam("第二组构件。省略则在 setA 内部两两互检", Required = false)]
        public ClashSetSpec SetB { get; set; }

        [McpParam("只检查这个阶段创建的构件，ID 来自 revit_list_phases。" +
                  "改造项目不给它，「现有」与「拆除」的构件会算出一堆假碰撞")]
        public string PhaseId { get; set; }

        [McpParam("最多返回多少条碰撞，默认 200，上限 1000")]
        public int? Limit { get; set; }
    }

    public sealed class ClashPair
    {
        [McpParam("A 组构件 ID")]
        public string IdA { get; set; }

        [McpParam("A 组构件名")]
        public string NameA { get; set; }

        [McpParam("A 组构件类别")]
        public string CategoryA { get; set; }

        [McpParam("B 组构件 ID")]
        public string IdB { get; set; }

        [McpParam("B 组构件名")]
        public string NameB { get; set; }

        [McpParam("B 组构件类别")]
        public string CategoryB { get; set; }

        [McpParam("两者包围盒交集的中心点，毫米。拿它去 revit_set_selection 能直接定位到现场")]
        public Point3D Location { get; set; }
    }

    public sealed class CheckClashesOutput
    {
        [McpParam("A 组的构件数")]
        public int CountA { get; set; }

        [McpParam("B 组的构件数")]
        public int CountB { get; set; }

        [McpParam("找到的碰撞对数（不受 limit 影响）")]
        public int Total { get; set; }

        [McpParam("本次实际返回的条数")]
        public int Returned { get; set; }

        [McpParam("是否因 limit 而被截断")]
        public bool Truncated { get; set; }

        [McpParam("碰撞列表")]
        public List<ClashPair> Clashes { get; set; } = new List<ClashPair>();

        [McpParam("所有卷入碰撞的构件 ID，去重后。直接传给 revit_set_selection 可在 Revit 里高亮")]
        public List<string> InvolvedElementIds { get; set; } = new List<string>();
    }

    /// <summary>
    /// 硬碰撞检查。
    ///
    /// 用的是 Revit 自己的 <c>ElementIntersectsElementFilter</c>——
    /// 判定标准是**实体几何真的相交**，不是包围盒重叠。
    /// 这一点值得说明：包围盒重叠会把一大堆根本没碰上的斜撑、弧形墙报成碰撞，
    /// 那种结果多到没人会去看。
    ///
    /// 只做硬碰撞，不做间隙（clearance）检查——后者需要定义每一对专业的最小间距，
    /// 那是个规则库的问题，不是一个工具参数能表达的。
    /// </summary>
    [McpTool("revit_check_clashes",
        Title = "碰撞检查",
        Description = "在两组构件之间做硬碰撞检查（实体几何真的相交，不是包围盒重叠）。" +
                      "省略 setB 则在 setA 内部两两互检。" +
                      "支持用 linkInstanceId 检查链接模型里的构件——专业间碰撞靠它。" +
                      "**改造项目请给 phaseId**，否则「现有」与「新建」的构件会算出一堆假碰撞。" +
                      "只做硬碰撞，不做最小间隙检查。",
        ReadOnly = true,
        TimeoutSeconds = 600)]
    public sealed class CheckClashesTool : RevitTool<CheckClashesInput, CheckClashesOutput>
    {
        private const int DefaultLimit = 200;
        private const int MaxLimit = 1000;

        public override CheckClashesOutput Execute(
            CheckClashesInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = ResolveDocument(context, input.DocumentId);
            var limit = Math.Min(Math.Max(input.Limit ?? DefaultLimit, 1), MaxLimit);

            if (input.SetA == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter, "setA 不能为空。");

            var phaseId = ResolvePhaseId(document, input.PhaseId);

            var setA = Resolve(document, input.SetA, "setA", phaseId);
            var selfCheck = input.SetB == null;
            var setB = selfCheck ? setA : Resolve(document, input.SetB, "setB", phaseId);

            var output = new CheckClashesOutput { CountA = setA.Count, CountB = setB.Count };

            if (setA.Count == 0 || setB.Count == 0)
            {
                context.Warnings.Add(
                    "有一组是空的（A " + setA.Count + " 个、B " + setB.Count + " 个），没什么可检查的。" +
                    "先用 revit_query_elements 确认这些类别在模型里确实有构件。");
                return output;
            }

            var pairs = new List<ClashPair>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var involved = new HashSet<string>(StringComparer.Ordinal);

            var total = setA.Count;

            for (var index = 0; index < total; index++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var a = setA[index];

                // 每个 A 都拿 B 的 ID 集合重新建一个 collector：
                // Revit 的相交过滤器走空间索引，比把 B 拉进托管代码逐对算快一个数量级
                var hits = FindIntersecting(document, a, setB, input.SetB?.LinkInstanceId);

                foreach (var b in hits)
                {
                    if (a.Id.GetValue() == b.Id.GetValue()) continue;

                    // 自检时 (a,b) 与 (b,a) 是同一处碰撞，报两遍等于把结果翻倍
                    var key = Key(a, b);
                    if (!seen.Add(key)) continue;

                    pairs.Add(Describe(a, b));
                    involved.Add(a.Id.ToProtocolString());
                    involved.Add(b.Id.ToProtocolString());
                }

                ProgressTicker.Tick(context.Progress, index + 1, total, "已检查");
            }

            output.Total = pairs.Count;
            output.Truncated = pairs.Count > limit;
            output.Clashes = pairs.Take(limit).ToList();
            output.Returned = output.Clashes.Count;
            output.InvolvedElementIds = involved.ToList();

            return output;
        }

        private static List<Element> FindIntersecting(
            Document document, Element a, IList<Element> setB, string linkInstanceId)
        {
            var ids = setB.Select(e => e.Id).ToList();
            if (ids.Count == 0) return new List<Element>();

            try
            {
                // B 组来自链接模型时，构件属于另一个 Document，
                // 只能在那个文档里建 collector，用带变换的过滤器去比
                var scope = setB[0].Document;

                var filter = ReferenceEquals(scope, a.Document)
                    ? (ElementFilter)new ElementIntersectsElementFilter(a)
                    : BuildLinkedFilter(document, a, linkInstanceId);

                if (filter == null) return new List<Element>();

                return new FilteredElementCollector(scope, ids)
                    .WherePasses(filter)
                    .ToList();
            }
            catch
            {
                // 个别构件没有可用的实体几何，相交过滤器会拒绝它。
                // 跳过这一个而不是让整次检查失败
                return new List<Element>();
            }
        }

        /// <summary>
        /// 跨链接模型的相交过滤。
        ///
        /// 链接模型有自己的坐标系，直接拿本文档的构件去比会全部落空。
        /// 用 <c>ElementIntersectsSolidFilter</c> + 反向变换：
        /// 把本文档构件的实体搬到链接模型的坐标系里再比。
        /// </summary>
        private static ElementFilter BuildLinkedFilter(Document document, Element a, string linkInstanceId)
        {
            if (string.IsNullOrWhiteSpace(linkInstanceId)) return null;

            // 走统一寻址。这里曾经只认十进制 ID——外层用 uniqueId 传进来时
            // 校验能过，到这一步却悄悄返回 null，结果是跨链接碰撞一条都查不到
            string problem;
            var instance = ElementRef.Resolve(document, linkInstanceId, out problem) as RevitLinkInstance;
            if (instance == null) return null;

            var solid = LargestSolid(a);
            if (solid == null) return null;

            try
            {
                var inverse = instance.GetTotalTransform().Inverse;
                var moved = SolidUtils.CreateTransformed(solid, inverse);

                return new ElementIntersectsSolidFilter(moved);
            }
            catch { return null; }
        }

        private static Solid LargestSolid(Element element)
        {
            try
            {
                var geometry = element.get_Geometry(new Options { ComputeReferences = false });
                if (geometry == null) return null;

                Solid best = null;

                foreach (var item in geometry)
                {
                    var solid = item as Solid;

                    if (solid == null)
                    {
                        var instance = item as GeometryInstance;
                        if (instance == null) continue;

                        foreach (var inner in instance.GetInstanceGeometry())
                        {
                            var innerSolid = inner as Solid;
                            if (innerSolid != null && innerSolid.Volume > (best?.Volume ?? 0))
                                best = innerSolid;
                        }

                        continue;
                    }

                    if (solid.Volume > (best?.Volume ?? 0)) best = solid;
                }

                return best;
            }
            catch { return null; }
        }

        private List<Element> Resolve(Document document, ClashSetSpec spec, string field, long? phaseId)
        {
            if (spec == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter, field + " 不能为空。");

            var scope = document;

            if (!string.IsNullOrWhiteSpace(spec.LinkInstanceId))
            {
                var instance = RequireElement(document, spec.LinkInstanceId) as RevitLinkInstance;

                if (instance == null)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        field + ".linkInstanceId " + spec.LinkInstanceId +
                        " 不是链接模型实例。用 revit_list_links 取 instanceIds 里的 ID。");

                scope = instance.GetLinkDocument();

                if (scope == null)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "链接模型当前没有载入，里面的构件取不到。" +
                        "请让用户在 Revit 的「管理链接」里先重新载入它。");
            }

            var hasIds = spec.ElementIds != null && spec.ElementIds.Count > 0;
            var hasCategory = !string.IsNullOrWhiteSpace(spec.Category);

            if (hasIds == hasCategory)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    field + " 必须给 category 或 elementIds 其中之一，不能都给也不能都不给。");

            List<Element> elements;

            if (hasIds)
            {
                elements = spec.ElementIds.Select(raw => RequireElement(scope, raw)).ToList();
            }
            else
            {
                elements = new FilteredElementCollector(scope)
                    .OfCategory(ParseCategory(spec.Category))
                    .WhereElementIsNotElementType()
                    .ToList();
            }

            if (phaseId != null) elements = elements.Where(e => CreatedIn(e, phaseId.Value)).ToList();

            return elements;
        }

        private long? ResolvePhaseId(Document document, string rawId)
        {
            if (string.IsNullOrWhiteSpace(rawId)) return null;

            var phase = RequireElement(document, rawId) as Phase;

            if (phase == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "phaseId " + rawId + " 不是阶段。用 revit_list_phases 取 ID。");

            return phase.Id.GetValue();
        }

        private static bool CreatedIn(Element element, long phaseId)
        {
            try
            {
                var parameter = element.get_Parameter(BuiltInParameter.PHASE_CREATED);
                if (parameter == null || !parameter.HasValue) return false;

                var id = parameter.AsElementId();
                return id != null && id.GetValue() == phaseId;
            }
            catch { return false; }
        }

        private static string Key(Element a, Element b)
        {
            var x = a.Id.GetValue();
            var y = b.Id.GetValue();

            return x < y ? x + "|" + y : y + "|" + x;
        }

        private static ClashPair Describe(Element a, Element b)
        {
            return new ClashPair
            {
                IdA = a.Id.ToProtocolString(),
                NameA = AnnotationSupport.SafeName(a),
                CategoryA = a.Category?.Name,
                IdB = b.Id.ToProtocolString(),
                NameB = AnnotationSupport.SafeName(b),
                CategoryB = b.Category?.Name,
                Location = Overlap(a, b)
            };
        }

        /// <summary>两者包围盒交集的中心。算不出来时退回 A 的包围盒中心。</summary>
        private static Point3D Overlap(Element a, Element b)
        {
            var boxA = SafeBox(a);
            var boxB = SafeBox(b);

            if (boxA == null) return null;

            if (boxB == null) return ToPoint((boxA.Min + boxA.Max) / 2);

            var min = new XYZ(
                Math.Max(boxA.Min.X, boxB.Min.X),
                Math.Max(boxA.Min.Y, boxB.Min.Y),
                Math.Max(boxA.Min.Z, boxB.Min.Z));

            var max = new XYZ(
                Math.Min(boxA.Max.X, boxB.Max.X),
                Math.Min(boxA.Max.Y, boxB.Max.Y),
                Math.Min(boxA.Max.Z, boxB.Max.Z));

            // 交集为空说明两者的包围盒不重叠，但实体相交过滤器又说它们碰上了——
            // 跨链接模型时坐标系不同就会这样。那种情况给 A 的中心更有用
            if (min.X > max.X || min.Y > max.Y || min.Z > max.Z)
                return ToPoint((boxA.Min + boxA.Max) / 2);

            return ToPoint((min + max) / 2);
        }

        private static BoundingBoxXYZ SafeBox(Element element)
        {
            try { return element.get_BoundingBox(null); }
            catch { return null; }
        }

        private static Point3D ToPoint(XYZ point)
        {
            return new Point3D
            {
                X = Units.Round(Units.FromFeet(point.X)),
                Y = Units.Round(Units.FromFeet(point.Y)),
                Z = Units.Round(Units.FromFeet(point.Z))
            };
        }
    }

    // ==================== 项目位置 ====================

    public sealed class ProjectLocationInput
    {
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档")]
        public string DocumentId { get; set; }
    }

    public sealed class ProjectLocationOutput
    {
        [McpParam("当前项目位置的名称")]
        public string LocationName { get; set; }

        [McpParam("地点名（「上海，中国」这类）")]
        public string PlaceName { get; set; }

        [McpParam("纬度，度。北纬为正")]
        public double? Latitude { get; set; }

        [McpParam("经度，度。东经为正")]
        public double? Longitude { get; set; }

        [McpParam("时区，相对 UTC 的小时数")]
        public double? TimeZone { get; set; }

        [McpParam("测量点相对项目基点的东西向偏移，毫米。东为正")]
        public double? EastWestMm { get; set; }

        [McpParam("测量点相对项目基点的南北向偏移，毫米。北为正")]
        public double? NorthSouthMm { get; set; }

        [McpParam("测量点相对项目基点的高程偏移，毫米")]
        public double? ElevationMm { get; set; }

        [McpParam("正北相对项目北的旋转角，度。逆时针为正")]
        public double? AngleToTrueNorthDegrees { get; set; }

        [McpParam("项目里定义的所有位置名。多个位置常见于同一场地的多方案比选")]
        public List<string> AvailableLocations { get; set; } = new List<string>();
    }

    /// <summary>
    /// 项目位置与地理坐标。
    ///
    /// 这些数字决定日照分析、场地定位和与其他模型的对齐。
    /// 对导出 IFC / NWC 给别的专业尤其重要——
    /// 测量点对不上，两个模型在别人的软件里会差出几十米。
    /// </summary>
    [McpTool("revit_get_project_location",
        Title = "查看项目位置",
        Description = "返回项目的地理位置（经纬度、时区）与测量点相对项目基点的偏移、正北角。" +
                      "导出 IFC/NWC 交给别的专业之前值得核对一遍——" +
                      "测量点对不上，两个模型在别人的软件里会差出几十米，而各自看都是正常的。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class ProjectLocationTool : RevitTool<ProjectLocationInput, ProjectLocationOutput>
    {
        public override ProjectLocationOutput Execute(
            ProjectLocationInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = ResolveDocument(context, input.DocumentId);
            var output = new ProjectLocationOutput();

            try
            {
                var site = document.SiteLocation;
                if (site != null)
                {
                    output.PlaceName = site.PlaceName;

                    // Revit 内部用弧度存经纬度
                    output.Latitude = Math.Round(site.Latitude * 180.0 / Math.PI, 6);
                    output.Longitude = Math.Round(site.Longitude * 180.0 / Math.PI, 6);
                    output.TimeZone = site.TimeZone;
                }
            }
            catch { /* 族文档没有场地信息 */ }

            try
            {
                var active = document.ActiveProjectLocation;
                if (active != null)
                {
                    output.LocationName = AnnotationSupport.SafeName(active);

                    var position = active.GetProjectPosition(XYZ.Zero);
                    if (position != null)
                    {
                        output.EastWestMm = Units.Round(Units.FromFeet(position.EastWest));
                        output.NorthSouthMm = Units.Round(Units.FromFeet(position.NorthSouth));
                        output.ElevationMm = Units.Round(Units.FromFeet(position.Elevation));
                        output.AngleToTrueNorthDegrees = Math.Round(position.Angle * 180.0 / Math.PI, 6);
                    }
                }
            }
            catch { /* 拿不到位置不影响已经读到的场地信息 */ }

            try
            {
                foreach (ProjectLocation location in document.ProjectLocations)
                {
                    var name = AnnotationSupport.SafeName(location);
                    if (name != null) output.AvailableLocations.Add(name);
                }
            }
            catch { /* 列不出来就不列 */ }

            if (output.Latitude == null && output.EastWestMm == null)
                context.Warnings.Add(
                    "读不到这个文档的项目位置信息——族文档没有场地与位置的概念。");

            return output;
        }
    }
}
