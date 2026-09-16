using System;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tools
{
    /// <summary>
    /// 对外单位与 Revit 内部单位之间的换算：长度用毫米，面积用平方米，体积用立方米。
    ///
    /// 不走 <c>UnitUtils</c> 是刻意的：它的 API 在 2021 前后不兼容，
    /// 而 1 ft = 304.8 mm 是精确定义值，硬编码它省掉一整类版本问题。
    ///
    /// 所有对外协议里的长度一律是毫米，与项目的显示单位无关——
    /// 显示单位是给人看的，接口需要的是一个不随项目设置漂移的固定语义。
    /// 项目实际用什么单位由 revit_get_project_units 单独回答。
    /// </summary>
    internal static class Units
    {
        public const double PerFoot = 304.8;

        /// <summary>
        /// Revit 拒绝短于约 1/32 英尺（≈0.79 毫米）的曲线。
        /// 取 1 毫米作为对外的下限，提前拦下比让 Revit 抛一句晦涩的报错好。
        /// </summary>
        public const double MinLength = 1.0;

        public static double ToFeet(double millimeters)
        {
            return millimeters / PerFoot;
        }

        public static double FromFeet(double feet)
        {
            return feet * PerFoot;
        }

        /// <summary>毫米坐标 → 内部单位的点。</summary>
        public static XYZ Point(double x, double y, double z)
        {
            return new XYZ(ToFeet(x), ToFeet(y), ToFeet(z));
        }

        /// <summary>内部单位的点 → 毫米，保留 3 位小数（内部单位是英尺，换算后必然有浮点尾巴）。</summary>
        public static double Round(double millimeters)
        {
            return Math.Round(millimeters, 3);
        }

        /// <summary>
        /// 平方英尺 → 平方米，保留 3 位小数。
        ///
        /// **面积刻意不用平方毫米。** 长度一律毫米是为了有个不随项目设置漂移的固定语义，
        /// 但同一套换算放到面积上就失效了：一个 20 平米的房间是 20000000 平方毫米，
        /// 这种数字人读不出、模型也容易数错一个零。
        /// 面积用平方米、体积用立方米，字段名一律带单位后缀（areaSqm、volumeCbm），
        /// 让单位跟着数字走，而不是靠读文档记住。
        /// </summary>
        public static double SquareMeters(double squareFeet)
        {
            return Math.Round(squareFeet * SquareMetersPerSquareFoot, 3);
        }

        /// <summary>立方英尺 → 立方米，保留 3 位小数。</summary>
        public static double CubicMeters(double cubicFeet)
        {
            return Math.Round(cubicFeet * CubicMetersPerCubicFoot, 3);
        }

        /// <summary>1 ft = 0.3048 m 是精确定义值，平方和立方也就都是精确的。</summary>
        private const double MetersPerFoot = 0.3048;
        private const double SquareMetersPerSquareFoot = MetersPerFoot * MetersPerFoot;
        private const double CubicMetersPerCubicFoot = MetersPerFoot * MetersPerFoot * MetersPerFoot;
    }
}
