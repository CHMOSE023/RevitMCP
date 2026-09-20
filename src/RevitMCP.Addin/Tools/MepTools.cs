using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    /// <summary>
    /// 本文件的失败构造：把下标前缀写成 <c>curves[i]</c>，而不是通用的 elements[i]。
    ///
    /// 报错里出现的数组名必须是调用方真的传过的那个，否则它会去找一个不存在的参数。
    /// </summary>
    internal static class MepFail
    {
        public static ToolFailureException At(int index, string code, string message)
        {
            return CreateSupport.Failure(index, code, message, "curves", "整批未创建");
        }
    }

    // ==================== 创建 MEP 管线 ====================

    public sealed class MepCurveSpec
    {
        [McpParam("管线种类：duct（风管）、pipe（水管）、conduit（线管）、cableTray（桥架）",
                  Required = true,
                  AllowedValues = new[] { "duct", "pipe", "conduit", "cableTray" })]
        public string Kind { get; set; }

        [McpParam("管线的起止点，毫米", Required = true)]
        public LocationLine LocationLine { get; set; }

        [McpParam("管线类型 ID，来自 revit_list_types（风管查 OST_DuctCurves、" +
                  "水管查 OST_PipeCurves、线管查 OST_Conduit、桥架查 OST_CableTray）。" +
                  "省略则用该类别的第一个可用类型")]
        public string TypeId { get; set; }

        [McpParam("系统类型 ID。duct 与 pipe 必须归属一个系统（送风、给水这类）——" +
                  "省略则用项目里的第一个。conduit 与 cableTray 没有系统的概念，给了会被忽略")]
        public string SystemTypeId { get; set; }

        [McpParam("标高 ID，来自 revit_list_levels。省略则用活动视图所在标高")]
        public string LevelId { get; set; }

        [McpParam("圆形管的直径，毫米。duct、pipe、conduit 用。" +
                  "省略则用类型的默认尺寸")]
        public double? DiameterMm { get; set; }

        [McpParam("矩形管的宽度，毫米。duct 与 cableTray 用。与 diameterMm 二选一")]
        public double? WidthMm { get; set; }

        [McpParam("矩形管的高度，毫米。duct 与 cableTray 用。与 diameterMm 二选一")]
        public double? HeightMm { get; set; }
    }

    public sealed class CreateMepCurvesInput
    {
        [McpParam("要创建的管线，一次调用可建多根", Required = true)]
        public List<MepCurveSpec> Curves { get; set; }

        [McpParam("数量超过单次上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    public sealed class CreatedMepCurve
    {
        [McpParam("对应 curves 数组中的下标，从 0 起")]
        public int Index { get; set; }

        [McpParam("新建管线的 ID")]
        public string Id { get; set; }

        [McpParam("管线种类")]
        public string Kind { get; set; }

        [McpParam("使用的类型名")]
        public string Type { get; set; }

        [McpParam("所属系统名。conduit 与 cableTray 为 null")]
        public string System { get; set; }

        [McpParam("所在标高名")]
        public string Level { get; set; }

        [McpParam("长度，毫米")]
        public double LengthMm { get; set; }

        [McpParam("实际的尺寸描述，如「直径 200」或「400×300」")]
        public string Size { get; set; }
    }

    public sealed class CreateMepCurvesOutput : IReportsAffectedElements
    {
        [McpParam("成功创建的管线数")]
        public int Created { get; set; }

        [McpParam("新建的管线，顺序与入参一致")]
        public List<CreatedMepCurve> Curves { get; set; } = new List<CreatedMepCurve>();

        int IReportsAffectedElements.AffectedElements => Created;
    }

    /// <summary>
    /// 创建风管 / 水管 / 线管 / 桥架。
    ///
    /// 这四样在 Revit 里都是 <c>MEPCurve</c>：一条定位线 + 一个类型 + 一个尺寸。
    /// 差别只在风管和水管要归属一个"系统"，而线管和桥架没有系统的概念——
    /// 这一条差异由 <c>kind</c> 分支处理，其余部分完全一致。
    ///
    /// 注意：这里建的是**单根直管**，不做自动连接与弯头生成。
    /// 两根管的端点重合时 Revit 通常会自动连上，但那是 Revit 的行为而非本工具的承诺。
    /// </summary>
    [McpTool("revit_create_mep_curves",
        Toolsets = new[] { Toolsets.ModelingMep },
        Title = "创建风管/水管/线管/桥架",
        Description = "按定位线批量创建 MEP 管线。坐标与尺寸一律用毫米。" +
                      "建的是**单根直管**，不自动生成弯头与三通——" +
                      "端点重合时 Revit 可能自己连上，但那不是本工具的承诺。" +
                      "风管与水管必须归属一个系统类型，省略则用项目里的第一个（会通过 warnings 告知）。" +
                      "整批要么全成、要么一根都不建。",
        Destructive = false,
        TimeoutSeconds = 180)]
    public sealed class CreateMepCurvesTool : RevitTool<CreateMepCurvesInput, CreateMepCurvesOutput>
    {
        public override CreateMepCurvesOutput Execute(
            CreateMepCurvesInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);
            RequireBatch(input.Curves, input.Confirm, context, "创建");

            var output = new CreateMepCurvesOutput();
            var total = input.Curves.Count;

            for (var index = 0; index < total; index++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var spec = input.Curves[index];
                if (spec == null)
                    throw MepFail.At(index, McpDomainError.InvalidParameter, "该项为 null。");

                output.Curves.Add(CreateOne(document, context, spec, index));
                ProgressTicker.Tick(context.Progress, index + 1, total, "已创建");
            }

            output.Created = output.Curves.Count;
            return output;
        }

        private static CreatedMepCurve CreateOne(
            Document document, ToolExecutionContext<UIApplication> context, MepCurveSpec spec, int index)
        {
            var kind = (spec.Kind ?? string.Empty).Trim().ToLowerInvariant();
            var line = CreateSupport.RequireLine(spec.LocationLine, index);
            var level = CreateSupport.ResolveLevel(document, context, spec.LevelId, index);

            var start = line.GetEndPoint(0);
            var end = line.GetEndPoint(1);

            MEPCurve curve;
            string category;

            switch (kind)
            {
                case "duct":
                    curve = CreateDuct(document, context, spec, index, level, start, end);
                    category = "OST_DuctCurves";
                    break;

                case "pipe":
                    curve = CreatePipe(document, context, spec, index, level, start, end);
                    category = "OST_PipeCurves";
                    break;

                case "conduit":
                    curve = CreateConduit(document, context, spec, index, level, start, end);
                    category = "OST_Conduit";
                    break;

                case "cabletray":
                    curve = CreateCableTray(document, context, spec, index, level, start, end);
                    category = "OST_CableTray";
                    break;

                default:
                    throw MepFail.At(index, McpDomainError.InvalidParameter,
                        "无法识别的 kind \"" + spec.Kind +
                        "\"。可用值：duct（风管）、pipe（水管）、conduit（线管）、cableTray（桥架）。");
            }

            if (curve == null)
                throw MepFail.At(index, McpDomainError.TransactionFailed,
                    "Revit 未能创建" + kind + "，但也没有报错。" +
                    "常见原因是项目里没有载入对应的管线类型——用 revit_list_types 查 " + category + "。");

            ApplySize(document, curve, spec, index, context);

            return new CreatedMepCurve
            {
                Index = index,
                Id = curve.Id.ToProtocolString(),
                Kind = kind,
                Type = AnnotationSupport.TypeNameOf(document, curve),
                System = SystemNameOf(curve),
                Level = AnnotationSupport.SafeName(level),
                LengthMm = Units.Round(Units.FromFeet(start.DistanceTo(end))),
                Size = DescribeSize(curve)
            };
        }

        // ==================== 各类管线 ====================

        private static MEPCurve CreateDuct(
            Document document, ToolExecutionContext<UIApplication> context, MepCurveSpec spec,
            int index, Level level, XYZ start, XYZ end)
        {
            var type = ResolveMepType<DuctType>(
                document, context, spec.TypeId, BuiltInCategory.OST_DuctCurves, index, "风管");

            var systemType = ResolveSystemType<MechanicalSystemType>(
                document, context, spec.SystemTypeId, index, "风管系统");

            try
            {
                return Duct.Create(document, systemType.Id, type.Id, level.Id, start, end);
            }
            catch (Exception ex)
            {
                throw Rejected("风管", ex, index);
            }
        }

        private static MEPCurve CreatePipe(
            Document document, ToolExecutionContext<UIApplication> context, MepCurveSpec spec,
            int index, Level level, XYZ start, XYZ end)
        {
            var type = ResolveMepType<PipeType>(
                document, context, spec.TypeId, BuiltInCategory.OST_PipeCurves, index, "水管");

            var systemType = ResolveSystemType<PipingSystemType>(
                document, context, spec.SystemTypeId, index, "管道系统");

            try
            {
                return Pipe.Create(document, systemType.Id, type.Id, level.Id, start, end);
            }
            catch (Exception ex)
            {
                throw Rejected("水管", ex, index);
            }
        }

        private static MEPCurve CreateConduit(
            Document document, ToolExecutionContext<UIApplication> context, MepCurveSpec spec,
            int index, Level level, XYZ start, XYZ end)
        {
            var type = ResolveMepType<ConduitType>(
                document, context, spec.TypeId, BuiltInCategory.OST_Conduit, index, "线管");

            WarnIgnoredSystem(spec, context, index, "线管");

            try
            {
                return Conduit.Create(document, type.Id, start, end, level.Id);
            }
            catch (Exception ex)
            {
                throw Rejected("线管", ex, index);
            }
        }

        private static MEPCurve CreateCableTray(
            Document document, ToolExecutionContext<UIApplication> context, MepCurveSpec spec,
            int index, Level level, XYZ start, XYZ end)
        {
            var type = ResolveMepType<CableTrayType>(
                document, context, spec.TypeId, BuiltInCategory.OST_CableTray, index, "桥架");

            WarnIgnoredSystem(spec, context, index, "桥架");

            try
            {
                return CableTray.Create(document, type.Id, start, end, level.Id);
            }
            catch (Exception ex)
            {
                throw Rejected("桥架", ex, index);
            }
        }

        // ==================== 类型与系统 ====================

        private static T ResolveMepType<T>(
            Document document, ToolExecutionContext<UIApplication> context,
            string rawId, BuiltInCategory category, int index, string label) where T : ElementType
        {
            if (!string.IsNullOrWhiteSpace(rawId))
            {
                var element = CreateSupport.RequireElement(document, rawId, index);
                var type = element as T;

                if (type == null)
                    throw MepFail.At(index, McpDomainError.InvalidParameter,
                        "typeId " + rawId + " 不是" + label + "类型（它是「" +
                        (element.Category?.Name ?? element.GetType().Name) + "」）。" +
                        "用 revit_list_types 查 " + category + "。");

                return type;
            }

            var candidate = new FilteredElementCollector(document)
                .OfClass(typeof(T))
                .Cast<T>()
                .FirstOrDefault();

            if (candidate == null)
                throw MepFail.At(index, McpDomainError.InvalidParameter,
                    "本项目里没有任何" + label + "类型，建不了。" +
                    "这通常说明项目样板不含 MEP 内容——需要用户先在 Revit 里载入对应的族与类型。");

            CreateSupport.Once(context, "未指定 typeId，使用" + label + "类型「" +
                                        AnnotationSupport.SafeName(candidate) + "」。");
            return candidate;
        }

        private static T ResolveSystemType<T>(
            Document document, ToolExecutionContext<UIApplication> context,
            string rawId, int index, string label) where T : ElementType
        {
            if (!string.IsNullOrWhiteSpace(rawId))
            {
                var element = CreateSupport.RequireElement(document, rawId, index);
                var type = element as T;

                if (type == null)
                    throw MepFail.At(index, McpDomainError.InvalidParameter,
                        "systemTypeId " + rawId + " 不是" + label + "类型（它是「" +
                        (element.Category?.Name ?? element.GetType().Name) + "」）。");

                return type;
            }

            var candidate = new FilteredElementCollector(document)
                .OfClass(typeof(T))
                .Cast<T>()
                .FirstOrDefault();

            if (candidate == null)
                throw MepFail.At(index, McpDomainError.InvalidParameter,
                    "本项目里没有任何" + label + "，建不了管线。" +
                    "需要用户先在 Revit 里定义系统类型。");

            // 系统类型选错了，管线的颜色、过滤器、明细表归属全会跟着错，
            // 而模型上看不出任何异样——所以这个默认值一定要说出来
            CreateSupport.Once(context,
                "未指定 systemTypeId，使用" + label + "「" + AnnotationSupport.SafeName(candidate) +
                "」。系统归属会影响管线的显示与统计，不确定的话请显式指定。");

            return candidate;
        }

        private static void WarnIgnoredSystem(
            MepCurveSpec spec, ToolExecutionContext<UIApplication> context, int index, string label)
        {
            if (string.IsNullOrWhiteSpace(spec.SystemTypeId)) return;

            CreateSupport.Once(context,
                "curves[" + index + "]：" + label + "没有系统的概念，systemTypeId 被忽略。");
        }

        // ==================== 尺寸 ====================

        /// <summary>
        /// 设尺寸。
        ///
        /// Create 出来的管线用的是类型的默认尺寸，改尺寸只能事后写参数。
        /// 圆形与矩形是互斥的两套参数，写错一套 Revit 会静默忽略——
        /// 所以这里先按给了哪些参数判断意图，再确认写进去的值确实生效了。
        /// </summary>
        private static void ApplySize(
            Document document, MEPCurve curve, MepCurveSpec spec, int index,
            ToolExecutionContext<UIApplication> context)
        {
            var hasRound = spec.DiameterMm != null;
            var hasRectangular = spec.WidthMm != null || spec.HeightMm != null;

            if (hasRound && hasRectangular)
                throw MepFail.At(index, McpDomainError.InvalidParameter,
                    "diameterMm 与 widthMm/heightMm 只能给一组——" +
                    "一根管子要么是圆的要么是方的。");

            if (!hasRound && !hasRectangular) return;

            // Revit 内部一律用英尺，这里直接按内部单位写，绕开 SetValueString 的语言差异
            if (hasRound)
            {
                Write(curve, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM,
                      spec.DiameterMm.Value, "diameterMm", index, context);
                return;
            }

            if (spec.WidthMm != null)
                Write(curve, BuiltInParameter.RBS_CURVE_WIDTH_PARAM,
                      spec.WidthMm.Value, "widthMm", index, context);

            if (spec.HeightMm != null)
                Write(curve, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM,
                      spec.HeightMm.Value, "heightMm", index, context);
        }

        private static void Write(
            MEPCurve curve, BuiltInParameter which, double millimeters, string field,
            int index, ToolExecutionContext<UIApplication> context)
        {
            if (millimeters < Units.MinLength)
                throw MepFail.At(index, McpDomainError.InvalidParameter,
                    field + " 必须大于 " + Units.MinLength + " 毫米，收到 " + Format(millimeters) + "。");

            var parameter = curve.get_Parameter(which);

            if (parameter == null || parameter.IsReadOnly)
            {
                CreateSupport.Once(context,
                    "curves[" + index + "]：这类管线不支持设置 " + field +
                    "（该尺寸由类型决定，或与管线形状不符），管线已按类型默认尺寸创建。");
                return;
            }

            if (!parameter.Set(Units.ToFeet(millimeters)))
                throw MepFail.At(index, McpDomainError.TransactionFailed,
                    "Revit 拒绝把 " + field + " 设为 " + Format(millimeters) +
                    " 毫米。该尺寸可能不在类型允许的尺寸表里——" +
                    "MEP 管线的尺寸通常只能取「机械设置」里预定义的那几档。");
        }

        /// <summary>
        /// 描述管线的实际尺寸。
        ///
        /// Revit 的「尺寸」参数（RBS_CALCULATED_SIZE）是算出来的，
        /// 刚创建完还没算的时候是空的——实测矩形风管就会这样。
        /// 所以读不到就退回去读宽高/直径，而不是给调用方一个 null。
        /// </summary>
        private static string DescribeSize(MEPCurve curve)
        {
            try
            {
                var size = curve.get_Parameter(BuiltInParameter.RBS_CALCULATED_SIZE)?.AsString();
                if (!string.IsNullOrWhiteSpace(size)) return size;
            }
            catch { /* 落到下面自己拼 */ }

            var width = ReadMm(curve, BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
            var height = ReadMm(curve, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);

            if (width != null && height != null)
                return Format(width.Value) + "×" + Format(height.Value);

            var diameter = ReadMm(curve, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
            if (diameter != null) return "直径 " + Format(diameter.Value);

            try { return "直径 " + Format(Units.FromFeet(curve.Diameter)); }
            catch { return null; }
        }

        private static double? ReadMm(MEPCurve curve, BuiltInParameter which)
        {
            try
            {
                var parameter = curve.get_Parameter(which);
                if (parameter == null || !parameter.HasValue) return null;

                var value = Units.Round(Units.FromFeet(parameter.AsDouble()));
                return value > 0 ? value : (double?)null;
            }
            catch { return null; }
        }

        private static string SystemNameOf(MEPCurve curve)
        {
            try
            {
                var parameter = curve.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM);
                return parameter?.AsString();
            }
            catch { return null; }
        }

        private static ToolFailureException Rejected(string label, Exception ex, int index)
        {
            return MepFail.At(index, McpDomainError.TransactionFailed,
                "Revit 拒绝创建" + label + "：" + ex.Message);
        }

        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    // ==================== MEP 系统清单 ====================

    public sealed class ListMepSystemsInput
    {
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档")]
        public string DocumentId { get; set; }

        [McpParam("只列这一类：mechanical（风）、piping（水）、electrical（电）。省略则全给",
                  AllowedValues = new[] { "mechanical", "piping", "electrical" })]
        public string Discipline { get; set; }

        [McpParam("是否列出系统类型（而不是系统实例），默认 false。" +
                  "建管线要填的 systemTypeId 来自系统**类型**，所以建模前用 true")]
        public bool? Types { get; set; }
    }

    public sealed class MepSystemInfo
    {
        [McpParam("系统或系统类型的 ID")]
        public string Id { get; set; }

        [McpParam("名称")]
        public string Name { get; set; }

        [McpParam("专业：mechanical / piping / electrical")]
        public string Discipline { get; set; }

        [McpParam("系统分类，如「送风」「循环供水」。系统类型才有")]
        public string Classification { get; set; }

        [McpParam("这个系统里有多少构件。仅列系统实例时有值")]
        public int? ElementCount { get; set; }
    }

    public sealed class ListMepSystemsOutput
    {
        [McpParam("总数")]
        public int Total { get; set; }

        [McpParam("返回的是系统类型（true）还是系统实例（false）")]
        public bool IsTypes { get; set; }

        [McpParam("列表")]
        public List<MepSystemInfo> Systems { get; set; } = new List<MepSystemInfo>();
    }

    [McpTool("revit_list_mep_systems",
        Toolsets = new[] { Toolsets.ModelingMep },
        Title = "列出 MEP 系统",
        Description = "列出模型里的 MEP 系统实例，或（types: true 时）可用的系统类型。" +
                      "**建管线前用 types: true 查 systemTypeId**——" +
                      "系统归属决定管线的显示颜色与统计归类，选错了模型上看不出来。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class ListMepSystemsTool : RevitTool<ListMepSystemsInput, ListMepSystemsOutput>
    {
        public override ListMepSystemsOutput Execute(
            ListMepSystemsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = ResolveDocument(context, input.DocumentId);
            var wanted = ParseDiscipline(input.Discipline);
            var types = input.Types ?? false;

            var output = new ListMepSystemsOutput { IsTypes = types };

            if (types)
            {
                Collect<MechanicalSystemType>(document, output, wanted, "mechanical");
                Collect<PipingSystemType>(document, output, wanted, "piping");

                // 电气没有"系统类型"这种图元——Revit 里 ElectricalSystemType 是个枚举
                // （动力/数据/照明…），不是可以挑选的项目资源。说出来，
                // 免得调用方以为是本项目缺内容而反复去找
                if (wanted == null || wanted == "electrical")
                    context.Warnings.Add(
                        "电气系统没有可供选择的「系统类型」图元——" +
                        "Revit 里电气的系统类别是固定枚举，不是项目资源。" +
                        "创建线管与桥架也不需要 systemTypeId。");
            }
            else
            {
                CollectInstances<MechanicalSystem>(document, output, wanted, "mechanical");
                CollectInstances<PipingSystem>(document, output, wanted, "piping");
                CollectInstances<ElectricalSystem>(document, output, wanted, "electrical");
            }

            output.Systems = output.Systems
                .OrderBy(s => s.Discipline, StringComparer.Ordinal)
                .ThenBy(s => s.Name, StringComparer.CurrentCulture)
                .ToList();

            output.Total = output.Systems.Count;

            if (output.Total == 0)
                context.Warnings.Add(
                    "没有找到任何 MEP " + (types ? "系统类型" : "系统") +
                    "。这个项目可能不含 MEP 内容，或者用的是不带 MEP 的项目样板。");

            return output;
        }

        private static void Collect<T>(
            Document document, ListMepSystemsOutput output, string wanted, string discipline)
            where T : ElementType
        {
            if (wanted != null && wanted != discipline) return;

            foreach (var type in new FilteredElementCollector(document).OfClass(typeof(T)).Cast<T>())
            {
                output.Systems.Add(new MepSystemInfo
                {
                    Id = type.Id.ToProtocolString(),
                    Name = AnnotationSupport.SafeName(type),
                    Discipline = discipline,
                    Classification = ClassificationOf(type)
                });
            }
        }

        private static void CollectInstances<T>(
            Document document, ListMepSystemsOutput output, string wanted, string discipline)
            where T : MEPSystem
        {
            if (wanted != null && wanted != discipline) return;

            foreach (var system in new FilteredElementCollector(document).OfClass(typeof(T)).Cast<T>())
            {
                var info = new MepSystemInfo
                {
                    Id = system.Id.ToProtocolString(),
                    Name = AnnotationSupport.SafeName(system),
                    Discipline = discipline
                };

                try { info.ElementCount = system.Elements?.Size ?? 0; }
                catch { /* 读不到成员数不影响其他字段 */ }

                output.Systems.Add(info);
            }
        }

        private static string ClassificationOf(Element type)
        {
            try
            {
                var parameter = type.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM);
                var value = parameter?.AsValueString();

                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch { return null; }
        }

        private static string ParseDiscipline(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            switch (value.Trim().ToLowerInvariant())
            {
                case "mechanical": return "mechanical";
                case "piping": return "piping";
                case "electrical": return "electrical";

                default:
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "无法识别的 discipline \"" + value +
                        "\"。可用值：mechanical（风）、piping（水）、electrical（电）。");
            }
        }
    }
}
