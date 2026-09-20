using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    public sealed class BoundingBoxInfo
    {
        [McpParam("最小角点，毫米")]
        public Point3D Min { get; set; }

        [McpParam("最大角点，毫米")]
        public Point3D Max { get; set; }

        [McpParam("中心点，毫米")]
        public Point3D Center { get; set; }

        [McpParam("X 方向尺寸，毫米")]
        public double SizeXMm { get; set; }

        [McpParam("Y 方向尺寸，毫米")]
        public double SizeYMm { get; set; }

        [McpParam("Z 方向尺寸，毫米")]
        public double SizeZMm { get; set; }
    }

    public sealed class ElementGeometry
    {
        [McpParam("构件 ID")]
        public string Id { get; set; }

        [McpParam("构件名称")]
        public string Name { get; set; }

        [McpParam("所属类别")]
        public string Category { get; set; }

        [McpParam("轴对齐包围盒。构件没有几何时为 null")]
        public BoundingBoxInfo BoundingBox { get; set; }

        [McpParam("定位线，毫米。墙、梁这类线定位构件才有")]
        public LocationLine LocationLine { get; set; }

        [McpParam("定位点，毫米。门窗、家具这类点定位构件才有")]
        public Point3D LocationPoint { get; set; }

        [McpParam("绕 Z 轴的旋转角，度。仅点定位构件有")]
        public double? RotationDeg { get; set; }

        [McpParam("朝向的单位向量。族实例才有——门窗靠它判断朝里还是朝外")]
        public Point3D FacingDirection { get; set; }

        [McpParam("左右方向的单位向量。族实例才有")]
        public Point3D HandDirection { get; set; }
    }

    public sealed class GetGeometryInput
    {
        [McpParam("要查询的构件 ID 列表。ElementId 与 uniqueId 两种写法都接受", Required = true)]
        public List<string> ElementIds { get; set; }

        [McpParam("是否返回包围盒，默认 true")]
        public bool? IncludeBoundingBox { get; set; }

        [McpParam("是否返回定位线/定位点与朝向，默认 true")]
        public bool? IncludeLocation { get; set; }
    }

    public sealed class GetGeometryOutput
    {
        [McpParam("查到的构件几何")]
        public List<ElementGeometry> Elements { get; set; } = new List<ElementGeometry>();

        [McpParam("未找到或 ID 非法的构件，及原因")]
        public List<string> NotFound { get; set; } = new List<string>();
    }

    [McpTool("revit_get_element_geometry",
        Title = "读取构件几何",
        Description = "返回构件的包围盒、定位线或定位点、朝向。长度一律毫米。" +
                      "用它回答「这两个构件是不是撞上了」「这面墙有多长、朝哪边」。" +
                      "**不返回网格面片**——那种数据量对话里根本放不下，" +
                      "要做精确碰撞请用包围盒先筛，再看具体定位。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class GetGeometryTool : RevitTool<GetGeometryInput, GetGeometryOutput>
    {
        private const int MaxElements = 100;

        public override GetGeometryOutput Execute(GetGeometryInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);

            if (input.ElementIds == null || input.ElementIds.Count == 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter, "elementIds 不能为空。");

            if (input.ElementIds.Count > MaxElements)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "一次最多查询 " + MaxElements + " 个构件的几何，本次传入 " + input.ElementIds.Count +
                    " 个。几何数据比参数大得多，请分批调用。");

            var wantBox = input.IncludeBoundingBox != false;
            var wantLocation = input.IncludeLocation != false;

            var output = new GetGeometryOutput();
            var total = input.ElementIds.Count;
            var done = 0;

            foreach (var raw in input.ElementIds)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                done++;
                ProgressTicker.Tick(context.Progress, done, total, "已读取");

                string problem;
                var element = ElementRef.Resolve(document, raw, out problem);

                if (element == null)
                {
                    output.NotFound.Add(raw + "（" + problem + "）");
                    continue;
                }

                output.Elements.Add(Describe(element, wantBox, wantLocation));
            }

            return output;
        }

        private static ElementGeometry Describe(Element element, bool wantBox, bool wantLocation)
        {
            var geometry = new ElementGeometry
            {
                Id = element.Id.ToProtocolString(),
                Name = SafeName(element),
                Category = element.Category?.Name
            };

            if (wantBox) geometry.BoundingBox = ReadBoundingBox(element);

            if (!wantLocation) return geometry;

            ReadLocation(element, geometry);
            ReadOrientation(element, geometry);

            return geometry;
        }

        /// <summary>
        /// 读轴对齐包围盒。
        ///
        /// <c>get_BoundingBox(null)</c> 给的是模型坐标系下的盒子，但它带一个 Transform，
        /// 个别构件上不是单位矩阵。这时直接把 Min/Max 变换过去是错的——
        /// 变换后的两个点不再是新坐标系里的极值。稳妥办法是把八个角点都变换一遍再取极值。
        /// </summary>
        internal static BoundingBoxXYZ RawBox(Element element)
        {
            try { return element.get_BoundingBox(null); }
            catch { return null; }
        }

        private static BoundingBoxInfo ReadBoundingBox(Element element)
        {
            var box = RawBox(element);
            if (box == null) return null;

            XYZ min, max;
            try
            {
                Extremes(box, out min, out max);
            }
            catch
            {
                return null;
            }

            return new BoundingBoxInfo
            {
                Min = ToPoint(min),
                Max = ToPoint(max),
                Center = ToPoint((min + max) / 2),
                SizeXMm = Units.Round(Units.FromFeet(max.X - min.X)),
                SizeYMm = Units.Round(Units.FromFeet(max.Y - min.Y)),
                SizeZMm = Units.Round(Units.FromFeet(max.Z - min.Z))
            };
        }

        /// <summary>把包围盒化到模型坐标系下的真实极值。</summary>
        internal static void Extremes(BoundingBoxXYZ box, out XYZ min, out XYZ max)
        {
            var transform = box.Transform;

            if (transform == null || transform.IsIdentity)
            {
                min = box.Min;
                max = box.Max;
                return;
            }

            var lo = box.Min;
            var hi = box.Max;
            var corners = new[]
            {
                new XYZ(lo.X, lo.Y, lo.Z), new XYZ(hi.X, lo.Y, lo.Z),
                new XYZ(lo.X, hi.Y, lo.Z), new XYZ(hi.X, hi.Y, lo.Z),
                new XYZ(lo.X, lo.Y, hi.Z), new XYZ(hi.X, lo.Y, hi.Z),
                new XYZ(lo.X, hi.Y, hi.Z), new XYZ(hi.X, hi.Y, hi.Z)
            };

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            foreach (var corner in corners)
            {
                var p = transform.OfPoint(corner);
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
            }

            min = new XYZ(minX, minY, minZ);
            max = new XYZ(maxX, maxY, maxZ);
        }

        private static void ReadLocation(Element element, ElementGeometry geometry)
        {
            Location location;
            try { location = element.Location; }
            catch { return; }

            var curve = location as LocationCurve;
            if (curve?.Curve != null)
            {
                try
                {
                    geometry.LocationLine = new LocationLine
                    {
                        P0 = ToPoint(curve.Curve.GetEndPoint(0)),
                        P1 = ToPoint(curve.Curve.GetEndPoint(1))
                    };
                }
                catch { /* 退化曲线取不到端点，几何的其余部分照样有用 */ }
                return;
            }

            var point = location as LocationPoint;
            if (point == null) return;

            try
            {
                geometry.LocationPoint = ToPoint(point.Point);
                geometry.RotationDeg = Units.Round(point.Rotation * 180.0 / Math.PI);
            }
            catch { }
        }

        private static void ReadOrientation(Element element, ElementGeometry geometry)
        {
            var instance = element as FamilyInstance;
            if (instance == null) return;

            try { geometry.FacingDirection = ToVector(instance.FacingOrientation); }
            catch { }

            try { geometry.HandDirection = ToVector(instance.HandOrientation); }
            catch { }
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

        /// <summary>方向向量是无量纲的，不换算单位，只做四舍五入。</summary>
        private static Point3D ToVector(XYZ vector)
        {
            if (vector == null) return null;

            return new Point3D
            {
                X = Math.Round(vector.X, 6),
                Y = Math.Round(vector.Y, 6),
                Z = Math.Round(vector.Z, 6)
            };
        }

        private static string SafeName(Element element)
        {
            try { return element.Name; }
            catch { return null; }
        }
    }
}
