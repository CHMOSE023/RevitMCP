using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using RevitMCP.Protocol.Mcp;

namespace RevitMCP.Addin.Guide
{
    /// <summary>
    /// 建模指引：随服务发布的那份"怎么用这套工具把模型建对"。
    ///
    /// **为什么要随服务发布，而不是让用户往客户端里装一份文件。**
    /// 装漏了没人发现——Agent 照样能调工具，只是会按自己的习惯乱来；
    /// 装旧了更糟——它会对着**已经修好的缺陷**执行补救动作
    /// （比如建完楼板还去把标高偏移改回 0），而一切看起来都在正常工作。
    /// 指引跟着 DLL 走，就永远和工具的实际行为是同一个版本。
    ///
    /// 内容源文件在仓库的 <c>skills/revit-modeling/</c> 下，编译时嵌进程序集。
    /// 改内容改那边，这里不留副本——两份会长歪。
    /// </summary>
    internal static class ModelingGuide
    {
        public const string UriPrefix = "revitmcp://guide/";

        /// <summary>入口那一份的名字。省略 section 时给的就是它。</summary>
        public const string EntryName = "overview";

        private static readonly object Gate = new object();
        private static IReadOnlyList<GuideSection> _sections;

        public static IReadOnlyList<GuideSection> Sections
        {
            get
            {
                lock (Gate)
                {
                    return _sections ?? (_sections = Load());
                }
            }
        }

        public static GuideSection Entry =>
            Sections.FirstOrDefault(s => s.Name == EntryName) ?? Sections.FirstOrDefault();

        public static GuideSection Find(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return Entry;

            var wanted = name.Trim();

            return Sections.FirstOrDefault(s =>
                       string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase)) ??
                   Sections.FirstOrDefault(s =>
                       string.Equals(s.Uri, wanted, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 每一节是什么、什么时候该读。这段文字决定了它会不会被读，
        /// 所以写的是"什么时候需要它"，而不是"它的内容是什么"。
        /// </summary>
        private static readonly Dictionary<string, string[]> Catalog =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["overview"] = new[]
                {
                    "建模总则",
                    "开工前先读这一份：目标文档核验、八条硬规则、建模阶段表。" +
                    "**开始任何 Revit 建模任务前都该看一眼**——" +
                    "这套工具的失败模式不是调用报错，而是调用成功、模型不对。"
                },
                ["sequence"] = new[]
                {
                    "建模顺序与批次",
                    "制订建模计划、或任务类型变了（新建 / 改造 / 只出图）时读：" +
                    "构件依赖图、任务分支、一次建多少个合适。"
                },
                ["validation"] = new[]
                {
                    "验收清单",
                    "**每建完一类构件读对应那一节**：墙量什么、板量什么、房间量什么。" +
                    "回执说成功不算数，量回来对得上才算。"
                },
                ["recovery"] = new[]
                {
                    "失败恢复",
                    "调用失败或超时后读：先判断「到底执行了没有」，再按错误码处置。" +
                    "超时后直接重建会在模型里留下两套一模一样的构件。"
                },
                ["limitations"] = new[]
                {
                    "版本与已知限制",
                    "**写任何补救/绕行代码之前读**：哪些坑已经修了（别再绕）、" +
                    "哪些限制还在（原生楼梯、天花的版本要求等）。"
                }
            };

        /// <summary>嵌入的资源名 → 对外的 section 名。</summary>
        private static readonly Dictionary<string, string> FileNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SKILL.md"] = "overview",
                ["modeling-sequence.md"] = "sequence",
                ["validation-checklist.md"] = "validation",
                ["recovery-guide.md"] = "recovery",
                ["version-limitations.md"] = "limitations"
            };

