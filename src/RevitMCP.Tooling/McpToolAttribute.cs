using System;

namespace RevitMCP.Tooling
{
    /// <summary>
    /// 标记一个 MCP 工具。OnStartup 时反射扫描本程序集完成注册（M3 实现）。
    ///
    /// 工具作者只需写"业务逻辑 + Input/Output DTO"，
    /// 线程编组、事务、序列化、超时一律由执行管线统一处理。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class McpToolAttribute : Attribute
    {
        public McpToolAttribute(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("工具名不能为空。", nameof(name));
            Name = name;
        }

        /// <summary>协议中的工具名，如 revit_query_elements。建议小写下划线风格。</summary>
        public string Name { get; }

        /// <summary>给人看的标题。</summary>
        public string Title { get; set; }

        /// <summary>给模型看的说明。写清楚"什么时候该用它"，比罗列参数更有价值。</summary>
        public string Description { get; set; }

        /// <summary>只读工具在写保护开启时仍可调用；非只读工具会被拒绝并返回 WRITE_DISABLED。</summary>
        public bool ReadOnly { get; set; }

        /// <summary>
        /// 非只读工具默认跑在事务里。个别工具改的不是模型、而是 Revit 的界面状态
        /// （切换活动视图就是），它们要受写保护管辖，却**不能**开事务——
        /// Revit 不允许在事务打开的状态下切换活动视图。
        ///
        /// 这个开关把"要不要写保护"和"要不要事务"拆开。在它出现之前，
        /// <see cref="ReadOnly"/> 一个标志承担了两件不同的事，
        /// 于是"受管辖但无事务"这一类工具根本没法表达。
        /// </summary>
        public bool WithoutTransaction { get; set; }

        /// <summary>
        /// 是否可能做出不易挽回的改动。只对非只读工具有意义，默认 true。
        /// 纯粹新增东西的工具（创建图纸、创建房间）可以置 false——
        /// 它们改了模型，但撤销一步就没了，和删除不是一个量级。
        /// </summary>
        public bool Destructive { get; set; } = true;

        /// <summary>单次调用超时。0 表示使用配置中的默认值。</summary>
        public int TimeoutSeconds { get; set; }

        /// <summary>
        /// 这个工具属于哪些工具集。一个工具可以属于多个——
        /// 本项目的工具是按**几何范式**组织的（线定位 / 点定位 / 面定位），
        /// 而专业的边界横切在它们内部：同一个 `create_line_based_elements`
        /// 既造墙（建筑）也造梁（结构），切不开，只能同时挂在两个集合下。
        ///
        /// 省略即 <see cref="Toolsets.Core"/>：常驻、不可关闭。
        /// 取值见 <see cref="Toolsets"/>。
        /// </summary>
        public string[] Toolsets { get; set; }
    }

    /// <summary>
    /// 工具集的名字。**以功能为主轴、专业为副轴**——实测（见 docs/design-toolsets.md）：
    /// 纯按建筑/结构/MEP 切，能摘掉的只有 MEP 与协同两块，上限约 10%；
    /// 而按功能切，"只出图"的会话能省 35%、"只建模"的能省 27%。
    /// </summary>
    public static class Toolsets
    {
        /// <summary>常驻，不可关闭：文档与查询、标高轴网、类型与族、参数、几何、警告、保存、删除、指引、操作状态。</summary>
        public const string Core = "core";

        public const string ModelingArchitecture = "modeling.architecture";
        public const string ModelingStructure = "modeling.structure";
        public const string ModelingMep = "modeling.mep";

        /// <summary>视图、图纸、注释标记、明细表、取景、导出。</summary>
        public const string Documentation = "documentation";

        /// <summary>链接、工作集、阶段、设计选项、碰撞、同步、修订。</summary>
        public const string Coordination = "coordination";

        /// <summary>材质、类型编辑、项目参数、编组、变换。</summary>
        public const string Authoring = "authoring";

        /// <summary>两个逃生舱。它们另有 escapeHatchEnabled 这道独立开关。</summary>
        public const string Escape = "escape";

        public static readonly string[] All =
        {
            Core, ModelingArchitecture, ModelingStructure, ModelingMep,
            Documentation, Coordination, Authoring, Escape
        };
    }

    /// <summary>描述工具 Input DTO 的一个属性，用于生成 JSON Schema。</summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class McpParamAttribute : Attribute
    {
        public McpParamAttribute(string description)
        {
            Description = description;
        }

        public string Description { get; }

        /// <summary>是否必填。值类型默认必填，可空类型与引用类型默认可选。</summary>
        public bool Required { get; set; }

        /// <summary>
        /// 这个参数只接受这几个值，会原样生成为 JSON Schema 的 <c>enum</c>。
        ///
        /// 判别式参数（operation / kind / viewType 这类）必须写它。
        /// 把取值只写在 <see cref="Description"/> 的散文里，等于要求调用方
        /// 从一段自然语言中把枚举抠出来——而 schema 里的 <c>enum</c>
        /// 能让客户端直接做约束，这一类错误本就不该发生在运行期。
        ///
        /// 顺序即推荐顺序：默认值放第一个。
        /// </summary>
        public string[] AllowedValues { get; set; }
    }
}
