using System.Globalization;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Compat
{
    /// <summary>
    /// Revit 2024 起 ElementId 的底层类型由 Int32 变为 Int64，IntegerValue 被 Value 取代。
    /// 全代码库只允许在这里出现该差异，其他地方一律走这些扩展方法。
    ///
    /// 对外协议中 ElementId 一律序列化为字符串（"123456"）：
    /// 既规避 32/64 位差异，也避开 JavaScript 端 Number 只有 53 位安全整数的精度问题。
    /// </summary>
    public static class ElementIdCompat
    {
#if REVIT2024_OR_GREATER
        public static long GetValue(this ElementId id) => id.Value;

        public static ElementId ToElementId(long value) => new ElementId(value);
#else
        public static long GetValue(this ElementId id) => id.IntegerValue;

        public static ElementId ToElementId(long value) => new ElementId(checked((int)value));
#endif

        public static string ToProtocolString(this ElementId id) =>
            id == null ? null : id.GetValue().ToString(CultureInfo.InvariantCulture);

        public static bool TryParse(string text, out ElementId id)
        {
            id = null;
            if (string.IsNullOrEmpty(text)) return false;
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return false;

#if !REVIT2024_OR_GREATER
            if (value < int.MinValue || value > int.MaxValue) return false;
#endif
            id = ToElementId(value);
            return true;
        }
    }
}
