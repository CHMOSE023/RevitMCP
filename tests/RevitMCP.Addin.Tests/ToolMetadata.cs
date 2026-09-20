using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace RevitMCP.Addin.Tests
{
    /// <summary>一个工具的元数据，从 <c>[McpTool]</c> 上读出来。</summary>
    public sealed class ToolFacts
    {
        public string Name;
        public string Title;
        public string Description;
        public bool ReadOnly;
        public bool Destructive = true;      // 特性的默认值
        public bool WithoutTransaction;
        public int TimeoutSeconds;

        /// <summary>入参 DTO 的类型名，用于把参数归到工具名下。</summary>
        public string InputTypeName;

        public override string ToString() => Name;
    }

    /// <summary>一个参数，从 <c>[McpParam]</c> 上读出来。</summary>
    public sealed class ParamFacts
    {
        public string DeclaringType;
        public string PropertyName;
        public string PropertyType;
        public string Description;
        public bool Required;
        public string[] AllowedValues;

        public override string ToString() => DeclaringType + "." + PropertyName;
    }

    /// <summary>
    /// 把 RevitMCP.Addin.dll 当数据读。
    ///
    /// 用 <see cref="MetadataLoadContext"/> 而不是 <c>Assembly.Load</c>：
    /// 后者会去解析 Revit API 的执行期依赖链，在没装 Revit 的机器上必然失败。
    /// </summary>
    public static class AddinMetadata
    {
        private static readonly Lazy<Snapshot> Loaded = new Lazy<Snapshot>(Read);

        public static IReadOnlyList<ToolFacts> Tools => Loaded.Value.Tools;
        public static IReadOnlyList<ParamFacts> Params => Loaded.Value.Params;

        private sealed class Snapshot
        {
            public List<ToolFacts> Tools = new List<ToolFacts>();
            public List<ParamFacts> Params = new List<ParamFacts>();
        }

        /// <summary>
        /// 测试程序集真正的输出目录。
        ///
        /// **不能用 <c>Assembly.Location</c>**：xunit 默认开影子副本，
        /// 那个属性会指向 %TEMP% 下的一个临时目录，而 Addin.dll 并不在那里。
        /// <c>CodeBase</c> 始终指向原始位置。
        /// </summary>
        private static string OutputDirectory()
        {
            var assembly = typeof(AddinMetadata).Assembly;

            try
            {
                var codeBase = assembly.CodeBase;
                if (!string.IsNullOrEmpty(codeBase))
                    return Path.GetDirectoryName(new Uri(codeBase).LocalPath);
            }
            catch { /* 落到下面 */ }

            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private static Snapshot Read()
        {
            var directory = OutputDirectory();
            var addin = Path.Combine(directory, "RevitMCP.Addin.dll");

            if (!File.Exists(addin))
                throw new FileNotFoundException(
                    "找不到 " + addin + "。这个测试项目通过 ProjectReference 把 Addin 拷到输出目录，" +
                    "拷不过来说明构建顺序出了问题。");

            // 解析器只需要覆盖"读特性时会碰到的程序集"：Addin 自己、Tooling（特性定义在那里）、
            // 以及 BCL。Revit 的程序集一个都不在这里，也不需要——
            // 我们从不触碰任何以 Revit 类型为参数的成员
            var assemblies = Directory.GetFiles(directory, "*.dll").ToList();
            assemblies.AddRange(Directory.GetFiles(
                Path.GetDirectoryName(typeof(object).Assembly.Location), "*.dll"));

            var snapshot = new Snapshot();

            using (var context = new MetadataLoadContext(new PathAssemblyResolver(assemblies)))
            {
                var assembly = context.LoadFromAssemblyPath(addin);

                foreach (var type in assembly.GetTypes())
                {
                    ReadTool(type, snapshot);
                    ReadParams(type, snapshot);
                }
            }

            if (snapshot.Tools.Count == 0)
                throw new InvalidOperationException(
                    "从 RevitMCP.Addin.dll 里一个 [McpTool] 都没读到。" +
                    "要么特性改名了，要么元数据读取的方式失效了——这两种都不该悄悄放过。");

            return snapshot;
        }

        private static void ReadTool(Type type, Snapshot snapshot)
        {
            var attribute = type.GetCustomAttributesData()
                .FirstOrDefault(a => a.AttributeType.Name == "McpToolAttribute");

            if (attribute == null) return;

            var facts = new ToolFacts
            {
                Name = (string)attribute.ConstructorArguments[0].Value,
                InputTypeName = InputTypeNameOf(type)
            };

            foreach (var named in attribute.NamedArguments)
            {
                switch (named.MemberName)
                {
                    case "Title": facts.Title = (string)named.TypedValue.Value; break;
                    case "Description": facts.Description = (string)named.TypedValue.Value; break;
                    case "ReadOnly": facts.ReadOnly = (bool)named.TypedValue.Value; break;
                    case "Destructive": facts.Destructive = (bool)named.TypedValue.Value; break;
                    case "WithoutTransaction": facts.WithoutTransaction = (bool)named.TypedValue.Value; break;
                    case "TimeoutSeconds": facts.TimeoutSeconds = (int)named.TypedValue.Value; break;
                }
            }

            snapshot.Tools.Add(facts);
        }

        /// <summary>
        /// 工具类的入参 DTO 类型名。
        ///
        /// 只往上走一层（<c>RevitTool&lt;TInput, TOutput&gt;</c>），不再继续——
        /// 再往上是 <c>McpTool&lt;UIApplication, …&gt;</c>，一碰就要解析 RevitAPIUI。
        /// </summary>
        private static string InputTypeNameOf(Type type)
        {
            try
            {
                var baseType = type.BaseType;
                if (baseType == null || !baseType.IsGenericType) return null;

                var arguments = baseType.GetGenericArguments();
                return arguments.Length >= 1 ? arguments[0].Name : null;
            }
            catch
            {
                // 解析不了就算了：参数断言按"声明类型"归类，不依赖这个
                return null;
            }
        }

        private static void ReadParams(Type type, Snapshot snapshot)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var attribute = property.GetCustomAttributesData()
                    .FirstOrDefault(a => a.AttributeType.Name == "McpParamAttribute");

                if (attribute == null) continue;

                var facts = new ParamFacts
                {
                    DeclaringType = type.Name,
                    PropertyName = property.Name,
                    PropertyType = TypeNameOf(property.PropertyType),
                    Description = attribute.ConstructorArguments.Count > 0
                        ? (string)attribute.ConstructorArguments[0].Value
                        : null
                };

                foreach (var named in attribute.NamedArguments)
                {
                    if (named.MemberName == "Required")
                        facts.Required = (bool)named.TypedValue.Value;

                    if (named.MemberName == "AllowedValues")
                        facts.AllowedValues = ReadStringArray(named.TypedValue);
                }

                snapshot.Params.Add(facts);
            }
        }

        private static string[] ReadStringArray(CustomAttributeTypedArgument argument)
        {
            var items = argument.Value as IReadOnlyCollection<CustomAttributeTypedArgument>;
            if (items == null) return null;

            return items.Select(i => (string)i.Value).ToArray();
        }

        /// <summary>
        /// 属性类型的可读名字。<c>List&lt;string&gt;</c> 归一成 "List&lt;string&gt;"，
        /// 这样断言里判断"是不是字符串参数"时不用分别写两遍。
        /// </summary>
        private static string TypeNameOf(Type type)
        {
            try
            {
                if (type.IsGenericType)
                {
                    var arguments = type.GetGenericArguments();
                    var name = type.Name.Split('`')[0];

                    // Nullable<bool> 这类按底层类型算
                    if (name == "Nullable" && arguments.Length == 1) return TypeNameOf(arguments[0]);

                    return name + "<" + string.Join(", ", arguments.Select(TypeNameOf).ToArray()) + ">";
                }

                return type.Name;
            }
            catch { return "(未知)"; }
        }
    }
}
