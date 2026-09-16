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
