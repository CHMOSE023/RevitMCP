using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    /// <summary>
    /// Revit 工具基类。绑定上下文类型，并收拢几个每个工具都要做的琐事。
    /// 执行时已在 Revit 主线程、且具备有效的 API context。
    /// </summary>
    public abstract class RevitTool<TInput, TOutput> : McpTool<UIApplication, TInput, TOutput>
        where TInput : class, new()
    {
        /// <summary>取当前文档；没有打开文档时抛出带 NO_ACTIVE_DOC 的失败。</summary>
        protected static Document RequireDocument(ToolExecutionContext<UIApplication> context)
        {
            var uiDocument = context.Host?.ActiveUIDocument;
            var document = uiDocument?.Document;

            if (document == null)
                throw new ToolFailureException(McpDomainError.NoActiveDocument,
                    "Revit 中没有打开的文档。请先打开一个模型再重试。");

            return document;
        }

        protected static UIDocument RequireUiDocument(ToolExecutionContext<UIApplication> context)
        {
            var uiDocument = context.Host?.ActiveUIDocument;
            if (uiDocument?.Document == null)
                throw new ToolFailureException(McpDomainError.NoActiveDocument,
                    "Revit 中没有打开的文档。请先打开一个模型再重试。");
            return uiDocument;
        }

        /// <summary>
        /// 把字符串解析成 BuiltInCategory。
        /// 解析失败时给出相近的候选——模型靠这个纠正拼写，比单纯报错有用得多。
        /// </summary>
        protected static BuiltInCategory ParseCategory(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ToolFailureException(McpDomainError.InvalidParameter, "category 不能为空。");

            var text = value.Trim();
            if (Enum.TryParse(text, ignoreCase: true, out BuiltInCategory parsed) &&
                Enum.IsDefined(typeof(BuiltInCategory), parsed))
                return parsed;

            // 允许省略 OST_ 前缀
            if (!text.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) &&
                Enum.TryParse("OST_" + text, ignoreCase: true, out BuiltInCategory prefixed) &&
                Enum.IsDefined(typeof(BuiltInCategory), prefixed))
                return prefixed;

            var suggestions = SuggestCategories(text);
            var hint = suggestions.Length > 0
                ? "。是否想找：" + string.Join("、", suggestions)
                : "。可先调用 revit_list_categories 查看当前模型中实际存在的类别";

            throw new ToolFailureException(McpDomainError.InvalidParameter,
                "无法识别的类别 \"" + value + "\"" + hint);
        }

        private static string[] SuggestCategories(string text)
        {
            var needle = text.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) ? text.Substring(4) : text;

            return Enum.GetNames(typeof(BuiltInCategory))
                .Where(name => name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(name => name.Length)
                .Take(8)
                .ToArray();
        }

        /// <summary>
        /// 按 ID 取构件。取不到就抛 ELEMENT_NOT_FOUND——
        /// 写工具不该"跳过找不到的那个然后照样改剩下的"：
        /// 模型以为改了 5 个，实际只改了 4 个，这种偏差会一直传下去。
        /// </summary>
        protected static Element RequireElement(Document document, string rawId)
        {
            if (!ElementIdCompat.TryParse(rawId, out var elementId))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "构件 ID \"" + rawId + "\" 格式非法，应为十进制整数的字符串形式。");

            var element = document.GetElement(elementId);
            if (element == null)
                throw new ToolFailureException(McpDomainError.ElementNotFound,
                    "模型中不存在 ID 为 " + rawId + " 的构件。请先用 revit_query_elements 确认 ID。");

            return element;
        }

        /// <summary>
        /// 规模阈值。影响面超过配置上限时拒绝，要求模型显式带 confirm: true 再来一次。
        ///
        /// 这道闸的意义不在于阻止"想改 600 个"，而在于阻止"以为在改 6 个、
        /// 实际匹配到 600 个"——后者才是真正会毁掉模型的那种错误。
        /// </summary>
        protected static void GuardScale(
            int affected, bool? confirm, ToolExecutionContext<UIApplication> context, string action)
        {
            var limit = context.MaxElementsPerWrite;
            if (limit <= 0 || affected <= limit || confirm == true) return;

            throw new ToolFailureException(McpDomainError.ConfirmationRequired,
                "本次操作将" + action + " " + affected + " 个构件，超过单次上限 " + limit + " 个。" +
                "确认这正是你想要的范围后，带上 confirm: true 重新调用；" +
                "否则请收紧筛选条件（例如缩小类别范围或加上 nameContains）。");
        }

        /// <summary>
        /// 读取一个参数的值。
        /// 同时给出原始值与 AsValueString()——后者带单位、已按用户的显示设置格式化。
        /// 刻意不做单位换算：UnitUtils 的 API 在 2021 前后不兼容，绕开它省掉一整类版本问题。
        /// </summary>
        protected static ParameterValue ReadParameter(Parameter parameter)
        {
            var result = new ParameterValue
            {
                Name = parameter.Definition?.Name ?? "(未命名)",
                StorageType = parameter.StorageType.ToString(),
                IsReadOnly = parameter.IsReadOnly
            };

            if (!parameter.HasValue) return result;

            switch (parameter.StorageType)
            {
                case StorageType.String:
                    result.Value = parameter.AsString();
                    break;
                case StorageType.Integer:
                    result.Value = parameter.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case StorageType.Double:
                    result.Value = parameter.AsDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case StorageType.ElementId:
                    result.Value = parameter.AsElementId().ToProtocolString();
                    break;
            }

            try { result.DisplayValue = parameter.AsValueString(); }
            catch { /* 某些参数类型不支持，不值得让整个读取失败 */ }

            return result;
        }

        protected static ElementSummary Summarize(Element element)
        {
            return new ElementSummary
            {
                Id = element.Id.ToProtocolString(),
                Name = SafeName(element),
                Category = element.Category?.Name,
                TypeId = element.GetTypeId()?.ToProtocolString(),
                LevelId = element.LevelId != null && element.LevelId != ElementId.InvalidElementId
                    ? element.LevelId.ToProtocolString()
                    : null
            };
        }

        private static string SafeName(Element element)
        {
            // 某些元素访问 Name 会抛异常（族文档里的部分内部元素）
            try { return element.Name; }
            catch { return null; }
        }
    }

    public sealed class ElementSummary
    {
        [McpParam("构件 ID（字符串形式）")]
        public string Id { get; set; }

        [McpParam("构件名称")]
        public string Name { get; set; }

        [McpParam("所属类别")]
        public string Category { get; set; }

        [McpParam("类型（族类型）ID")]
        public string TypeId { get; set; }

        [McpParam("所属标高 ID，没有则为 null")]
        public string LevelId { get; set; }
    }

    public sealed class ParameterValue
    {
        [McpParam("参数名")]
        public string Name { get; set; }

        [McpParam("存储类型：String / Integer / Double / ElementId")]
        public string StorageType { get; set; }

        [McpParam("原始值。Double 为 Revit 内部单位（长度为英尺）")]
        public string Value { get; set; }

        [McpParam("按用户显示设置格式化后的值，通常带单位")]
        public string DisplayValue { get; set; }

        [McpParam("是否只读")]
        public bool IsReadOnly { get; set; }
    }
}
