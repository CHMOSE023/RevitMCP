using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Addin.Diagnostics;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    // ==================== 直接调用 Revit API ====================

    public sealed class ApiArgument
    {
        [McpParam("实参的类型：string、int、long、double、bool、elementId、xyz、null。" +
                  "elementId 传 ID 字符串；xyz 传 \"x,y,z\"（毫米）", Required = true,
                  AllowedValues = new[] { "string", "int", "long", "double", "bool", "elementId", "xyz", "null" })]
        public string Type { get; set; }

        [McpParam("实参的值，一律用字符串表示。type 为 null 时忽略")]
        public string Value { get; set; }
    }

    public sealed class InvokeApiInput
    {
        [McpParam("操作：call（调方法）、get（读属性或字段）、set（写属性或字段）", Required = true,
                  AllowedValues = new[] { "call", "get", "set" })]
        public string Action { get; set; }

        [McpParam("目标构件 ID。给了它就在这个构件上操作；" +
                  "省略则把 typeName 当成静态类型，做静态调用。ElementId 与 uniqueId 两种写法都接受")]
        public string TargetId { get; set; }

        [McpParam("类型全名，如 Autodesk.Revit.DB.ElementTransformUtils。" +
                  "targetId 给了时可以省略——那时用构件自己的运行时类型。" +
                  "静态调用必填")]
        public string TypeName { get; set; }

        [McpParam("方法名、属性名或字段名", Required = true)]
        public string Member { get; set; }

        [McpParam("实参列表，顺序与方法签名一致。get 不需要；" +
                  "set 时第一个就是要写入的值")]
        public List<ApiArgument> Arguments { get; set; }

        [McpParam("确认要执行。这个工具能调用任意 Revit API，必须显式确认", Required = true)]
        public bool Confirm { get; set; }
    }

    public sealed class InvokeApiOutput
    {
        [McpParam("实际解析到的成员签名——确认调到的是不是你想要的那个重载")]
        public string Resolved { get; set; }

        [McpParam("返回值的类型全名。void 为 null")]
        public string ResultType { get; set; }

        [McpParam("返回值的字符串表示。ElementId 会给出数字，集合会给出元素个数与前若干项")]
        public string Result { get; set; }

        [McpParam("返回值里包含的构件 ID，如果能识别出来的话")]
        public List<string> ElementIds { get; set; } = new List<string>();
    }

    /// <summary>
    /// 反射调用 Revit API。
    ///
    /// 这是一个**逃生舱**：当某个能力还没有被封装成工具时，它让调用方不必干等。
    /// 代价是它绕开了本项目其余部分的全部保证——
    /// 没有单位换算、没有参数校验、没有规模闸、错误信息是 .NET 的原始异常。
    ///
    /// 它受两道闸管辖：写保护（「修改模型」）与配置里的 <c>escapeHatchEnabled</c>。
    /// 后者刻意只能改配置文件，Ribbon 上没有一键开关——
    /// 开启"允许任意代码在 Revit 进程里跑"应当是一个需要停下来想一想的动作。
    /// </summary>
    [McpTool("revit_invoke_api",
        Toolsets = new[] { Toolsets.Escape },
        Title = "直接调用 Revit API（逃生舱）",
        Description = "用反射调用任意 Revit API 方法、读写任意属性。" +
                      "**这是逃生舱，不是常规工具**：没有单位换算（长度按 Revit 内部单位英尺）、" +
                      "没有参数校验、没有规模闸，错误信息是 .NET 原始异常。" +
                      "先找有没有对应的专用工具——它们会处理单位、校验和批量事务。" +
                      "需要在配置里把 escapeHatchEnabled 设为 true 才能用。",
        TimeoutSeconds = 120)]
    public sealed class InvokeApiTool : RevitTool<InvokeApiInput, InvokeApiOutput>
    {
        public override InvokeApiOutput Execute(
            InvokeApiInput input, ToolExecutionContext<UIApplication> context)
        {
            EscapeHatch.RequireEnabled("revit_invoke_api");

            if (!input.Confirm)
                throw new ToolFailureException(McpDomainError.ConfirmationRequired,
                    "这个工具能调用任意 Revit API，后果不受本服务的任何校验保护。" +
                    "确认要执行后带上 confirm: true 重新调用。");

            var document = RequireDocument(context);
            var action = (input.Action ?? string.Empty).Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(input.Member))
                throw new ToolFailureException(McpDomainError.InvalidParameter, "member 不能为空。");

            object target = null;
            Type type;

            if (!string.IsNullOrWhiteSpace(input.TargetId))
            {
                target = RequireElement(document, input.TargetId);
                type = string.IsNullOrWhiteSpace(input.TypeName)
                    ? target.GetType()
                    : EscapeHatch.ResolveType(input.TypeName);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(input.TypeName))
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "没有给 targetId 时必须给 typeName（静态调用的目标类型）。");

                type = EscapeHatch.ResolveType(input.TypeName);
            }

            switch (action)
            {
                case "call": return Call(document, type, target, input);
                case "get": return Get(type, target, input);
                case "set": return Set(document, type, target, input);

                default:
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "无法识别的 action \"" + input.Action +
                        "\"。可用值：call（调方法）、get（读属性/字段）、set（写属性/字段）。");
            }
        }

        private static InvokeApiOutput Call(
            Document document, Type type, object target, InvokeApiInput input)
        {
            var arguments = (input.Arguments ?? new List<ApiArgument>())
                .Select(a => EscapeHatch.Convert(document, a))
                .ToArray();

            var candidates = type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                            BindingFlags.FlattenHierarchy)
                .Where(m => string.Equals(m.Name, input.Member.Trim(), StringComparison.Ordinal))
                .Where(m => m.GetParameters().Length == arguments.Length)
                .ToList();

            if (candidates.Count == 0)
                throw NotFound(type, input.Member, "方法", arguments.Length);

            var method = EscapeHatch.PickOverload(candidates, arguments, type, input.Member);

            object result;
            try
            {
                result = method.Invoke(method.IsStatic ? null : target, arguments);
            }
            catch (TargetInvocationException ex)
            {
                // 反射会把真正的异常包一层，直接抛出去只会得到一句没用的
                // "调用的目标发生了异常"
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "调用 " + type.Name + "." + method.Name + " 抛出异常：" +
                    (ex.InnerException?.Message ?? ex.Message));
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "调用 " + type.Name + "." + method.Name + " 失败：" + ex.Message);
            }

            return EscapeHatch.Describe(result, EscapeHatch.Signature(method));
        }

        private static InvokeApiOutput Get(Type type, object target, InvokeApiInput input)
        {
            var name = input.Member.Trim();
            var property = FindProperty(type, name);

            if (property != null)
            {
                if (!property.CanRead)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        type.Name + "." + name + " 是只写属性。");

                return EscapeHatch.Describe(
                    Read(() => property.GetValue(target, null), type, name),
                    property.PropertyType.Name + " " + type.Name + "." + name + " { get; }");
            }

            var field = FindField(type, name);

            if (field == null) throw NotFound(type, name, "属性或字段", -1);

            return EscapeHatch.Describe(
                Read(() => field.GetValue(target), type, name),
                field.FieldType.Name + " " + type.Name + "." + name);
        }

        private static InvokeApiOutput Set(
            Document document, Type type, object target, InvokeApiInput input)
        {
            if (input.Arguments == null || input.Arguments.Count != 1)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "action 为 set 时必须且只能给一个 arguments 项——要写入的值。");

            var name = input.Member.Trim();
            var value = EscapeHatch.Convert(document, input.Arguments[0]);

            var property = FindProperty(type, name);

            if (property != null)
            {
                if (!property.CanWrite)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        type.Name + "." + name + " 是只读属性，写不了。");

                Write(() => property.SetValue(target, value, null), type, name);

                return EscapeHatch.Describe(value,
                    property.PropertyType.Name + " " + type.Name + "." + name + " { set; }");
            }

            var field = FindField(type, name);
            if (field == null) throw NotFound(type, name, "属性或字段", -1);

            Write(() => field.SetValue(target, value), type, name);

            return EscapeHatch.Describe(value, field.FieldType.Name + " " + type.Name + "." + name);
        }

        private static PropertyInfo FindProperty(Type type, string name)
        {
            return type.GetProperty(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.FlattenHierarchy);
        }

        private static FieldInfo FindField(Type type, string name)
        {
            return type.GetField(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.FlattenHierarchy);
        }

        private static object Read(Func<object> read, Type type, string name)
        {
            try { return read(); }
            catch (TargetInvocationException ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "读取 " + type.Name + "." + name + " 抛出异常：" +
                    (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private static void Write(Action write, Type type, string name)
        {
            try { write(); }
            catch (TargetInvocationException ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "写入 " + type.Name + "." + name + " 抛出异常：" +
                    (ex.InnerException?.Message ?? ex.Message));
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "写入 " + type.Name + "." + name + " 失败：" + ex.Message +
                    "。值的类型可能不匹配。");
            }
        }

        /// <summary>
        /// 找不到成员时，把这个类型上**名字接近**的成员列出来。
        /// 反射失败最常见的原因是名字记错或版本不同，光说"找不到"帮不上忙。
        /// </summary>
        private static ToolFailureException NotFound(Type type, string name, string kind, int argumentCount)
        {
            var needle = name.Trim();

            var similar = type
                .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                            BindingFlags.FlattenHierarchy)
                .Select(m => m.Name)
                .Distinct(StringComparer.Ordinal)
                .Where(n => n.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            needle.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(n => n.Length)
                .Take(10)
                .ToArray();

            var hint = similar.Length > 0
                ? "。名字相近的有：" + string.Join("、", similar)
                : "。请对照本版本 Revit API 的文档确认成员名——不同版本之间会有增删。";

            var counted = argumentCount >= 0
                ? "（接受 " + argumentCount + " 个参数的）"
                : string.Empty;

            return new ToolFailureException(McpDomainError.InvalidParameter,
                type.FullName + " 上没有名为 \"" + name + "\" 的" + counted + kind + hint);
        }
    }

    // ==================== 执行脚本 ====================

    public sealed class ExecuteScriptInput
    {
        [McpParam("要执行的 C# 代码。它会成为一个方法体，可以直接用这些变量：\n" +
                  "· uiapp（UIApplication）、uidoc（UIDocument）、doc（Document）\n" +
                  "· 已 using：System、System.Linq、System.Collections.Generic、" +
                  "Autodesk.Revit.DB、Autodesk.Revit.DB.Structure、Autodesk.Revit.UI\n" +
                  "用 return 返回结果（object）；不 return 则返回 null。\n" +
                  "**已经在事务里了**，不要自己开事务", Required = true)]
        public string Code { get; set; }

        [McpParam("额外要 using 的命名空间")]
        public List<string> Usings { get; set; }

        [McpParam("确认要执行。这个工具会在 Revit 进程里编译并运行你给的代码", Required = true)]
        public bool Confirm { get; set; }
    }

    public sealed class ExecuteScriptOutput
    {
        [McpParam("返回值的类型全名。返回 null 时为 null")]
        public string ResultType { get; set; }

        [McpParam("返回值的字符串表示")]
        public string Result { get; set; }

        [McpParam("返回值里包含的构件 ID，如果能识别出来的话")]
        public List<string> ElementIds { get; set; } = new List<string>();

        [McpParam("脚本里用 Print(...) 输出的内容")]
        public List<string> Output { get; set; } = new List<string>();

        [McpParam("编译耗时，毫秒")]
        public long CompileMs { get; set; }
    }

    /// <summary>
    /// 编译并执行一段 C# 代码。
    ///
    /// **用的是 C# 而不是 Python**，这是一个有意的取舍：
    /// 本项目全程零第三方依赖，理由是 Revit 把所有插件加载进同一个 AppDomain
    /// 且不应用插件自己的绑定重定向（架构 §2 的 C2）——
    /// 引入 IronPython 就必须同时引入 ILRepack 那一整套内联化设施，
    /// 而它带来的仍然只是"执行任意代码"这一个能力。
    /// .NET Framework 自带的 <c>CSharpCodeProvider</c> 提供同样的能力、零依赖，
    /// 而且脚本里用的就是 Revit API 本身的类型，不需要跨语言的类型映射。
    ///
    /// 与 <see cref="InvokeApiTool"/> 的分工：那个适合一次调用，
    /// 这个适合需要循环、条件、临时变量的一小段逻辑。
    /// </summary>
    [McpTool("revit_execute_script",
        Toolsets = new[] { Toolsets.Escape },
        Title = "执行 C# 脚本（逃生舱）",
        Description = "在 Revit 进程里编译并执行一段 C# 代码，可以直接用 doc、uidoc、uiapp。" +
                      "**这是逃生舱，不是常规工具**：没有单位换算（长度用 Revit 内部单位英尺）、" +
                      "没有任何校验，写错了就是直接改坏模型。先找有没有对应的专用工具。" +
                      "代码已经运行在事务里，不要自己开事务；抛异常会让整个事务回滚。" +
                      "用 Print(...) 输出中间值，用 return 返回结果。" +
                      "需要在配置里把 escapeHatchEnabled 设为 true 才能用。" +
                      "（用 C# 而非 Python：本插件零第三方依赖，详见工具说明。）",
        TimeoutSeconds = 300)]
    public sealed class ExecuteScriptTool : RevitTool<ExecuteScriptInput, ExecuteScriptOutput>
    {
        private const string EntryType = "RevitMcpScript.Entry";
        private const string EntryMethod = "Run";

        public override ExecuteScriptOutput Execute(
            ExecuteScriptInput input, ToolExecutionContext<UIApplication> context)
        {
            EscapeHatch.RequireEnabled("revit_execute_script");

            if (!input.Confirm)
                throw new ToolFailureException(McpDomainError.ConfirmationRequired,
                    "这个工具会在 Revit 进程里编译并运行你给的代码，后果不受本服务的任何校验保护。" +
                    "确认要执行后带上 confirm: true 重新调用。");

            if (string.IsNullOrWhiteSpace(input.Code))
                throw new ToolFailureException(McpDomainError.InvalidParameter, "code 不能为空。");

            var uiDocument = RequireUiDocument(context);
            var source = BuildSource(input);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var assembly = Compile(source);
            stopwatch.Stop();

            var output = new ExecuteScriptOutput { CompileMs = stopwatch.ElapsedMilliseconds };
            var printed = new List<string>();

            var method = assembly.GetType(EntryType)?.GetMethod(EntryMethod);
            if (method == null)
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "脚本编译成功，但找不到入口方法。这是本工具的内部问题，请报告。");

            object result;
            try
            {
                result = method.Invoke(null, new object[]
                {
                    context.Host, uiDocument, uiDocument.Document,
                    (Action<object>)(value => printed.Add(Stringify(value)))
                });
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException ?? ex;

                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "脚本抛出 " + inner.GetType().Name + "：" + inner.Message +
                    "（模型改动已全部回滚）" +
                    (printed.Count > 0
                        ? "\n抛异常前 Print 的输出：\n" + string.Join("\n", printed.ToArray())
                        : string.Empty));
            }

            output.Output = printed;

            var described = EscapeHatch.Describe(result, null);
            output.Result = described.Result;
            output.ResultType = described.ResultType;
            output.ElementIds = described.ElementIds;

            return output;
        }

        /// <summary>
        /// 把用户的代码包成一个完整的编译单元。
        ///
        /// 入口签名固定，参数按位置传进来而不是靠字段注入——
        /// 后者需要先实例化，而实例化的失败信息比"参数对不上"难懂得多。
        /// </summary>
        private static string BuildSource(ExecuteScriptInput input)
        {
            var builder = new StringBuilder();

            var namespaces = new List<string>
            {
                "System", "System.Linq", "System.Collections.Generic",
                "Autodesk.Revit.DB", "Autodesk.Revit.DB.Structure", "Autodesk.Revit.UI"
            };

            if (input.Usings != null)
            {
                foreach (var extra in input.Usings)
                {
                    if (string.IsNullOrWhiteSpace(extra)) continue;

                    var trimmed = extra.Trim().TrimEnd(';');

                    // using 是直接拼进源码的，带分号或大括号就能跳出这一行去写别的东西。
                    // 逃生舱本来就允许执行任意代码，但仍然不该有"看起来只是个命名空间"的注入面
                    if (trimmed.IndexOfAny(new[] { ';', '{', '}', '\n', '\r' }) >= 0)
                        throw new ToolFailureException(McpDomainError.InvalidParameter,
                            "usings 里的 \"" + extra + "\" 不是一个合法的命名空间名。");

                    if (!namespaces.Contains(trimmed)) namespaces.Add(trimmed);
                }
            }

            foreach (var ns in namespaces) builder.AppendLine("using " + ns + ";");

            builder.AppendLine();
            builder.AppendLine("namespace RevitMcpScript {");
            builder.AppendLine("  public static class Entry {");
            builder.AppendLine("    public static object Run(UIApplication uiapp, UIDocument uidoc, " +
                               "Document doc, Action<object> Print) {");
            builder.AppendLine("#line 1 \"script\"");
            builder.AppendLine(input.Code);
            builder.AppendLine("#line default");
            builder.AppendLine("      return null;");
            builder.AppendLine("    }");
            builder.AppendLine("  }");
            builder.AppendLine("}");

            return builder.ToString();
        }

        private static Assembly Compile(string source)
        {
            var parameters = new CompilerParameters
            {
                GenerateInMemory = true,
                GenerateExecutable = false,
                TreatWarningsAsErrors = false,

                // 优化关掉：编译速度比运行速度重要得多，
                // 脚本跑的是 Revit API 调用，瓶颈从来不在托管代码上
                CompilerOptions = "/optimize-"
            };

            foreach (var reference in ReferencePaths()) parameters.ReferencedAssemblies.Add(reference);

            CompilerResults results;
            try
            {
                using (var provider = new Microsoft.CSharp.CSharpCodeProvider())
                {
                    results = provider.CompileAssemblyFromSource(parameters, source);
                }
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "启动 C# 编译器失败：" + ex.Message +
                    "。这需要本机装有 .NET Framework 的编译器（csc.exe），通常随系统自带。");
            }

            if (results.Errors.HasErrors)
            {
                var messages = results.Errors.Cast<CompilerError>()
                    .Where(e => !e.IsWarning)
                    .Take(10)
                    .Select(e => "第 " + e.Line + " 行: " + e.ErrorNumber + " " + e.ErrorText);

                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "脚本编译失败：\n" + string.Join("\n", messages.ToArray()) +
                    "\n（行号对应 code 里的行）");
            }

            return results.CompiledAssembly;
        }

        /// <summary>
        /// 脚本要引用哪些程序集。
        /// 取的是**当前进程里已经加载的那一份** Revit API——
        /// 按路径去猜安装目录会在多版本共存的机器上引错版本。
        /// </summary>
        private static IEnumerable<string> ReferencePaths()
        {
            var wanted = new[]
            {
                "mscorlib", "System", "System.Core", "RevitAPI", "RevitAPIUI"
            };

            var paths = new List<string>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                string location;

                try
                {
                    name = assembly.GetName().Name;
                    location = assembly.IsDynamic ? null : assembly.Location;
                }
                catch { continue; }

                if (string.IsNullOrEmpty(location) || !File.Exists(location)) continue;
                if (!wanted.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (paths.Contains(location)) continue;

                paths.Add(location);
            }

            // 本插件自己也加进去：脚本里可能想用 Units、ElementIdCompat 这些帮手
            var self = typeof(ExecuteScriptTool).Assembly.Location;
            if (!string.IsNullOrEmpty(self) && File.Exists(self) && !paths.Contains(self))
                paths.Add(self);

            return paths;
        }

        private static string Stringify(object value)
        {
            if (value == null) return "null";

            try { return value.ToString(); }
            catch (Exception ex) { return "(ToString 抛异常：" + ex.Message + ")"; }
        }
    }

    // ==================== 逃生舱共用零件 ====================

    internal static class EscapeHatch
    {
        /// <summary>返回值里最多列出多少项。</summary>
        private const int MaxItems = 50;

        /// <summary>
        /// 检查逃生舱总闸。
        /// 与写保护分开：写保护管的是"能不能改模型"，
        /// 这道闸管的是"能不能在 Revit 进程里跑任意代码"——后者的影响面大得多。
        /// </summary>
        public static void RequireEnabled(string toolName)
        {
            if (App.Current?.Config?.EscapeHatchEnabled == true) return;

            throw new ToolFailureException(McpDomainError.WriteDisabled,
                toolName + " 是逃生舱工具，默认关闭。" +
                "它能在 Revit 进程里执行任意代码——读写任何文件、发任何网络请求，" +
                "远超出「改模型」的范围，所以它不跟着 Ribbon 上的写入开关走。" +
                "要开启，请在 " + Configuration.McpConfig.DefaultPath +
                " 里把 escapeHatchEnabled 设为 true，然后重启 Revit。" +
                "在那之前，请先确认没有现成的专用工具能做这件事。");
        }

        public static Type ResolveType(string typeName)
        {
            var name = typeName.Trim();

            var type = Type.GetType(name, throwOnError: false, ignoreCase: false);
            if (type != null) return type;

            // 没带程序集限定名时，到已加载的程序集里挨个找。
            // Revit API 的类型绝大多数都能这样找到
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetType(name, throwOnError: false, ignoreCase: false);
                    if (type != null) return type;
                }
                catch { /* 动态程序集可能查不了，跳过 */ }
            }

            throw new ToolFailureException(McpDomainError.InvalidParameter,
                "找不到类型 \"" + typeName + "\"。请用完整的命名空间，" +
                "如 Autodesk.Revit.DB.ElementTransformUtils。" +
                "不同 Revit 版本的类型有增删，当前是 Revit " + RevitVersionInfo.Year + "。");
        }

        /// <summary>把对外的实参描述转成真正的 .NET 值。</summary>
        public static object Convert(Document document, ApiArgument argument)
        {
            if (argument == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "arguments 里有 null 项。要传 null 实参，请用 { \"type\": \"null\" }。");

            var kind = (argument.Type ?? string.Empty).Trim().ToLowerInvariant();
            var value = argument.Value;

            switch (kind)
            {
                case "null": return null;
                case "string": return value;

                case "int":
                    return ParseNumber(value, s => int.Parse(s, CultureInfo.InvariantCulture), "int");

                case "long":
                    return ParseNumber(value, s => long.Parse(s, CultureInfo.InvariantCulture), "long");

                case "double":
                    return ParseNumber(value,
                        s => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture), "double");

                case "bool":
                    return ParseNumber(value, s =>
                        string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1", "bool");

                case "elementid":
                    string problem;
                    var target = ElementRef.Resolve(document, value, out problem);

                    if (target == null)
                        throw new ToolFailureException(McpDomainError.InvalidParameter,
                            "elementId 实参：" + problem);

                    return target.Id;

                case "xyz":
                    return ParseXyz(value);

                default:
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "无法识别的实参 type \"" + argument.Type +
                        "\"。可用值：string、int、long、double、bool、elementId、xyz、null。");
            }
        }

        private static object ParseNumber<T>(string value, Func<string, T> parse, string label)
        {
            try { return parse((value ?? string.Empty).Trim()); }
            catch
            {
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "实参 \"" + value + "\" 不是合法的 " + label + "。");
            }
        }

        private static XYZ ParseXyz(string value)
        {
            var parts = (value ?? string.Empty).Split(',');

            if (parts.Length != 3)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "xyz 实参要写成 \"x,y,z\"（毫米），收到 \"" + value + "\"。");

            try
            {
                return Units.Point(
                    double.Parse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
                    double.Parse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
                    double.Parse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture));
            }
            catch (ToolFailureException) { throw; }
            catch
            {
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "xyz 实参的三个分量必须都是数字，收到 \"" + value + "\"。");
            }
        }

        /// <summary>
        /// 在多个同名重载里挑一个。
        /// 挑不出唯一解时直接失败并把候选签名列出来——
        /// 随便挑一个"看起来能用"的重载，可能调到完全不同的行为上。
        /// </summary>
        public static MethodInfo PickOverload(
            IList<MethodInfo> candidates, object[] arguments, Type type, string member)
        {
            if (candidates.Count == 1) return candidates[0];

            var viable = candidates.Where(m => Accepts(m, arguments)).ToList();

            if (viable.Count == 1) return viable[0];

            var signatures = candidates.Select(Signature).Take(10);

            throw new ToolFailureException(McpDomainError.InvalidParameter,
                viable.Count == 0
                    ? "给的实参与 " + type.Name + "." + member + " 的任何一个重载都对不上。候选签名：\n" +
                      string.Join("\n", signatures.ToArray())
                    : "给的实参同时匹配 " + type.Name + "." + member + " 的多个重载，无法确定要调哪个。" +
                      "请把实参的 type 写得更精确（比如明确用 double 而不是 int）。候选签名：\n" +
                      string.Join("\n", signatures.ToArray()));
        }

        private static bool Accepts(MethodInfo method, object[] arguments)
        {
            var parameters = method.GetParameters();
            if (parameters.Length != arguments.Length) return false;

            for (var i = 0; i < parameters.Length; i++)
            {
                var expected = parameters[i].ParameterType;

                if (arguments[i] == null)
                {
                    if (expected.IsValueType && Nullable.GetUnderlyingType(expected) == null) return false;
                    continue;
                }

                if (!expected.IsInstanceOfType(arguments[i])) return false;
            }

            return true;
        }

        public static string Signature(MethodInfo method)
        {
            var parameters = method.GetParameters()
                .Select(p => p.ParameterType.Name + " " + p.Name);

            return (method.IsStatic ? "static " : string.Empty) +
                   method.ReturnType.Name + " " + method.DeclaringType.Name + "." + method.Name +
                   "(" + string.Join(", ", parameters.ToArray()) + ")";
        }

        /// <summary>
        /// 把返回值转成可读的描述，并尽量把里面的构件 ID 挖出来——
        /// 调用方拿到 ID 才能接着用别的工具去查，而不是对着一串 ToString() 干瞪眼。
        /// </summary>
        public static InvokeApiOutput Describe(object result, string resolved)
        {
            var output = new InvokeApiOutput { Resolved = resolved };

            if (result == null)
            {
                output.Result = "null";
                return output;
            }

            output.ResultType = result.GetType().FullName;

            var elementId = result as ElementId;
            if (elementId != null)
            {
                output.Result = elementId.ToProtocolString();
                output.ElementIds.Add(output.Result);
                return output;
            }

            var element = result as Element;
            if (element != null)
            {
                output.Result = AnnotationSupport.SafeName(element) ?? element.GetType().Name;
                output.ElementIds.Add(element.Id.ToProtocolString());
                return output;
            }

            var enumerable = result as System.Collections.IEnumerable;
            if (enumerable != null && !(result is string))
            {
                var items = new List<string>();
                var count = 0;

                foreach (var item in enumerable)
                {
                    count++;

                    if (items.Count < MaxItems) items.Add(DescribeItem(item, output.ElementIds));
                    else if (output.ElementIds.Count < MaxItems) DescribeItem(item, output.ElementIds);
                }

                output.Result = "共 " + count + " 项" +
                    (items.Count > 0 ? "：" + string.Join("、", items.ToArray()) : string.Empty) +
                    (count > items.Count ? "…（只列出前 " + items.Count + " 项）" : string.Empty);

                return output;
            }

            try { output.Result = result.ToString(); }
            catch (Exception ex) { output.Result = "(ToString 抛异常：" + ex.Message + ")"; }

            return output;
        }

        private static string DescribeItem(object item, List<string> elementIds)
        {
            if (item == null) return "null";

            var elementId = item as ElementId;
            if (elementId != null)
            {
                var text = elementId.ToProtocolString();
                if (!elementIds.Contains(text)) elementIds.Add(text);
                return text;
            }

            var element = item as Element;
            if (element != null)
            {
                var text = element.Id.ToProtocolString();
                if (!elementIds.Contains(text)) elementIds.Add(text);
                return (AnnotationSupport.SafeName(element) ?? element.GetType().Name) + "(" + text + ")";
            }

            try { return item.ToString(); }
            catch { return item.GetType().Name; }
        }
    }
}
