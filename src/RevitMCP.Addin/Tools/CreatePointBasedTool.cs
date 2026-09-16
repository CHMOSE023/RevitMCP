using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    public sealed class PointBasedElementSpec
    {
        [McpParam("BuiltInCategory 名，如 OST_Doors、OST_Windows、OST_Furniture、OST_GenericModel", Required = true)]
        public string Category { get; set; }

        [McpParam("族类型 ID，来自 revit_list_types。省略则用该类别的默认类型。" +
                  "门窗尺寸由类型决定，挑类型即是挑尺寸")]
        public string TypeId { get; set; }

        [McpParam("插入点，毫米。门窗放在宿主墙上，点会被投影到墙上", Required = true)]
        public Point3D LocationPoint { get; set; }

        [McpParam("标高 ID，来自 revit_list_levels。省略则用活动视图所在标高")]
        public string LevelId { get; set; }

        [McpParam("相对标高的高度偏移，毫米，默认 0。与 locationPoint.z 效果相加；" +
                  "对门窗即窗台高度（门通常填 0）")]
        public double? BaseOffset { get; set; }

        [McpParam("绕竖直轴旋转的角度，度，逆时针为正。门窗的朝向由宿主墙决定，不适用")]
        public double? Rotation { get; set; }

        [McpParam("宿主墙 ID。门窗必须依附于墙；省略时自动找离插入点最近的墙")]
        public string HostWallId { get; set; }

        [McpParam("翻转朝向（门窗的内外方向），默认 false")]
        public bool? FacingFlipped { get; set; }

        [McpParam("翻转左右（门的开启方向），默认 false")]
        public bool? HandFlipped { get; set; }
    }

    public sealed class CreatePointBasedInput
    {
        [McpParam("要创建的构件，一次调用可建多个", Required = true)]
        public List<PointBasedElementSpec> Elements { get; set; }

        [McpParam("数量超过单次上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    [McpTool("revit_create_point_based_elements",
        Title = "创建点定位构件",
        Description = "按插入点批量创建门、窗、家具等点定位构件。坐标一律用毫米。" +
                      "整批要么全部建成、要么一个都不建，且在撤销栈里只占一步。" +
                      "门窗必须依附于墙：给 hostWallId，或把插入点放在墙上让工具自己找。" +
                      "建之前先用 revit_list_types 确认对应的族已载入本项目——没载入的族无法创建。",
        Destructive = false,
        TimeoutSeconds = 120)]
    public sealed class CreatePointBasedTool : RevitTool<CreatePointBasedInput, CreateElementsOutput>
    {
        /// <summary>自动找宿主墙时，插入点到墙定位线的最大容许距离。超过就要求显式给 hostWallId。</summary>
        private const double MaxHostSearchMm = 1000.0;

        public override CreateElementsOutput Execute(
            CreatePointBasedInput input, ToolExecutionContext<UIApplication> context)
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
            PointBasedElementSpec spec, int index)
        {
            var category = ParseCategoryAt(spec.Category, index);

            if (spec.LocationPoint == null)
                throw CreateSupport.Failure(index, McpDomainError.InvalidParameter, "缺少 locationPoint。");

            var level = CreateSupport.ResolveLevel(document, context, spec.LevelId, index);
            var symbol = CreateSupport.ResolveType<FamilySymbol>(
                document, context, spec.TypeId, category, index);

            CreateSupport.EnsureActive(symbol);

            var aboveLevelMm = (spec.LocationPoint.Z ?? 0) + (spec.BaseOffset ?? 0);
            var needsHost = NeedsHost(category);

            var instance = needsHost
                ? CreateHosted(document, context, spec, index, symbol, level, aboveLevelMm)
                : CreateFree(document, context, spec, index, symbol, level, aboveLevelMm);

            ApplyFlips(document, instance, spec, context);

            return new CreatedElement
            {
                Index = index,
                Id = instance.Id.ToProtocolString(),
                Category = category.ToString(),
                Type = CreateSupport.SafeName(symbol),
                Level = CreateSupport.SafeName(level)
            };
        }

        /// <summary>
        /// 门窗必须有宿主墙。不把它做成"没有宿主就放个自由实例"——
        /// 那样建出来的门窗看着在，实际没有在墙上开洞，是比失败更坏的结果。
        /// </summary>
        private static bool NeedsHost(BuiltInCategory category)
        {
            return category == BuiltInCategory.OST_Doors || category == BuiltInCategory.OST_Windows;
        }

        private static FamilyInstance CreateHosted(
            Document document, ToolExecutionContext<UIApplication> context,
            PointBasedElementSpec spec, int index, FamilySymbol symbol, Level level, double aboveLevelMm)
        {
            if (spec.Rotation.HasValue)
                CreateSupport.Once(context,
                    "门窗的朝向由宿主墙决定，rotation 已被忽略。要改朝向用 facingFlipped / handFlipped。");

            var wall = ResolveHostWall(document, context, spec, index);

            // 插入点的 Z 用标高高度：门窗在墙上的竖向位置由窗台高度参数控制，
            // 插入点给错 Z 会让 Revit 找不到墙面
            var point = new XYZ(
                Units.ToFeet(spec.LocationPoint.X),
                Units.ToFeet(spec.LocationPoint.Y),
                level.Elevation);

            FamilyInstance instance;
            try
            {
                instance = document.Create.NewFamilyInstance(
                    point, symbol, wall, level, StructuralType.NonStructural);
            }
            catch (Exception ex)
            {
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "Revit 拒绝在墙 " + wall.Id.ToProtocolString() + " 上创建该构件：" + ex.Message);
            }

            if (instance == null)
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "Revit 未能创建构件，但也没有报错。插入点可能落在墙的范围之外。");

            if (aboveLevelMm != 0) ApplySillHeight(instance, aboveLevelMm, context);

            return instance;
        }

        private static FamilyInstance CreateFree(
            Document document, ToolExecutionContext<UIApplication> context,
            PointBasedElementSpec spec, int index, FamilySymbol symbol, Level level, double aboveLevelMm)
        {
            var point = new XYZ(
                Units.ToFeet(spec.LocationPoint.X),
                Units.ToFeet(spec.LocationPoint.Y),
                level.Elevation + Units.ToFeet(aboveLevelMm));

            FamilyInstance instance;
            try
            {
                instance = document.Create.NewFamilyInstance(
                    point, symbol, level, StructuralType.NonStructural);
            }
            catch (Exception ex)
            {
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "Revit 拒绝创建该构件：" + ex.Message);
            }

            if (instance == null)
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "Revit 未能创建构件，但也没有报错。类型「" + CreateSupport.SafeName(symbol) +
                    "」可能需要宿主（如面、墙），不能自由放置。");

            if (spec.Rotation.HasValue && Math.Abs(spec.Rotation.Value) > 1e-9)
                Rotate(document, instance, point, spec.Rotation.Value, index);

            return instance;
        }

        private static Wall ResolveHostWall(
            Document document, ToolExecutionContext<UIApplication> context,
            PointBasedElementSpec spec, int index)
        {
            if (!string.IsNullOrWhiteSpace(spec.HostWallId))
            {
                var element = CreateSupport.RequireElement(document, spec.HostWallId, index);
                var wall = element as Wall;

                if (wall == null)
                    throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                        "hostWallId " + spec.HostWallId + " 不是墙，而是「" +
                        (element.Category?.Name ?? element.GetType().Name) + "」。");

                return wall;
            }

            double distanceMm;
            var nearest = FindNearestWall(document, spec.LocationPoint, out distanceMm);

            if (nearest == null)
                throw CreateSupport.Failure(index, McpDomainError.ElementNotFound,
                    "插入点 " + spec.LocationPoint + " 附近 " + MaxHostSearchMm +
                    " 毫米内没有墙，门窗无处依附。请给出 hostWallId，或先建墙。");

            CreateSupport.Once(context,
                "未指定 hostWallId，按就近原则选用了墙（距插入点最近的一面）。" +
                "墙密集时这个猜测可能选错，建议显式给出 hostWallId。");

            return nearest;
        }

        /// <summary>
        /// 找离插入点最近的墙。只比较到定位线的平面距离——
        /// 竖向由标高和窗台高决定，把 Z 算进来只会让同一位置不同层的墙互相干扰。
        /// </summary>
        private static Wall FindNearestWall(Document document, Point3D point, out double distanceMm)
        {
            var target = new XYZ(Units.ToFeet(point.X), Units.ToFeet(point.Y), 0);
            var best = (Wall)null;
            var bestFeet = double.MaxValue;

            foreach (var wall in new FilteredElementCollector(document)
                         .OfCategory(BuiltInCategory.OST_Walls)
                         .WhereElementIsNotElementType()
                         .OfType<Wall>())
            {
                var location = wall.Location as LocationCurve;
                if (location?.Curve == null) continue;

                double feet;
                try
                {
                    var curve = location.Curve;
                    var flattened = Line.CreateBound(
                        new XYZ(curve.GetEndPoint(0).X, curve.GetEndPoint(0).Y, 0),
                        new XYZ(curve.GetEndPoint(1).X, curve.GetEndPoint(1).Y, 0));

                    feet = flattened.Distance(target);
                }
                catch
                {
                    continue;   // 弧墙端点重合之类的退化情况，跳过它而不是让整批失败
                }

                if (feet >= bestFeet) continue;

                bestFeet = feet;
                best = wall;
            }

            distanceMm = best == null ? double.MaxValue : Units.FromFeet(bestFeet);
            return distanceMm <= MaxHostSearchMm ? best : null;
        }

        /// <summary>
        /// 门窗的竖向位置是「窗台高度」，不是构件本身的 Z 坐标。
        /// 设不上只警告不失败：构件已经在墙上的正确平面位置，为一个高度回滚整批不划算。
        /// </summary>
        private static void ApplySillHeight(
            FamilyInstance instance, double aboveLevelMm, ToolExecutionContext<UIApplication> context)
        {
            var feet = Units.ToFeet(aboveLevelMm);

            if (TrySet(instance, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM, feet)) return;

            CreateSupport.Once(context,
                "构件 " + instance.Id.ToProtocolString() + " 的窗台高度没能设成 " + aboveLevelMm +
                " 毫米（该族可能没有这个参数），高度用的是族类型的默认值。");
        }

        private static void ApplyFlips(
            Document document, FamilyInstance instance,
            PointBasedElementSpec spec, ToolExecutionContext<UIApplication> context)
        {
            if (spec.FacingFlipped == true) Flip(document, instance, true, context);
            if (spec.HandFlipped == true) Flip(document, instance, false, context);
        }

        private static void Flip(
            Document document, FamilyInstance instance, bool facing, ToolExecutionContext<UIApplication> context)
        {
            var what = facing ? "朝向" : "左右";

            try
            {
                if (facing) instance.flipFacing();
                else instance.flipHand();

                // 翻转会改变几何，不刷新的话紧随其后的操作看到的还是旧位置
                document.Regenerate();
            }
            catch (Exception ex)
            {
                CreateSupport.Once(context,
                    "构件 " + instance.Id.ToProtocolString() + " 翻转" + what + "失败（" + ex.Message +
                    "），其余部分已正常创建。");
            }
        }

        private static void Rotate(
            Document document, FamilyInstance instance, XYZ point, double degrees, int index)
        {
            try
            {
                var axis = Line.CreateBound(point, point + XYZ.BasisZ);
                ElementTransformUtils.RotateElement(
                    document, instance.Id, axis, degrees * Math.PI / 180.0);
            }
            catch (Exception ex)
            {
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "构件已创建但旋转 " + degrees.ToString("0.###", CultureInfo.InvariantCulture) +
                    " 度失败：" + ex.Message);
            }
        }

        private static bool TrySet(Element element, BuiltInParameter id, double value)
        {
            try
            {
                var parameter = element.get_Parameter(id);
                return parameter != null && !parameter.IsReadOnly && parameter.Set(value);
            }
            catch
            {
                return false;
            }
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
