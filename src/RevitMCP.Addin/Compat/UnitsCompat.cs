using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Compat
{
    /// <summary>
    /// 读取项目的显示单位设置。单位 API 在 2021 前后整体换代——
    /// <c>UnitType</c> + <c>DisplayUnitType</c> 被 <c>ForgeTypeId</c> 取代——
    /// 差异只允许出现在这里。
    ///
    /// 只读不换算：工具内部的长度换算一律走 <see cref="RevitMCP.Addin.Tools.Units"/> 的
    /// 硬编码常量（1 ft = 304.8 mm），不碰 UnitUtils。这里要回答的是另一个问题——
    /// **用户在界面上看到的数字是什么单位**。英制项目上模型若假设"数字是毫米"，
    /// 读出来的一切都会错得无声无息。
    /// </summary>
    public static class UnitsCompat
    {
        public sealed class UnitReading
        {
            /// <summary>度量类型，如 Length / Area / Volume / Angle。</summary>
            public string Spec;

            /// <summary>跨版本稳定的单位标识，如 millimeters / feetFractionalInches。取不到时为 null。</summary>
            public string Unit;

            /// <summary>Revit 界面上显示的单位名，随 Revit 语言变化。</summary>
            public string Label;

            /// <summary>舍入精度，以该单位计。取不到时为 null。</summary>
            public double? Accuracy;
        }

        /// <summary>
        /// 读取项目里最常用的几个度量的显示单位。
        /// 不是全部——Revit 有上百个 spec，列全了只会淹没真正要看的那几行。
        /// </summary>
        public static List<UnitReading> ReadProjectUnits(Document document)
        {
            var units = document.GetUnits();
            var readings = new List<UnitReading>();

#if REVIT2021_OR_GREATER
            Add(readings, units, "Length", SpecTypeId.Length);
            Add(readings, units, "Area", SpecTypeId.Area);
            Add(readings, units, "Volume", SpecTypeId.Volume);
            Add(readings, units, "Angle", SpecTypeId.Angle);
            Add(readings, units, "MassDensity", SpecTypeId.MassDensity);
#else
            Add(readings, units, "Length", UnitType.UT_Length);
            Add(readings, units, "Area", UnitType.UT_Area);
            Add(readings, units, "Volume", UnitType.UT_Volume);
            Add(readings, units, "Angle", UnitType.UT_Angle);
            Add(readings, units, "MassDensity", UnitType.UT_MassDensity);
#endif
            return readings;
        }

        /// <summary>取长度的稳定单位标识，供工具判断"项目是不是公制毫米"。取不到时为 null。</summary>
        public static string LengthUnit(Document document)
        {
            foreach (var reading in ReadProjectUnits(document))
                if (reading.Spec == "Length") return reading.Unit;
            return null;
        }

#if REVIT2021_OR_GREATER
        private static void Add(List<UnitReading> readings, Units units, string spec, ForgeTypeId specId)
        {
            var reading = new UnitReading { Spec = spec };
            try
            {
                var options = units.GetFormatOptions(specId);

                // UseDefault 为真时 GetUnitTypeId() 会抛异常，只能从 spec 的默认单位取
                var unitId = options.UseDefault
                    ? UnitUtils.GetValidUnits(specId)[0]
                    : options.GetUnitTypeId();

                reading.Unit = Normalize(unitId.TypeId);
                reading.Label = TryLabel(unitId);
                if (!options.UseDefault) reading.Accuracy = options.Accuracy;
            }
            catch
            {
                // 单个度量读不出来不值得让整个工具失败，留下 Spec 让模型知道我们试过
            }
            readings.Add(reading);
        }

        private static string TryLabel(ForgeTypeId unitId)
        {
            try { return LabelUtils.GetLabelForUnit(unitId); }
            catch { return null; }
        }

        /// <summary>"autodesk.unit.unit:millimeters-1.0.1" → "millimeters"。</summary>
        private static string Normalize(string typeId)
        {
            if (string.IsNullOrEmpty(typeId)) return null;

            var colon = typeId.LastIndexOf(':');
            var text = colon >= 0 ? typeId.Substring(colon + 1) : typeId;

            var dash = text.LastIndexOf('-');
            return dash > 0 ? text.Substring(0, dash) : text;
        }
#else
        private static void Add(List<UnitReading> readings, Units units, string spec, UnitType unitType)
        {
            var reading = new UnitReading { Spec = spec };
            try
            {
                var options = units.GetFormatOptions(unitType);
                var displayUnit = options.DisplayUnits;

                reading.Unit = Normalize(displayUnit);
                reading.Label = TryLabel(displayUnit);
                if (!options.UseDefault) reading.Accuracy = options.Accuracy;
            }
            catch
            {
                // 同上
            }
            readings.Add(reading);
        }

        private static string TryLabel(DisplayUnitType displayUnit)
        {
            try { return LabelUtils.GetLabelFor(displayUnit); }
            catch { return null; }
        }

        /// <summary>
        /// DUT_MILLIMETERS → "millimeters"。
        /// 2021+ 的 Forge 标识不是 DUT 名的机械小驼峰化（DUT_DECIMAL_FEET 对应的是 "feet"），
        /// 所以常用单位显式列出来——同一个项目在 2020 和 2024 上必须读到同一个字符串，
        /// 否则模型的判断逻辑会随宿主版本失灵。
        /// </summary>
        private static string Normalize(DisplayUnitType displayUnit)
        {
            switch (displayUnit)
            {
                case DisplayUnitType.DUT_MILLIMETERS: return "millimeters";
                case DisplayUnitType.DUT_CENTIMETERS: return "centimeters";
                case DisplayUnitType.DUT_DECIMETERS: return "decimeters";
                case DisplayUnitType.DUT_METERS: return "meters";
                case DisplayUnitType.DUT_METERS_CENTIMETERS: return "metersCentimeters";
                case DisplayUnitType.DUT_DECIMAL_FEET: return "feet";
                case DisplayUnitType.DUT_FEET_FRACTIONAL_INCHES: return "feetFractionalInches";
                case DisplayUnitType.DUT_DECIMAL_INCHES: return "inches";
                case DisplayUnitType.DUT_FRACTIONAL_INCHES: return "fractionalInches";
                case DisplayUnitType.DUT_SQUARE_MILLIMETERS: return "squareMillimeters";
                case DisplayUnitType.DUT_SQUARE_METERS: return "squareMeters";
                case DisplayUnitType.DUT_SQUARE_FEET: return "squareFeet";
                case DisplayUnitType.DUT_CUBIC_MILLIMETERS: return "cubicMillimeters";
                case DisplayUnitType.DUT_CUBIC_METERS: return "cubicMeters";
                case DisplayUnitType.DUT_CUBIC_FEET: return "cubicFeet";
                case DisplayUnitType.DUT_DECIMAL_DEGREES: return "degrees";
                case DisplayUnitType.DUT_DEGREES_AND_MINUTES: return "degreesMinutes";
                case DisplayUnitType.DUT_RADIANS: return "radians";
                default: return ToCamel(displayUnit.ToString());
            }
        }

        /// <summary>兜底：DUT_KILOGRAMS_PER_CUBIC_METER → "kilogramsPerCubicMeter"。</summary>
        private static string ToCamel(string dutName)
        {
            if (string.IsNullOrEmpty(dutName)) return null;

            var body = dutName.StartsWith("DUT_", StringComparison.Ordinal) ? dutName.Substring(4) : dutName;
            var parts = body.Split('_');
            var text = string.Empty;

            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                var lower = parts[i].ToLower(CultureInfo.InvariantCulture);
                text += i == 0 ? lower : char.ToUpper(lower[0], CultureInfo.InvariantCulture) + lower.Substring(1);
            }

            return text;
        }
#endif
    }
}