        /// <summary>
        /// 从程序集里把指引读出来。
        ///
        /// **整段包在 try 里，任何情况下都要返回一个列表，绝不抛。**
        /// 这份东西是锦上添花：读不出来最多是"没有指引可看"，
        /// 不该让服务的任何一条请求因此失败——而 <c>ListResources</c> 的调用点
        /// 恰好在传输层的 try/catch 之外。
        ///
        /// 失败也要缓存（返回空列表并被 <see cref="Sections"/> 记住），
        /// 否则每个请求都会重试一遍反射。
        /// </summary>
        private static IReadOnlyList<GuideSection> Load()
        {
            try { return LoadCore(); }
            catch { return new GuideSection[0]; }
        }

        private static IReadOnlyList<GuideSection> LoadCore()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var sections = new List<GuideSection>();

            foreach (var resourceName in assembly.GetManifestResourceNames())
            {
                var file = FileNames.Keys.FirstOrDefault(
                    f => resourceName.EndsWith(f, StringComparison.OrdinalIgnoreCase));

                if (file == null) continue;

                var name = FileNames[file];
                var text = ReadText(assembly, resourceName);
                if (text == null) continue;

                string[] meta;
                if (!Catalog.TryGetValue(name, out meta)) meta = new[] { name, null };

                sections.Add(new GuideSection(name, meta[0], meta[1], Strip(text)));
            }

            // 顺序固定：入口在前，其余按目录里的顺序。反射返回的顺序不保证稳定
            var order = Catalog.Keys.ToList();
            return sections
                .OrderBy(s => order.IndexOf(s.Name) < 0 ? int.MaxValue : order.IndexOf(s.Name))
                .ToList();
        }

        private static string ReadText(Assembly assembly, string resourceName)
        {
            try
            {
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null) return null;

                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        return reader.ReadToEnd();
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// 去掉 SKILL.md 的 YAML frontmatter。
        /// 那几行是给"把它当文件装进客户端"那条路用的元数据，
        /// 从这条路读到的人不需要看见它。
        /// </summary>
        private static string Strip(string text)
        {
            if (text == null) return string.Empty;

            var trimmed = text.TrimStart('﻿', ' ', '\r', '\n');
            if (!trimmed.StartsWith("---", StringComparison.Ordinal)) return trimmed;

            var end = trimmed.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end < 0) return trimmed;

            var after = trimmed.IndexOf('\n', end + 1);
            return after < 0 ? trimmed : trimmed.Substring(after + 1).TrimStart('\r', '\n');
        }
    }

    internal sealed class GuideSection
    {
        public GuideSection(string name, string title, string description, string text)
        {
            Name = name;
            Title = title;
            Description = description;
            Text = text;
        }

        public string Name { get; }
        public string Title { get; }
        public string Description { get; }
        public string Text { get; }

        public string Uri => ModelingGuide.UriPrefix + Name;

        public ResourceDefinition ToResource() =>
            new ResourceDefinition(Uri, "建模指引 · " + Title, Description, "text/markdown", Title);
    }

    /// <summary>
    /// 把建模指引发布成 MCP 资源。
    ///
    /// 清单算一次就存下来：<c>ListResources</c> 会被每个请求调到
    /// （<c>IsKnownMethod</c> 与 <c>Capabilities</c> 各一次），
    /// 每次都重新 Select + ToList 是白花的开销，也多一次出错的机会。
    /// </summary>
    internal sealed class GuideResourceCatalog : IResourceCatalog
    {
        private readonly Lazy<IReadOnlyList<ResourceDefinition>> _definitions =
            new Lazy<IReadOnlyList<ResourceDefinition>>(
                () => ModelingGuide.Sections.Select(s => s.ToResource()).ToList(),
                isThreadSafe: true);

        public IReadOnlyList<ResourceDefinition> ListResources() => _definitions.Value;

        public ResourceContents ReadResource(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return null;

            var section = ModelingGuide.Sections.FirstOrDefault(
                s => string.Equals(s.Uri, uri.Trim(), StringComparison.OrdinalIgnoreCase));

            return section == null ? null : new ResourceContents(section.Uri, section.Text);
        }
    }
}
