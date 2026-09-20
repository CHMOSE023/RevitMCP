using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    public sealed class LoadFamilyInput
    {
        [McpParam("族文件的**完整路径**（.rfa），如 \"C:\\族库\\单扇门.rfa\"。" +
                  "必须含盘符——相对路径的基准目录取决于 Revit 进程的当前目录，那个值不由任何人控制", Required = true)]
        public string Path { get; set; }

        [McpParam("项目里已经有同名族时是否用文件里的版本覆盖它，默认 false（同名即失败）。" +
                  "**覆盖会改变已有实例的外形与参数**：项目里那些门窗会立刻换成新族的样子，" +
                  "所以必须显式要求。不覆盖时工具会把已有的那个族原样返回给你")]
        public bool? Overwrite { get; set; }

        [McpParam("只载入这几个类型（族类型名，如 \"0915 x 2134mm\"）。" +
                  "省略则载入族文件里的全部类型。" +
                  "大族（几十上百个类型）只用其中一两个时，挑着载能让项目干净很多")]
        public List<string> TypeNames { get; set; }
    }

    public sealed class LoadedFamilyType
    {
        [McpParam("族类型 ID。建模工具的 typeId 参数直接用它")]
        public string Id { get; set; }

        [McpParam("类型名")]
        public string Name { get; set; }
    }

    public sealed class LoadFamilyOutput : IReportsAffectedElements
    {
        [McpParam("族 ID")]
        public string Id { get; set; }

        [McpParam("族名（以族文件里的名字为准，不一定等于文件名）")]
        public string Name { get; set; }

        [McpParam("族所属类别，如「门」")]
        public string Category { get; set; }

        [McpParam("本次是不是真的载入了。为 false 表示项目里已经有这个族、" +
                  "而你没有要求覆盖——返回的是已有的那一个")]
        public bool Loaded { get; set; }

        [McpParam("本次是否覆盖了项目里已有的同名族")]
        public bool Replaced { get; set; }

        [McpParam("载入后这个族有哪些类型。**建模前从这里取 typeId**，不必再查一次 revit_list_types")]
        public List<LoadedFamilyType> Types { get; set; } = new List<LoadedFamilyType>();

        int IReportsAffectedElements.AffectedElements => 1;
    }

    /// <summary>
    /// 载入族。
    ///
    /// 补这个工具的理由，是它堵着的那个洞没有任何绕行办法：
    /// 项目样板里没有的族（房间标记、某个规格的门窗、原生楼梯的栏杆…），
    /// 纯 MCP 客户端此前**无论如何都拿不到**——只能停下来请用户去 Revit 界面里点「载入族」。
    /// 而建模任务十有八九会撞上这件事。
    ///
    /// 路径策略与 <c>revit_save_document_as</c> 一致：收完整路径。
    /// 理由也一样——族文件在哪儿只可能是用户的明确决定，沙箱到导出目录等于这个功能没人能用。
    ///
    /// **覆盖默认不允许**：同名族覆盖会立刻改变项目里所有已放置实例的外形与参数，
    /// 那是一次波及面很大又不容易察觉的改动，必须显式要求。
    /// </summary>
    [McpTool("revit_load_family",
        Title = "载入族",
        Description = "把一个 .rfa 族文件载入当前项目，返回族 ID 与它的全部类型 ID。" +
                      "**项目样板里没有的族只能靠它**——建模工具只能用已经载入项目的族，" +
                      "此前缺族时只能请用户去 Revit 界面里手工载入。收的是完整路径。" +
                      "同名族已存在时默认失败并把已有的那个返回给你；要用文件里的版本替换它，" +
                      "需要显式 overwrite: true（**会改变项目里所有已放置实例**）。" +
                      "只要其中几个类型时用 typeNames 挑着载，别把几十个类型全塞进项目。",
        Destructive = false,
        TimeoutSeconds = 300)]
    public sealed class LoadFamilyTool : RevitTool<LoadFamilyInput, LoadFamilyOutput>
    {
        public override LoadFamilyOutput Execute(
            LoadFamilyInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);
            var path = ResolveSource(input.Path);
            var overwrite = input.Overwrite == true;

            // 同名族先查出来：既要在"不覆盖"时把它原样返回，
            // 也要在覆盖后能说清楚"替换掉的是哪一个"
            var familyName = System.IO.Path.GetFileNameWithoutExtension(path);
            var existing = FindFamily(document, familyName);

            if (existing != null && !overwrite)
            {
                context.Warnings.Add(
                    "项目里已经有名为「" + SafeName(existing) + "」的族，没有载入文件里的版本。" +
                    "返回的是已有的那一个——如果你要的就是它，直接用回执里的类型 ID；" +
                    "确实要用文件里的新版本替换它（**会改变所有已放置实例**），带 overwrite: true 再来一次。");

                return Describe(document, existing, loaded: false, replaced: false);
            }

            var options = new LoadOptions(overwrite);
            Family family;

            try
            {
                if (input.TypeNames != null && input.TypeNames.Count > 0)
                    family = LoadSelected(document, path, input.TypeNames, options, context);
                else if (!document.LoadFamily(path, options, out family))
                    family = null;
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "Revit 拒绝载入 " + path + "：" + ex.Message +
                    "。常见原因：族文件是更高版本的 Revit 存的、文件损坏、" +
                    "或它的类别在本项目里被关掉了。");
            }

            if (family == null)
            {
                // LoadFamily 返回 false 而不抛异常的典型情形：同名族存在且 OnFamilyFound 拒绝了覆盖
                if (existing != null)
                    return Describe(document, existing, loaded: false, replaced: false);

                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "Revit 没有载入 " + path + "，也没有说为什么。" +
                    "请确认这个文件是族文件（.rfa）而不是项目文件，且能在 Revit 界面里正常打开。");
            }

            return Describe(document, family, loaded: true, replaced: existing != null);
        }

        /// <summary>
        /// 只载入点名的那几个类型。
        ///
        /// <c>LoadFamilySymbol</c> 一次载一个类型，第一次调用会把族本身带进来。
        /// 名字写错了要当场说清楚——否则调用方会拿着一个"载入成功"的回执，
        /// 去找一个根本不存在的 typeId。
        /// </summary>
        private static Family LoadSelected(
            Document document, string path, List<string> typeNames, LoadOptions options,
            ToolExecutionContext<UIApplication> context)
        {
            Family family = null;
            var missing = new List<string>();

            foreach (var raw in typeNames)
            {
                var name = (raw ?? string.Empty).Trim();
                if (name.Length == 0) continue;

                FamilySymbol symbol;
                if (document.LoadFamilySymbol(path, name, options, out symbol) && symbol != null)
                {
                    family = symbol.Family;
                    continue;
                }

                missing.Add(name);
            }

            if (missing.Count > 0)
            {
                var known = family == null ? null : NamesOf(document, family);

                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "族文件里没有这些类型：" + string.Join("、", missing.ToArray()) + "。" +
                    (known != null && known.Count > 0
                        ? "已载入的是：" + string.Join("、", known.ToArray()) + "。"
                        : "") +
                    "类型名要与族文件里的完全一致（含空格与大小写）。");
            }

            if (family == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "typeNames 里没有一个有效的类型名。省略这个参数可以载入族文件里的全部类型。");

            return family;
        }

        /// <summary>
        /// 校验族文件路径。
        ///
        /// 三件事当场查清楚，Revit 自己的报错含糊到没法照着改：
        /// 是不是完整路径、扩展名对不对、文件在不在。
        /// </summary>
        private static string ResolveSource(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    @"path 不能为空，需要族文件的完整路径（含盘符），如 C:\族库\单扇门.rfa。");

            var path = raw.Trim();

            if (path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "path 含有路径里不允许的字符：收到 \"" + raw + "\"。");

            if (!System.IO.Path.IsPathRooted(path))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "path 必须是完整路径（含盘符），收到 \"" + raw + "\"。");

            var extension = System.IO.Path.GetExtension(path);
            if (!string.Equals(extension, ".rfa", StringComparison.OrdinalIgnoreCase))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "载入的必须是族文件（.rfa），收到 \"" + extension + "\"。" +
                    "项目文件（.rvt）用 revit_open_document 打开，不是载入。");

            if (!File.Exists(path))
                throw new ToolFailureException(McpDomainError.ElementNotFound,
                    "找不到族文件：" + path + "。请确认路径，或让用户把族文件放到这个位置。");

            return System.IO.Path.GetFullPath(path);
        }

        private static Family FindFamily(Document document, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            try
            {
                return new FilteredElementCollector(document)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .FirstOrDefault(f => string.Equals(SafeName(f), name, StringComparison.OrdinalIgnoreCase));
            }
            catch { return null; }
        }

        private static LoadFamilyOutput Describe(Document document, Family family, bool loaded, bool replaced)
        {
            var output = new LoadFamilyOutput
            {
                Id = family.Id.ToProtocolString(),
                Name = SafeName(family),
                Category = SafeCategory(family),
                Loaded = loaded,
                Replaced = replaced
            };

            try
            {
                foreach (var id in family.GetFamilySymbolIds())
                {
                    var symbol = document.GetElement(id) as ElementType;
                    if (symbol == null) continue;

                    output.Types.Add(new LoadedFamilyType
                    {
                        Id = symbol.Id.ToProtocolString(),
                        Name = SafeName(symbol)
                    });
                }
            }
            catch { /* 类型列不出来不影响"族已经载入"这个事实 */ }

            output.Types = output.Types
                .OrderBy(t => t.Name, StringComparer.CurrentCulture)
                .ToList();

            return output;
        }

        private static List<string> NamesOf(Document document, Family family)
        {
            var names = new List<string>();

            try
            {
                foreach (var id in family.GetFamilySymbolIds())
                {
                    var name = SafeName(document.GetElement(id));
                    if (name != null) names.Add(name);
                }
            }
            catch { }

            return names;
        }

        private static string SafeName(Element element)
        {
            try { return element == null ? null : element.Name; }
            catch { return null; }
        }

        private static string SafeCategory(Family family)
        {
            try { return family.FamilyCategory == null ? null : family.FamilyCategory.Name; }
            catch { return null; }
        }

        /// <summary>
        /// 载入策略。
        ///
        /// Revit 用这个回调问两件事：族已经在项目里了怎么办、共享的嵌套族以谁为准。
        /// 两个问题都只有调用方能回答，所以这里**不替它做决定**：
        /// 没要求覆盖就拒绝载入（返回 false），要求了就连参数值一起覆盖——
        /// "覆盖族但保留项目里的参数值"听着温和，实际会让同一个族在不同项目里行为不同，
        /// 是最难排查的那类差异。
        /// </summary>
        private sealed class LoadOptions : IFamilyLoadOptions
        {
            private readonly bool _overwrite;

            public LoadOptions(bool overwrite)
            {
                _overwrite = overwrite;
            }

            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = _overwrite;
                return _overwrite;
            }

            public bool OnSharedFamilyFound(
                Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                // 嵌套的共享族：覆盖时以族文件里的为准，否则保留项目里已有的那个
                source = _overwrite ? FamilySource.Family : FamilySource.Project;
                overwriteParameterValues = _overwrite;
                return _overwrite;
            }
        }
    }
}
