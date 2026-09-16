using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    public sealed class LineBasedElementSpec
    {
        [McpParam("BuiltInCategory 名。目前支持 OST_Walls（墙）与 OST_StructuralFraming（梁）", Required = true)]
        public string Category { get; set; }

        [McpParam("族类型 ID，来自 revit_list_types。省略则用该类别的默认类型。" +
                  "墙厚由类型决定，改不了——要 200 厚的墙就挑 thicknessMm 为 200 的类型")]
        public string TypeId { get; set; }

        [McpParam("定位线，坐标用毫米", Required = true)]
        public LocationLine LocationLine { get; set; }

        [McpParam("高度，毫米。仅墙使用，默认 3000")]
        public double? Height { get; set; }

        [McpParam("标高 ID，来自 revit_list_levels。省略则用活动视图所在标高")]
        public string LevelId { get; set; }

        [McpParam("相对标高的偏移，毫米，默认 0")]
        public double? BaseOffset { get; set; }

        [McpParam("是否为结构构件。仅墙使用，默认 false（梁总是结构构件）")]
        public bool? Structural { get; set; }
    }

    public sealed class CreateLineBasedInput
    {
        [McpParam("要创建的构件，一次调用可建多个", Required = true)]
        public List<LineBasedElementSpec> Elements { get; set; }

        [McpParam("数量超过单次上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    [McpTool("revit_create_line_based_elements",
        Title = "创建线定位构件",
        Description = "按定位线批量创建墙或梁。坐标和尺寸一律用毫米，原点与项目坐标系一致。" +
                      "整批要么全部建成、要么一个都不建，且在撤销栈里只占一步——" +
                      "建一圈墙请一次调用传完，不要逐面调用。" +
                      "建之前先用 revit_list_types 挑类型、revit_list_levels 挑标高；" +
                      "建之后用 revit_get_warnings 复查有没有重叠。",
        TimeoutSeconds = 120)]
    public sealed class CreateLineBasedTool : RevitTool<CreateLineBasedInput, CreateElementsOutput>
    {
        private const double DefaultWallHeightMm = 3000.0;

        public override CreateElementsOutput Execute(
            CreateLineBasedInput input, ToolExecutionContext<UIApplication> context)
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
            LineBasedElementSpec spec, int index)
        {
            var category = ParseCategoryAt(spec.Category, index);
            var line = CreateSupport.RequireLine(spec.LocationLine, index);
            var level = CreateSupport.ResolveLevel(document, context, spec.LevelId, index);
            var offsetMm = spec.BaseOffset ?? 0;

            switch (category)
            {
                case BuiltInCategory.OST_Walls:
                    return CreateWall(document, context, spec, index, line, level, offsetMm);

                case BuiltInCategory.OST_StructuralFraming:
                    return CreateBeam(document, context, spec, index, line, level, offsetMm);

                default:
                    throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                        "线定位建模目前只支持 OST_Walls（墙）与 OST_StructuralFraming（梁），收到 " +
                        category + "。管道、风管、桥架需要 MEP 系统与尺寸信息，本服务尚未支持。");
            }
        }

        private static CreatedElement CreateWall(
            Document document, ToolExecutionContext<UIApplication> context,
            LineBasedElementSpec spec, int index, Line line, Level level, double offsetMm)
        {
            var heightMm = spec.Height ?? DefaultWallHeightMm;
            if (heightMm < Mm.MinLength)
                throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                    "墙高必须为正且不小于 " + Mm.MinLength + " 毫米，收到 " + heightMm + "。");

            var wallType = CreateSupport.ResolveType<WallType>(
                document, context, spec.TypeId, BuiltInCategory.OST_Walls, index);

            Wall wall;
            try
            {
                wall = Wall.Create(
                    document,
                    line,
                    wallType.Id,
                    level.Id,
                    Mm.ToFeet(heightMm),
                    Mm.ToFeet(offsetMm),
                    false,                          // flip：朝向由定位线的方向决定，不额外提供翻转
                    spec.Structural ?? false);
            }
            catch (Exception ex)
            {
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "Revit 拒绝创建该墙：" + ex.Message);
            }

            if (wall == null)
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "Revit 未能创建墙，但也没有报错。请检查标高与墙类型是否匹配。");

            return new CreatedElement
            {
                Index = index,
                Id = wall.Id.ToProtocolString(),
                Category = "OST_Walls",
                Type = CreateSupport.SafeName(wallType),
                Level = CreateSupport.SafeName(level),
                LengthMm = Mm.Round(CreateSupport.Distance(spec.LocationLine.P0, spec.LocationLine.P1))
            };
        }

        private static CreatedElement CreateBeam(
            Document document, ToolExecutionContext<UIApplication> context,
            LineBasedElementSpec spec, int index, Line line, Level level, double offsetMm)
        {
            if (spec.Height.HasValue)
                CreateSupport.Once(context,
                    "梁没有「高度」这个概念，height 已被忽略。梁的截面尺寸由族类型决定，" +
                    "抬高用 baseOffset。");

            var symbol = CreateSupport.ResolveType<FamilySymbol>(
                document, context, spec.TypeId, BuiltInCategory.OST_StructuralFraming, index);

            CreateSupport.EnsureActive(symbol);

            // 梁的竖向位置由定位线自己表达，不靠事后设参数：
            // 实测常见的结构框架族上「起点/终点标高偏移」是 Revit 算出来的，写不进去，
            // 结果是梁默默留在标高平面上、只留下一条警告。把高度做进几何里就不会有这种偏差。
            //
            // 规则与点定位工具一致：绝对高度 = 标高 + locationLine 的 z + baseOffset
            var rise = level.Elevation + Mm.ToFeet(offsetMm);
            var placement = Line.CreateBound(
                line.GetEndPoint(0) + XYZ.BasisZ * rise,
                line.GetEndPoint(1) + XYZ.BasisZ * rise);

            FamilyInstance beam;
            try
            {
                beam = document.Create.NewFamilyInstance(placement, symbol, level, StructuralType.Beam);
            }
            catch (Exception ex)
            {
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "Revit 拒绝创建该梁：" + ex.Message);
            }

            if (beam == null)
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "Revit 未能创建梁，但也没有报错。请确认类型「" + CreateSupport.SafeName(symbol) +
                    "」确实是结构框架族。");

            return new CreatedElement
            {
                Index = index,
                Id = beam.Id.ToProtocolString(),
                Category = "OST_StructuralFraming",
                Type = CreateSupport.SafeName(symbol),
                Level = CreateSupport.SafeName(level),
                LengthMm = Mm.Round(CreateSupport.Distance(spec.LocationLine.P0, spec.LocationLine.P1))
            };
        }

        private static BuiltInCategory ParseCategoryAt(string raw, int index)
        {
            try
            {
                return ParseCategory(raw);
            }
            catch (ToolFailureException ex)
            {
                // 批量场景下不说是哪一项的类别写错了，模型只能整批重猜
                throw CreateSupport.Failure(index, ex.Code, ex.Message);
            }
        }
    }
}
