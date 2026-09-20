using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Addin.Configuration;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    // ==================== 创建项目参数 ====================

    public sealed class CreateProjectParameterInput
    {
        [McpParam("参数名。项目内必须唯一", Required = true)]
        public string Name { get; set; }

        [McpParam("数据类型：text（默认）、multilineText、integer、number、length、area、" +
                  "volume、angle、yesNo、material、url。" +
                  "length/area/volume 会带单位，按项目显示单位读写",
                  AllowedValues = new[] { "text", "multilineText", "integer", "number", "length", "area", "volume", "angle", "yesNo", "material", "url" })]
        public string DataType { get; set; }

        [McpParam("要绑定到哪些类别，如 [\"OST_Walls\", \"OST_Doors\"]。可省略 OST_ 前缀", Required = true)]
        public List<string> Categories { get; set; }

        [McpParam("绑定方式：instance（实例参数，默认——每个构件可以有不同的值）、" +
                  "type（类型参数——同类型的构件共用一个值）",
                  AllowedValues = new[] { "instance", "type" })]
        public string Binding { get; set; }

        [McpParam("参数出现在属性面板的哪一栏：data（默认，「数据」）、identityData（「标识数据」）、" +
                  "text（「文字」）、geometry（「尺寸标注」）、construction（「构造」）、reference（「参照」）",
                  AllowedValues = new[] { "data", "identityData", "text", "geometry", "construction", "reference" })]
        public string Group { get; set; }

        [McpParam("共享参数分组名，写进共享参数文件里的。省略则用 RevitMCP")]
        public string SharedParameterGroup { get; set; }
    }

    public sealed class CreateProjectParameterOutput
    {
        [McpParam("参数名")]
        public string Name { get; set; }

        [McpParam("数据类型")]
        public string DataType { get; set; }

        [McpParam("绑定方式：instance / type")]
        public string Binding { get; set; }

        [McpParam("所在属性分组")]
        public string Group { get; set; }

        [McpParam("绑定到的类别")]
        public List<string> Categories { get; set; } = new List<string>();

        [McpParam("共享参数的 GUID。跨项目复用同一个参数时靠它对齐")]
        public string Guid { get; set; }

        [McpParam("共享参数文件的路径。这个文件是项目之外的资产，要跟着项目一起交付")]
        public string SharedParameterFile { get; set; }

        [McpParam("这个定义是本次新建的，还是共享参数文件里已经有的")]
        public bool DefinitionCreated { get; set; }
    }

    /// <summary>
    /// 创建项目参数。
    ///
    /// 有一件事必须说在前面：**Revit API 造不出"非共享"的项目参数**。
    /// 参数绑定需要一个 <c>Definition</c>，而 API 能拿到的 Definition
    /// 只有共享参数文件里的那种。UI 上那个"项目参数（非共享）"选项在 API 里没有对应入口。
    /// 所以这个工具实际做的是：往共享参数文件里写一条定义，再把它绑定到项目。
    ///
    /// 这不是实现偷懒，而是必须让调用方知道的后果——
    /// 共享参数文件是项目之外的一个文件，交付时漏掉它，别人打开项目会看到参数但对不上。
    /// </summary>
    [McpTool("revit_create_project_parameter",
        Toolsets = new[] { Toolsets.Authoring },
        Title = "创建项目参数",
        Description = "在项目里创建一个参数并绑定到指定类别。" +
                      "**Revit API 只能造共享参数**（非共享的项目参数没有 API 入口），" +
                      "所以参数定义会写进一个共享参数文件，路径在回执里——" +
                      "那个文件是项目之外的资产，交付时要一起给，否则别人对不上这个参数。" +
                      "创建后用 revit_set_element_parameters 写值，用 revit_get_element_parameters 读值。",
        Destructive = false,
        TimeoutSeconds = 120)]
    public sealed class CreateProjectParameterTool
        : RevitTool<CreateProjectParameterInput, CreateProjectParameterOutput>
    {
        public override CreateProjectParameterOutput Execute(
            CreateProjectParameterInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);
            var application = context.Host.Application;

            if (string.IsNullOrWhiteSpace(input.Name))
                throw new ToolFailureException(McpDomainError.InvalidParameter, "name 不能为空。");

            if (input.Categories == null || input.Categories.Count == 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "categories 不能为空——一个不绑定到任何类别的参数不会出现在任何构件上。");

            var name = input.Name.Trim();
            var isInstance = ParseBinding(input.Binding);

            RejectIfBound(document, name, isInstance);

            var categorySet = BuildCategorySet(document, application, input.Categories);
            var file = ProjectParameterSupport.OpenSharedParameterFile(application, context);

            var groupName = string.IsNullOrWhiteSpace(input.SharedParameterGroup)
                ? "RevitMCP"
                : input.SharedParameterGroup.Trim();

            var created = false;
            var definition = ProjectParameterSupport.FindDefinition(file, name);

            if (definition == null)
            {
                definition = CreateDefinition(file, groupName, name, input.DataType);
                created = true;
            }
            else
            {
                context.Warnings.Add(
                    "共享参数文件里已经有名为「" + name + "」的定义，直接复用它。" +
                    "它的数据类型是「" + ParameterDefinitionCompat.DataTypeOf(definition) +
                    "」，与本次请求的 dataType 可能不同——共享参数的类型一经创建就改不了。");
            }

            var binding = isInstance
                ? (Binding)application.Create.NewInstanceBinding(categorySet)
                : application.Create.NewTypeBinding(categorySet);

            ParameterDefinitionCompat.Bind(document, definition, binding, input.Group ?? "data");

            var external = definition as ExternalDefinition;

            return new CreateProjectParameterOutput
            {
                Name = name,
                DataType = ParameterDefinitionCompat.DataTypeOf(definition),
                Binding = isInstance ? "instance" : "type",
                Group = ParameterDefinitionCompat.GroupNameOf(definition) ?? (input.Group ?? "data"),
                Categories = categorySet.Cast<Category>().Select(c => c.Name).OrderBy(n => n).ToList(),
                Guid = external?.GUID.ToString(),
                SharedParameterFile = application.SharedParametersFilename,
                DefinitionCreated = created
            };
        }

        private static bool ParseBinding(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;

            switch (value.Trim().ToLowerInvariant())
            {
                case "instance": return true;
                case "type": return false;

                default:
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "无法识别的 binding \"" + value +
                        "\"。可用值：instance（实例参数）、type（类型参数）。");
            }
        }

        /// <summary>
        /// 同名参数已经绑定过就直接拒绝。
        ///
        /// Revit 允许"再插入一次"，但行为是**替换掉原来的类别集合**——
        /// 调用方以为自己在往上加类别，实际上把之前绑的全冲掉了，
        /// 而那些构件上已经填好的值会跟着一起消失。
        /// </summary>
        private static void RejectIfBound(Document document, string name, bool isInstance)
        {
            var iterator = document.ParameterBindings.ForwardIterator();
            iterator.Reset();

            while (iterator.MoveNext())
            {
                var definition = iterator.Key;
                if (definition == null || !string.Equals(definition.Name, name, StringComparison.Ordinal))
                    continue;

                var existing = iterator.Current as Binding;
                var existingKind = existing is InstanceBinding ? "instance" : "type";

                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "项目里已经有名为「" + name + "」的参数（绑定方式：" + existingKind + "）。" +
                    "重新绑定会**替换掉**它当前的类别集合，构件上已填的值会一起丢失，所以这里直接拒绝。" +
                    "想加类别请换个参数名，或让用户在 Revit 的「项目参数」对话框里手工修改。" +
                    (existingKind == (isInstance ? "instance" : "type")
                        ? ""
                        : " 另外，本次请求的绑定方式与它现有的不同——同一个参数名只能有一种绑定方式。"));
            }
        }

        private static CategorySet BuildCategorySet(
            Document document, Autodesk.Revit.ApplicationServices.Application application,
            IList<string> rawCategories)
        {
            var set = application.Create.NewCategorySet();

            foreach (var raw in rawCategories)
            {
                var builtIn = ParseCategory(raw);
                var category = Category.GetCategory(document, builtIn);

                if (category == null)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "本项目里没有类别 " + builtIn + "，绑定不上去。");

                if (!category.AllowsBoundParameters)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "类别「" + category.Name + "」（" + builtIn +
                        "）不接受项目参数——Revit 里有一部分类别就是这样，" +
                        "不是所有东西都能挂自定义参数。");

                set.Insert(category);
            }

            if (set.IsEmpty)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "没有一个有效的类别可以绑定。");

            return set;
        }

        private static Definition CreateDefinition(
            DefinitionFile file, string groupName, string name, string dataType)
        {
            var group = file.Groups.Cast<DefinitionGroup>()
                            .FirstOrDefault(g => string.Equals(g.Name, groupName, StringComparison.Ordinal))
                        ?? file.Groups.Create(groupName);

            var options = ParameterDefinitionCompat.CreateOptions(dataType, name);

            // 允许出现在明细表里，否则这个参数造出来就没法用于统计——
            // 而统计恰恰是自定义参数最主要的用途
            options.Visible = true;

            try
            {
                return group.Definitions.Create(options);
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "在共享参数文件里创建定义「" + name + "」失败：" + ex.Message);
            }
        }
    }

    // ==================== 参数清单 ====================

    public sealed class ListProjectParametersInput
    {
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档")]
        public string DocumentId { get; set; }

        [McpParam("按参数名过滤（不区分大小写的子串匹配）")]
        public string NameContains { get; set; }

        [McpParam("是否一并列出共享参数文件里**尚未绑定到本项目**的定义，默认 false。" +
                  "想复用别的项目定义好的参数时开它")]
        public bool? IncludeUnboundShared { get; set; }
    }

    public sealed class ProjectParameterInfo
    {
        [McpParam("参数名")]
        public string Name { get; set; }

        [McpParam("数据类型")]
        public string DataType { get; set; }

        [McpParam("绑定方式：instance / type。未绑定到本项目的为 null")]
        public string Binding { get; set; }

        [McpParam("所在属性分组")]
        public string Group { get; set; }

        [McpParam("绑定到的类别名。未绑定的为空")]
        public List<string> Categories { get; set; } = new List<string>();

        [McpParam("是不是共享参数")]
        public bool IsShared { get; set; }

        [McpParam("共享参数的 GUID")]
        public string Guid { get; set; }

        [McpParam("是否已绑定到本项目。false 表示它只存在于共享参数文件里")]
        public bool BoundToProject { get; set; }
    }

    public sealed class ListProjectParametersOutput
    {
        [McpParam("参数总数")]
        public int Total { get; set; }

        [McpParam("当前的共享参数文件路径。没设置则为 null")]
        public string SharedParameterFile { get; set; }

        [McpParam("参数列表")]
        public List<ProjectParameterInfo> Parameters { get; set; } = new List<ProjectParameterInfo>();
    }

    [McpTool("revit_list_project_parameters",
        Toolsets = new[] { Toolsets.Authoring },
        Title = "列出项目参数",
        Description = "列出项目里已绑定的参数及其数据类型、绑定方式、绑定到哪些类别。" +
                      "带上 includeUnboundShared 还会列出共享参数文件里尚未绑定的定义。" +
                      "创建新参数前先调它确认重名——同名参数会被拒绝。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class ListProjectParametersTool
        : RevitTool<ListProjectParametersInput, ListProjectParametersOutput>
    {
        public override ListProjectParametersOutput Execute(
            ListProjectParametersInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = ResolveDocument(context, input.DocumentId);
            var application = context.Host.Application;

            var output = new ListProjectParametersOutput
            {
                SharedParameterFile = SafeSharedFileName(application)
            };

            var bound = new HashSet<string>(StringComparer.Ordinal);

            var iterator = document.ParameterBindings.ForwardIterator();
            iterator.Reset();

            while (iterator.MoveNext())
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var definition = iterator.Key;
                if (definition == null) continue;

                if (!Matches(definition.Name, input.NameContains)) continue;

                bound.Add(definition.Name);

                var binding = iterator.Current as ElementBinding;
                var info = new ProjectParameterInfo
                {
                    Name = definition.Name,
                    DataType = ParameterDefinitionCompat.DataTypeOf(definition),
                    Group = ParameterDefinitionCompat.GroupNameOf(definition),
                    Binding = binding is InstanceBinding ? "instance" : "type",
                    BoundToProject = true
                };

                var external = definition as ExternalDefinition;
                if (external != null)
                {
                    info.IsShared = true;
                    try { info.Guid = external.GUID.ToString(); } catch { }
                }

                if (binding?.Categories != null)
                    info.Categories = binding.Categories.Cast<Category>()
                        .Select(c => c.Name)
                        .OrderBy(n => n, StringComparer.CurrentCulture)
                        .ToList();

                output.Parameters.Add(info);
            }

            if (input.IncludeUnboundShared == true)
                AppendUnboundShared(application, context, input.NameContains, bound, output);

            output.Parameters = output.Parameters
                .OrderByDescending(p => p.BoundToProject)
                .ThenBy(p => p.Name, StringComparer.CurrentCulture)
                .ToList();

            output.Total = output.Parameters.Count;
            return output;
        }

        private static void AppendUnboundShared(
            Autodesk.Revit.ApplicationServices.Application application,
            ToolExecutionContext<UIApplication> context, string nameContains,
            HashSet<string> bound, ListProjectParametersOutput output)
        {
            DefinitionFile file;
            try { file = application.OpenSharedParameterFile(); }
            catch { file = null; }

            if (file == null)
            {
                context.Warnings.Add(
                    "没有设置共享参数文件，或文件打不开，includeUnboundShared 没有内容可列。");
                return;
            }

            foreach (var group in file.Groups.Cast<DefinitionGroup>())
            {
                foreach (var definition in group.Definitions.Cast<Definition>())
                {
                    if (bound.Contains(definition.Name)) continue;
                    if (!Matches(definition.Name, nameContains)) continue;

                    var info = new ProjectParameterInfo
                    {
                        Name = definition.Name,
                        DataType = ParameterDefinitionCompat.DataTypeOf(definition),
                        Group = group.Name,
                        IsShared = true,
                        BoundToProject = false
                    };

                    var external = definition as ExternalDefinition;
                    if (external != null)
                    {
                        try { info.Guid = external.GUID.ToString(); } catch { }
                    }

                    output.Parameters.Add(info);
                }
            }
        }

        private static bool Matches(string name, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return true;
            return name != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string SafeSharedFileName(Autodesk.Revit.ApplicationServices.Application application)
        {
            try
            {
                var path = application.SharedParametersFilename;
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
            catch { return null; }
        }
    }

    // ==================== 共用零件 ====================

    internal static class ProjectParameterSupport
    {
        /// <summary>
        /// 拿到可用的共享参数文件。
        ///
        /// 用户没设过共享参数文件时，在服务自己的数据目录下建一个。
        /// 这会改掉 Revit 的一项全局设置（<c>SharedParametersFilename</c> 是应用级的），
        /// 所以一定要说出来——用户下次在 Revit 里手工加共享参数时，
        /// 看到的会是这个文件而不是他原本的那个。
        /// </summary>
        public static DefinitionFile OpenSharedParameterFile(
            Autodesk.Revit.ApplicationServices.Application application,
            ToolExecutionContext<UIApplication> context)
        {
            var current = SafeFileName(application);

            if (!string.IsNullOrWhiteSpace(current) && File.Exists(current))
            {
                var existing = TryOpen(application);
                if (existing != null) return existing;

                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "共享参数文件 " + current + " 打不开（可能格式损坏或没有读权限）。" +
                    "请让用户在 Revit 的「管理 → 共享参数」里重新指定一个。");
            }

            var path = DefaultSharedParameterPath();

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                if (!File.Exists(path))
                {
                    // 共享参数文件是纯文本，Revit 认的是制表符分隔的那套表头。
                    // 空文件它也接受，会自己补上表头
                    File.WriteAllText(path, string.Empty, new System.Text.UTF8Encoding(false));
                }

                application.SharedParametersFilename = path;
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "准备共享参数文件 " + path + " 失败：" + ex.Message);
            }

            var file = TryOpen(application);

            if (file == null)
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "已创建共享参数文件 " + path + "，但 Revit 打不开它。");

            context.Warnings.Add(
                "项目原本没有设置共享参数文件，已在 " + path + " 新建一个并设为当前文件。" +
                "**这是 Revit 的一项全局设置**——用户下次手工添加共享参数时看到的会是这个文件。" +
                "交付项目时别忘了带上它，否则别人对不上这些参数。");

            return file;
        }

        public static Definition FindDefinition(DefinitionFile file, string name)
        {
            foreach (var group in file.Groups.Cast<DefinitionGroup>())
            {
                var match = group.Definitions.Cast<Definition>()
                    .FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.Ordinal));

                if (match != null) return match;
            }

            return null;
        }

        private static DefinitionFile TryOpen(Autodesk.Revit.ApplicationServices.Application application)
        {
            try { return application.OpenSharedParameterFile(); }
            catch { return null; }
        }

        private static string SafeFileName(Autodesk.Revit.ApplicationServices.Application application)
        {
            try { return application.SharedParametersFilename; }
            catch { return null; }
        }

        private static string DefaultSharedParameterPath()
        {
            return Path.Combine(
                Path.GetDirectoryName(McpConfig.DefaultPath) ?? McpConfig.DefaultExportDirectory,
                "RevitMCP-SharedParameters.txt");
        }
    }
}
