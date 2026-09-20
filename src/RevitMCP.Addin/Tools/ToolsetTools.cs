using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    public sealed class ListToolsetsInput
    {
    }

    public sealed class ToolsetInfo
    {
        [McpParam("工具集名")]
        public string Name { get; set; }

        [McpParam("这个集合管什么")]
        public string Description { get; set; }

        [McpParam("当前是否启用。未启用的工具**不在 tools/list 里，也调不动**")]
        public bool Enabled { get; set; }

        [McpParam("这个集合里有几个工具（按当前启用状态统计到的）")]
        public int ToolCount { get; set; }

        [McpParam("具体有哪些工具")]
        public List<string> Tools { get; set; } = new List<string>();
    }

    public sealed class ListToolsetsOutput
    {
        [McpParam("当前 tools/list 里有多少个工具")]
        public int EnabledTools { get; set; }

        [McpParam("工具集清单")]
        public List<ToolsetInfo> Toolsets { get; set; } = new List<ToolsetInfo>();

        [McpParam("没启用的集合怎么打开——本服务不支持运行时开关，必须改配置并重启 Revit")]
        public string HowToEnable { get; set; }
    }

    /// <summary>
    /// 列出工具集。
    ///
    /// 存在的理由不是"多一个清单"，而是**让被关掉的能力可见**：
    /// 部署者按需只开了建筑 + 出图，调用方看到的 tools/list 里就没有 MEP，
    /// 它会以为这套服务压根不会建管线，于是去找替代方案、或者直接告诉用户做不了。
    /// 有这个工具，它至少知道"能力在那儿，只是没开"。
    /// </summary>
    [McpTool("revit_list_toolsets",
        Title = "列出工具集",
        Description = "列出本服务的工具集分组、各自的工具数与启用状态。" +
                      "**tools/list 里没有你要的工具时先查它**——那个能力可能只是没启用，" +
                      "而不是不存在。未启用的集合需要改配置并重启 Revit 才能打开。",
        ReadOnly = true,
        TimeoutSeconds = 15)]
    public sealed class ListToolsetsTool : RevitTool<ListToolsetsInput, ListToolsetsOutput>
    {
        /// <summary>由 ServerHost 注入：注册表（知道当前实际注册了哪些）与配置里启用的集合。</summary>
        internal static Func<IReadOnlyList<ToolsetMembership>> Source { get; set; }

        private static readonly Dictionary<string, string> Descriptions =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Toolsets.Core] = "文档与查询、标高轴网、类型与族、参数、几何、警告、保存、删除、建模指引、操作状态。常驻，关不掉",
                [Toolsets.ModelingArchitecture] = "墙、门窗、楼板屋顶、房间、载入族",
                [Toolsets.ModelingStructure] = "梁、柱、基础。与建筑共用那几个几何工具，所以它们同时属于两个集合",
                [Toolsets.ModelingMep] = "风管、水管、线管、桥架与 MEP 系统",
                [Toolsets.Documentation] = "视图、图纸、注释与标记、明细表、取景、导出",
                [Toolsets.Coordination] = "链接、工作集、阶段、设计选项、碰撞检查、同步、修订",
                [Toolsets.Authoring] = "材质、类型编辑、项目参数、编组、变换、选择",
                [Toolsets.Escape] = "两个逃生舱。另有 escapeHatchEnabled 这道独立开关"
            };

        public override ListToolsetsOutput Execute(
            ListToolsetsInput input, ToolExecutionContext<UIApplication> context)
        {
            var memberships = Source == null
                ? new List<ToolsetMembership>()
                : Source().ToList();

            var output = new ListToolsetsOutput
            {
                EnabledTools = memberships.Count(m => m.Registered),
                HowToEnable =
                    "改 %APPDATA%\\RevitMCP\\config.json 里的 enabledToolsets（留空数组 = 全开），" +
                    "然后重启 Revit。本服务是无会话的，没有运行时开关——" +
                    "那会变成一个影响所有连接方的全局状态。"
            };

            foreach (var name in Tooling.Toolsets.All)
            {
                var inSet = memberships.Where(m => m.Toolsets.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
                if (inSet.Count == 0) continue;

                string description;
                Descriptions.TryGetValue(name, out description);

                output.Toolsets.Add(new ToolsetInfo
                {
                    Name = name,
                    Description = description,
                    Enabled = inSet.Any(m => m.Registered),
                    ToolCount = inSet.Count,
                    Tools = inSet.Select(m => m.ToolName).OrderBy(n => n, StringComparer.Ordinal).ToList()
                });
            }

            return output;
        }
    }

    /// <summary>一个工具属于哪些集合、当前有没有真的注册。</summary>
    public sealed class ToolsetMembership
    {
        public ToolsetMembership(string toolName, IReadOnlyList<string> toolsets, bool registered)
        {
            ToolName = toolName;
            Toolsets = toolsets;
            Registered = registered;
        }

        public string ToolName { get; }
        public IReadOnlyList<string> Toolsets { get; }
        public bool Registered { get; }
    }
}
