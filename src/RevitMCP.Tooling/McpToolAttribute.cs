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

        /// <summary>单次调用超时。0 表示使用配置中的默认值。</summary>
        public int TimeoutSeconds { get; set; }
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
    }
}
