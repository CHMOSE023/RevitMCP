using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    /// <summary>
    /// 墙的定位线：调用方给的那条线，说的是墙的哪个面。
    ///
    /// **这是为了消灭手算偏移。** 在此之前只有一种语义——给的线就是墙的中心线。
    /// 于是"外墙外皮要正好落在 10000 × 8000 的轮廓上"这种最常见的要求，
    /// 得由调用方自己把每一段轮廓线朝里挪半个墙厚：四条边四个方向，
    /// 算错了不会报错，只会让模型静默差 100 毫米。
    ///
    /// Revit 自己的做法是给墙一个「定位线」参数，画的时候按那条线对齐。
    /// API 里的 <c>Wall.Create</c> 没有这个入口——它**永远**把传入的曲线当中心线。
    /// 所以这里建完之后补两步：把墙整体挪到位，再把参数设对。
    ///
    /// **挪的方向不靠推算，直接问 Revit。**
    /// 墙的哪一侧算"外"由定位线的方向决定，规则可以写成
    /// "外法线 = 方向 × Z"这样一个叉乘——但这个式子的符号取决于 Revit 内部约定。
    /// 写这段代码时推出的符号是错的（推成了"逆时针给一圈线、外表面朝外"），
    /// 而猜错的代价正是这个功能本来要消灭的那类错误：
    /// 每一面外墙静默偏半个墙厚，方向还正好反。
    ///
    /// 所以这里建完先 <c>Regenerate</c>，再读 <see cref="Wall.Orientation"/>：
    /// 那是 Revit 自己给出的外表面法线，无论内部约定是哪一种都对。
    /// 多一次重生成的代价，换掉一个没法在写代码时验证的假设，值——
    /// **事后实测证明当时的推导确实是反的，而这段代码因为没依赖它，一次就对了。**
    ///
    /// 实测结论（2026-09-17，Revit 2019，200 厚墙围 10000 × 8000）：
    /// 顺时针给一圈定位线 + FinishFaceExterior，包围盒正好是 10000 × 8000；
    /// 逆时针给则是 10400 × 8400。所以对外的说法是**外轮廓顺时针给**。
    /// </summary>
    internal static class WallReference
    {
        /// <summary>
        /// 定位线的内部编号，与 Revit 的 <c>WallLocationLine</c> 枚举一致。
        /// 不直接引用那个枚举是因为要按名字解析，且要给出可读的候选列表。
        /// </summary>
        private static readonly Dictionary<string, int> Options =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "WallCenterline", 0 },
                { "CoreCenterline", 1 },
                { "FinishFaceExterior", 2 },
                { "FinishFaceInterior", 3 },
                { "CoreExterior", 4 },
                { "CoreInterior", 5 }
            };

        public const string ParamDescription =
            "定位线：给的那条线说的是墙的哪个面。默认 WallCenterline（墙中心线）。" +
            "可选 FinishFaceExterior（外表面）、FinishFaceInterior（内表面）、" +
            "CoreCenterline、CoreExterior、CoreInterior（核心层的中心与两面）。" +
            "**外皮尺寸直接照图给就用 FinishFaceExterior**，不必自己把轮廓往里挪半个墙厚。" +
            "墙的内外由定位线的方向决定，和 Revit 里画墙时一样：" +
            "**外轮廓要顺时针给**（平面上 X 向右、Y 向上时），外表面才朝外；" +
            "逆时针给会让整圈墙朝外长半个墙厚。" +
            "拿不准就建完量一下：用 revit_get_element_geometry 看一圈墙的包围盒，" +
            "外表面定位时它应当正好等于图纸尺寸。";

        public static int Parse(string raw, int index)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0;   // WallCenterline

            int value;
            if (Options.TryGetValue(raw.Trim(), out value)) return value;

            throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                "无法识别的 locationLineRef \"" + raw + "\"。可用：" +
                string.Join("、", Options.Keys.ToArray()) + "。");
        }

        public static bool IsCenterline(int reference)
        {
            return reference == 0;
        }

        /// <summary>
        /// 把墙挪到位，并把定位线参数设对。返回实际挪动的距离（毫米，非负）。
        ///
        /// 顺序是"先按中心线建、再整体挪"，而不是"先换算好中心线再建"：
        /// 只有墙已经存在，才能问它 <see cref="Wall.Orientation"/> ——
        /// 也就是 Revit 自己认定的外表面朝向。见类型注释。
        ///
        /// 偏移量为正表示基准面在中心线的外侧。墙建出来时中心线压在调用方给的线上，
        /// 要让**基准面**落到那条线上，就得把整面墙朝内挪同样的距离：挪 −法线 × 偏移量。
        /// </summary>
        public static double Place(
            Document document, Wall wall, WallType wallType, int reference,
            ToolExecutionContext<Autodesk.Revit.UI.UIApplication> context, int index)
        {
            if (IsCenterline(reference)) return 0;

            var offsetMm = OffsetFromCenterlineMm(wallType, reference, index);

            if (Math.Abs(offsetMm) > 1e-9)
            {
                // Orientation 要等几何生成出来才准。批量建墙时这是每面墙一次重生成，
                // 不便宜——但换掉的是一个错了不会报错的假设
                document.Regenerate();

                var exterior = ExteriorNormal(wall, index);
                var shift = exterior * -Units.ToFeet(offsetMm);

                try
                {
                    ElementTransformUtils.MoveElement(document, wall.Id, shift);
                }
                catch (Exception ex)
                {
                    throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                        "墙已建出，但按 locationLineRef 挪到位时失败：" + ex.Message);
                }
            }

            ApplyParameter(wall, reference, context);
            return Math.Abs(offsetMm);
        }

        /// <summary>
        /// 墙的外表面法线。压平到水平面——竖向分量对定位线没有意义，
        /// 留着只会让挪动带上一点竖向漂移。
        /// </summary>
        private static XYZ ExteriorNormal(Wall wall, int index)
        {
            XYZ orientation;
            try { orientation = wall.Orientation; }
            catch (Exception ex)
            {
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "读不出墙的朝向，无法按 locationLineRef 定位：" + ex.Message);
            }

            var flat = orientation == null ? null : new XYZ(orientation.X, orientation.Y, 0);

            if (flat == null || flat.GetLength() < 1e-9)
                throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                    "这面墙的朝向没有水平分量（定位线可能是竖直的），无法判断内外侧。" +
                    "locationLineRef 只对水平走向的墙有意义。");

            return flat.Normalize();
        }

        /// <summary>
        /// 基准面到中心线的有符号距离，毫米，朝外为正。
        ///
        /// 层构造的层序是**从外到内**，索引 0 是最外一层。于是：
        /// 外表面到核心层外表面的距离 = 第一层核心层之前所有层的厚度和；
        /// 外表面到核心层内表面的距离 = 到最后一层核心层为止所有层的厚度和。
        /// </summary>
        private static double OffsetFromCenterlineMm(WallType wallType, int reference, int index)
        {
            var totalMm = Units.FromFeet(wallType.Width);
            var halfMm = totalMm / 2.0;

            switch (reference)
            {
                case 2: return halfMm;      // FinishFaceExterior
                case 3: return -halfMm;     // FinishFaceInterior
            }

            var structure = Structure(wallType);

            if (structure == null)
                throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                    "墙类型「" + CreateSupport.SafeName(wallType) +
                    "」没有可读的层构造（幕墙、叠层墙就是这样），无法按核心层定位。" +
                    "改用 WallCenterline，或 FinishFaceExterior / FinishFaceInterior。");

            var layers = structure.GetLayers();
            var first = structure.GetFirstCoreLayerIndex();
            var last = structure.GetLastCoreLayerIndex();

            if (layers == null || first < 0 || last < first || last >= layers.Count)
                throw CreateSupport.Failure(index, McpDomainError.InvalidParameter,
                    "墙类型「" + CreateSupport.SafeName(wallType) +
                    "」的层构造里找不到核心层，无法按核心层定位。改用 WallCenterline 或面层定位。");

            var toCoreExteriorMm = 0.0;
            for (var i = 0; i < first; i++) toCoreExteriorMm += Units.FromFeet(layers[i].Width);

            var toCoreInteriorMm = toCoreExteriorMm;
            for (var i = first; i <= last; i++) toCoreInteriorMm += Units.FromFeet(layers[i].Width);

            switch (reference)
            {
                case 1: return halfMm - (toCoreExteriorMm + toCoreInteriorMm) / 2.0;   // CoreCenterline
                case 4: return halfMm - toCoreExteriorMm;                              // CoreExterior
                case 5: return halfMm - toCoreInteriorMm;                              // CoreInterior
            }

            return 0;
        }

        private static CompoundStructure Structure(WallType wallType)
        {
            try { return wallType.GetCompoundStructure(); }
            catch { return null; }
        }

        /// <summary>
        /// 把定位线参数设到墙上，让 Revit 界面里显示的定位线与调用方的本意一致。
        ///
        /// 设不上只警告：几何已经摆对了，丢的只是这面墙在属性面板里的说法，
        /// 以及用户以后拖动它时的基准。
        /// </summary>
        private static void ApplyParameter(
            Wall wall, int reference, ToolExecutionContext<Autodesk.Revit.UI.UIApplication> context)
        {
            try
            {
                var parameter = wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
                if (parameter != null && !parameter.IsReadOnly && parameter.Set(reference)) return;
            }
            catch { /* 落到下面的警告 */ }

            CreateSupport.Once(context,
                "墙 " + wall.Id.ToProtocolString() + " 的「定位线」参数没能设上。" +
                "墙的位置是对的（已按定位线挪过），只是属性面板里仍显示为墙中心线。");
        }
    }
}
