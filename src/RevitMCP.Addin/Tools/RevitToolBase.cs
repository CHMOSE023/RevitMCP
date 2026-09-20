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
        /// 取要操作的文档：给了 documentId 就用那一个，否则用当前活动文档。
        ///
        /// 一个 Revit 实例可以同时开着十几个项目，批量审计就是冲着它们去的。
        /// Revit 允许对任何打开的文档做只读查询，**不需要先把它切成活动文档**——
        /// 切换活动文档会打断用户正在看的东西，而查询不该有这种副作用。
        ///
        /// 只读工具才给这个参数。写操作一律只作用于活动文档：
        /// 让模型去改一个用户根本没在看的文档，风险和收益完全不成比例。
        /// </summary>
        protected static Document ResolveDocument(
            ToolExecutionContext<UIApplication> context, string documentId)
        {
            if (string.IsNullOrWhiteSpace(documentId)) return RequireDocument(context);

            var wanted = documentId.Trim();

            foreach (var document in DocumentRef.Opened(context.Host, includeLinked: true))
            {
                if (string.Equals(DocumentRef.KeyOf(document), wanted, StringComparison.OrdinalIgnoreCase))
                    return document;
            }

            var opened = new List<string>();
            foreach (var document in DocumentRef.Opened(context.Host))
            {
                var key = DocumentRef.KeyOf(document);
                if (key != null) opened.Add(key);
            }

            throw new ToolFailureException(McpDomainError.ElementNotFound,
                "没有打开 ID 为「" + documentId + "」的文档。" +
                (opened.Count > 0
                    ? "当前打开的是：" + string.Join("、", opened.ToArray())
                    : "当前没有打开任何文档") +
                "。用 revit_list_documents 查看。");
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
            string problem;
            var element = ElementRef.Resolve(document, rawId, out problem);

            if (element != null) return element;

            // 格式不对是参数问题，构件不在是查找问题——两者的补救动作不同，
            // 错误码必须分开：前者要改写法，后者要重新查一次
            var code = problem != null && problem.Contains("格式")
                ? McpDomainError.InvalidParameter
                : McpDomainError.ElementNotFound;

            throw new ToolFailureException(code, problem);
        }

        /// <summary>
        /// 规模阈值。影响面超过配置上限时拒绝，要求模型显式带 confirm: true 再来一次。
        ///
        /// 这道闸的意义不在于阻止"想改 600 个"，而在于阻止"以为在改 6 个、
        /// 实际匹配到 600 个"——后者才是真正会毁掉模型的那种错误。
        /// </summary>
        protected static void GuardScale(
            int affected, bool? confirm, ToolExecutionContext<UIApplication> context, string action,
            string remedy = null)
        {
            var limit = context.MaxElementsPerWrite;
            if (limit <= 0 || affected <= limit || confirm == true) return;

            throw new ToolFailureException(McpDomainError.ConfirmationRequired,
                "本次操作将" + action + " " + affected + " 个构件，超过单次上限 " + limit + " 个。" +
                "确认这正是你想要的范围后，带上 confirm: true 重新调用；" +
                (remedy ?? "否则请收紧筛选条件（例如缩小类别范围或加上 nameContains）。"));
        }

        /// <summary>
        /// 导出文件的根目录。工具只接受文件名，落点由服务决定——理由见 <see cref="RevitMCP.Tooling.ExportPaths"/>。
        /// </summary>
        protected static string ExportRoot()
        {
            var root = App.Current?.Config?.ResolvedExportDirectory;

            if (string.IsNullOrWhiteSpace(root))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "插件未正确初始化，导出目录不可用。请查看 RevitMCP 日志。");

            return root;
        }

        /// <summary>
        /// 批量工具的开场白：入参非空 + 过一遍规模闸。
        /// 三个建模工具的这两步必须完全一致，抄三遍迟早抄岔。
        /// </summary>
        protected static void RequireBatch<T>(
            List<T> elements, bool? confirm, ToolExecutionContext<UIApplication> context, string action)
        {
            if (elements == null || elements.Count == 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "elements 不能为空，至少要给一项。");

            // 批量创建没有"筛选条件"可收紧——那句默认建议是给按条件匹配的工具写的，
            // 照搬过来就是让模型去做一件做不到的事。能做的只有分批
            GuardScale(elements.Count, confirm, context, action,
                "否则请分批调用，每批不超过 " + context.MaxElementsPerWrite + " 个。");
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

            // 显示值必须和写工具回执里的 oldValue/newValue 来自同一个函数。
            //
            // 这里曾经直接调 AsValueString()，而**文本参数的 AsValueString() 返回 null**——
            // 于是写工具回执说 newValue="探针值"，读工具却把 displayValue 交成 null。
            // 调用方读参数时最自然的选择就是 displayValue，看到 null 会判定"这个参数是空的"，
            // 而原始值明明就在旁边的 value 里。两套实现长歪的代价就是这种自相矛盾。
            result.DisplayValue = ParameterWriter.DisplayOf(parameter);

            return result;
        }

        /// <summary>
        /// 族实例的结构类型。
        ///
        /// 单独读出来，是因为它**不是参数**——<c>FamilyInstance.StructuralType</c>
        /// 在「属性」面板上没有对应的行，把参数列表整个翻一遍也找不到它。
        /// 而一根被当成非结构建出来的柱子，几何、类型、参数全都正常，
        /// 只有它缺了结构行为与分析模型：结构专业的明细表、荷载与分析会整个漏掉它。
        /// 不把这个值交出来，这种错误没有任何办法从模型外部发现。
        /// </summary>
        protected static string StructuralTypeOf(Element element)
        {
            var instance = element as FamilyInstance;
            if (instance == null) return null;

            try { return instance.StructuralType.ToString(); }
            catch { return null; }
        }

        protected static ElementSummary Summarize(Element element)
        {
            return new ElementSummary
            {
                Id = element.Id.ToProtocolString(),
                UniqueId = ElementRef.UniqueIdOf(element),
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
        [McpParam("构件 ID（字符串形式）。**只在这一个文档的这一次会话里有效**——" +
                  "要跨会话留存（写进审计报告、交付清单）请用 uniqueId")]
        public string Id { get; set; }

        [McpParam("构件的 UniqueId，Revit 维护的 GUID。跨会话、跨 Revit 重启都稳定，" +
                  "导出 IFC 后也能对应回来。所有接受构件 ID 的工具都同样接受它。" +
                  "**隔天还要用的 ID 一律存这个**——ElementId 那串数字第二天会指向别的构件，" +
                  "而且看起来完全正常")]
        public string UniqueId { get; set; }

        [McpParam("构件名称")]
        public string Name { get; set; }

        [McpParam("所属类别")]
        public string Category { get; set; }

        [McpParam("类型（族类型）ID")]
        public string TypeId { get; set; }

        [McpParam("所属标高 ID，没有则为 null")]
        public string LevelId { get; set; }

        [McpParam("到查询点的最近距离，毫米。仅 near 查询时有值")]
        public double? DistanceMm { get; set; }
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
