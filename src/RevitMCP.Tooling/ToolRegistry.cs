using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using RevitMCP.Protocol.Mcp;
using RevitMCP.Tooling.Schema;

namespace RevitMCP.Tooling
{
    /// <summary>
    /// 工具执行时能拿到的一切。
    /// <typeparamref name="TContext"/> 在插件里是 UIApplication——
    /// 拿到它时已经在 Revit 主线程、且具备有效的 API context。
    /// </summary>
    public sealed class ToolExecutionContext<TContext>
    {
        public ToolExecutionContext(TContext host, bool writeEnabled, int maxElementsPerWrite,
            CancellationToken cancellationToken, IList<string> warnings = null,
            IProgressSink progress = null)
        {
            Host = host;
            WriteEnabled = writeEnabled;
            MaxElementsPerWrite = maxElementsPerWrite;
            CancellationToken = cancellationToken;
            Warnings = warnings ?? new List<string>();
            Progress = progress ?? NullProgressSink.Instance;
        }

        public TContext Host { get; }
        public bool WriteEnabled { get; }
        public int MaxElementsPerWrite { get; }
        public CancellationToken CancellationToken { get; }

        /// <summary>
        /// 执行期间被抑制的 Revit 警告，以及工具自己想让模型看见的提示。
        /// 管线会把非空的它作为 warnings 字段并入工具输出——
        /// 工具作者往里 Add 即可，不必在自己的 Output DTO 里另开一个字段。
        /// </summary>
        public IList<string> Warnings { get; }

        /// <summary>
        /// 进度上报口。永远不为 null——客户端没要进度时是空实现，
        /// 工具照常调用即可，不必到处判空。
        /// 长循环里顺手报一下，客户端才不会把一个正常的慢操作当成卡死。
        /// </summary>
        public IProgressSink Progress { get; }
    }

    /// <summary>注册表看到的工具形态（已擦除泛型）。工具作者不直接实现它。</summary>
    public interface IToolBinding<TContext>
    {
        Type InputType { get; }
        object Invoke(object input, ToolExecutionContext<TContext> context);
    }

    /// <summary>
    /// 工具基类。作者只写业务逻辑和两个 DTO——
    /// 线程编组、事务、参数绑定、Schema、序列化、超时全由管线统一处理。
    /// </summary>
    public abstract class McpTool<TContext, TInput, TOutput> : IToolBinding<TContext>
        where TInput : class, new()
    {
        public abstract TOutput Execute(TInput input, ToolExecutionContext<TContext> context);

        Type IToolBinding<TContext>.InputType => typeof(TInput);

        object IToolBinding<TContext>.Invoke(object input, ToolExecutionContext<TContext> context) =>
            Execute((TInput)input, context);
    }

    /// <summary>一个已注册的工具：元数据 + 协议定义 + 绑定。</summary>
    public sealed class RegisteredTool<TContext>
    {
        internal RegisteredTool(McpToolAttribute metadata, ToolDefinition definition, IToolBinding<TContext> binding)
        {
            Metadata = metadata;
            Definition = definition;
            Binding = binding;
        }

        public McpToolAttribute Metadata { get; }
        public ToolDefinition Definition { get; }
        public IToolBinding<TContext> Binding { get; }

        public string Name => Metadata.Name;
        public bool IsReadOnly => Metadata.ReadOnly;
    }

    /// <summary>
    /// 扫描 [McpTool] 标注的类型并建表。
    /// 不做外部程序集的动态加载——Revit 单 AppDomain 下热加载弊远大于利。
    /// </summary>
    public sealed class ToolRegistry<TContext>
    {
        private readonly Dictionary<string, RegisteredTool<TContext>> _tools =
            new Dictionary<string, RegisteredTool<TContext>>(StringComparer.Ordinal);
        private readonly List<RegisteredTool<TContext>> _ordered = new List<RegisteredTool<TContext>>();

        /// <summary>规范要求 tools/list 顺序稳定，以便客户端缓存。</summary>
        public IReadOnlyList<RegisteredTool<TContext>> Tools => _ordered;

        public IReadOnlyList<ToolDefinition> Definitions =>
            _ordered.Select(t => t.Definition).ToArray();

        public bool TryGet(string name, out RegisteredTool<TContext> tool) =>
            _tools.TryGetValue(name, out tool);

        /// <summary>
        /// 扫描程序集中所有带 [McpTool] 的类型。
        /// <paramref name="disabledTools"/> 里的名字会被跳过（来自 config.json）。
        /// </summary>
        public int RegisterAssembly(Assembly assembly, IEnumerable<string> disabledTools = null)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));

            var disabled = new HashSet<string>(disabledTools ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var count = 0;

            // 按类型名排序，保证注册顺序与反射返回顺序无关
            var candidates = assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract)
                .Where(t => t.GetCustomAttribute<McpToolAttribute>() != null)
                .OrderBy(t => t.FullName, StringComparer.Ordinal);

            foreach (var type in candidates)
            {
                var metadata = type.GetCustomAttribute<McpToolAttribute>();
                if (disabled.Contains(metadata.Name)) continue;

                Register(type, metadata);
                count++;
            }

            return count;
        }

        public void Register(Type type, McpToolAttribute metadata = null)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            metadata = metadata ?? type.GetCustomAttribute<McpToolAttribute>();
            if (metadata == null)
                throw new InvalidOperationException(type.FullName + " 缺少 [McpTool] 标注。");

            var binding = Instantiate(type);

            var definition = new ToolDefinition(
                metadata.Name,
                metadata.Title,
                metadata.Description,
                SchemaGenerator.Generate(binding.InputType),
                // 破坏性默认跟着"非只读"走（规范的默认值也是如此）。
                // 个别改了模型却谈不上破坏的工具可以显式声明 Destructive = false
                new ToolAnnotations(
                    readOnlyHint: metadata.ReadOnly,
                    destructiveHint: !metadata.ReadOnly && metadata.Destructive));

            if (_tools.ContainsKey(metadata.Name))
                throw new InvalidOperationException("工具名重复：" + metadata.Name);

            var registered = new RegisteredTool<TContext>(metadata, definition, binding);
            _tools.Add(metadata.Name, registered);
            _ordered.Add(registered);
        }

        private static IToolBinding<TContext> Instantiate(Type type)
        {
            if (!typeof(IToolBinding<TContext>).IsAssignableFrom(type))
                throw new InvalidOperationException(
                    type.FullName + " 必须继承 McpTool<,,>（上下文类型为 " + typeof(TContext).Name + "）。");

            try
            {
                return (IToolBinding<TContext>)Activator.CreateInstance(type);
            }
            catch (MissingMethodException)
            {
                // 工具刻意设计成无状态：需要的一切都从 ToolExecutionContext 拿，
                // 这样注册就不需要依赖注入容器
                throw new InvalidOperationException(
                    type.FullName + " 需要公共无参构造函数。工具应保持无状态，依赖通过 ToolExecutionContext 获取。");
            }
        }
    }
}
