using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace RevitMCP.Addin.Tests
{
    /// <summary>
    /// 工具契约：对**每一个**工具、**每一个**参数做同一组断言。
    ///
    /// 这些性质单看任何一个工具都显然成立，问题出在"每一个"上。
    /// 68 个工具、900 多个参数，靠人肉扫一遍能扫出什么，实测过一次：
    /// 21 个判别式参数的合法取值只写在中文描述里、schema 上是个裸 string，
    /// 是手工审计才发现的。同一类疏漏再发生一次，这里应当当场拦住。
    ///
    /// 全部只读元数据，不加载 Revit——所以它能在没装 Revit 的 CI 上跑。
    /// </summary>
    public class ToolContractTests
    {
        public static IEnumerable<object[]> AllTools =>
            AddinMetadata.Tools.Select(t => new object[] { t.Name });

        private static ToolFacts Tool(string name) =>
            AddinMetadata.Tools.Single(t => t.Name == name);

        // ==================== 读得出来 ====================

        [Fact]
        public void MetadataIsReadableAndNotEmpty()
        {
            Assert.True(AddinMetadata.Tools.Count >= 60,
                "只读到 " + AddinMetadata.Tools.Count + " 个工具，明显少了");

            Assert.True(AddinMetadata.Params.Count >= 500,
                "只读到 " + AddinMetadata.Params.Count + " 个参数，明显少了");
        }

        // ==================== 工具名 ====================

        [Fact]
        public void ToolNamesAreUniqueAndWellFormed()
        {
            foreach (var tool in AddinMetadata.Tools)
            {
                Assert.StartsWith("revit_", tool.Name);

                // 小写下划线。混进大写或连字符，模型在不同上下文里会写出不同的变体
                Assert.Matches("^[a-z][a-z0-9_]*$", tool.Name);
            }

            var duplicates = AddinMetadata.Tools
                .GroupBy(t => t.Name, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToArray();

            Assert.True(duplicates.Length == 0, "工具名重复：" + string.Join("、", duplicates));
        }

        // ==================== 给模型看的文字 ====================

        [Theory]
        [MemberData(nameof(AllTools))]
        public void EveryToolHasTitleAndDescription(string name)
        {
            var tool = Tool(name);

            Assert.False(string.IsNullOrWhiteSpace(tool.Title), name + " 缺少 Title");
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), name + " 缺少 Description");

            // 描述是模型选工具的唯一依据。一句话说不清一个工具该在什么时候用
            Assert.True(tool.Description.Length >= 20,
                name + " 的 Description 只有 " + tool.Description.Length + " 字，太短了");
        }

        [Fact]
        public void EveryParameterHasADescription()
        {
            var missing = AddinMetadata.Params
                .Where(p => string.IsNullOrWhiteSpace(p.Description))
                .Select(p => p.ToString())
                .ToArray();

            Assert.True(missing.Length == 0,
                "这些参数没写描述：" + string.Join("、", missing));
        }

        // ==================== 判别式参数必须进 enum ====================

        /// <summary>
        /// 描述里列了固定取值（"可用值：a、b、c"）的字符串参数，
        /// 必须同时声明 <c>AllowedValues</c>，它会被生成为 schema 的 <c>enum</c>。
        ///
        /// 只写在散文里，等于要求调用方从一段自然语言里把枚举抠出来，
        /// 客户端也没法据此做约束解码——那一类错误本不该发生在运行期。
        ///
        /// 只管**输入侧**：输出 DTO 不生成 schema，在那儿标 AllowedValues 是死代码。
        /// </summary>
        [Fact]
        public void EnumeratedInputParametersDeclareTheirValues()
        {
            var offenders = AddinMetadata.Params
                .Where(IsInput)
                .Where(p => p.PropertyType == "String" || p.PropertyType == "List<String>")
                .Where(p => ListsFixedValues(p.Description))
                .Where(p => p.AllowedValues == null || p.AllowedValues.Length == 0)
                .Select(p => p.ToString())
                .ToArray();

            Assert.True(offenders.Length == 0,
                "这些输入参数在描述里列了固定取值，但没有声明 AllowedValues：" +
                string.Join("、", offenders) +
                "。加 [McpParam(..., AllowedValues = new[] { ... })]，它会生成为 schema 的 enum。");
        }

        [Fact]
        public void AllowedValuesAreNotDeclaredOnOutputs()
        {
            // 输出 DTO 不生成 schema，标了也不会变成任何约束。
            // 留着是死代码，更糟的是它看起来像在约束什么
            var dead = AddinMetadata.Params
                .Where(p => !IsInput(p))
                .Where(p => p.AllowedValues != null && p.AllowedValues.Length > 0)
                .Select(p => p.ToString())
                .ToArray();

            Assert.True(dead.Length == 0,
                "这些是输出字段，声明 AllowedValues 不会产生任何约束（死代码）：" +
                string.Join("、", dead));
        }

        [Fact]
        public void AllowedValuesAppearInTheirOwnDescription()
        {
            // enum 说有哪些值，描述说每个值什么意思——两者都要有，且不能对不上。
            // 描述里漏掉某个取值，模型就永远不会去用它
            var mismatched = new List<string>();

            foreach (var param in AddinMetadata.Params.Where(p => p.AllowedValues != null))
            {
                var absent = param.AllowedValues
                    .Where(v => param.Description == null ||
                                param.Description.IndexOf(v, StringComparison.OrdinalIgnoreCase) < 0)
                    .ToArray();

                if (absent.Length > 0)
                    mismatched.Add(param + " 的描述里没提到：" + string.Join("/", absent));
            }

            Assert.True(mismatched.Count == 0, string.Join("；", mismatched.ToArray()));
        }

        /// <summary>
        /// 描述里有没有在列举取值。
        ///
        /// 判据刻意保守——宁可漏判，不可误判：误判会让一个描述里恰好带某个词的参数
        /// 永远过不了测试，而漏判只是少拦一个。
        /// </summary>
        private static bool ListsFixedValues(string description)
        {
            if (string.IsNullOrEmpty(description)) return false;

            return description.Contains("可用值") || description.Contains("可用：");
        }

        /// <summary>
        /// 是不是输入侧的 DTO。
        ///
        /// 靠类名后缀判断，与 SchemaGenerator 实际会走到的类型一致：
        /// 只有工具的 Input 类型（及它嵌套引用的 Spec / Filter）会被生成 schema。
        /// </summary>
        private static bool IsInput(ParamFacts param)
        {
            var name = param.DeclaringType;

            return name.EndsWith("Input", StringComparison.Ordinal)
                || name.EndsWith("Spec", StringComparison.Ordinal)
                || name.EndsWith("Filter", StringComparison.Ordinal)
                || name.EndsWith("Argument", StringComparison.Ordinal)
                || name == "Point3D" || name == "LocationLine";
        }

        // ==================== 安全语义 ====================

        [Fact]
        public void ReadOnlyToolsAreNeitherDestructiveNorTransactionless()
        {
            var contradictory = AddinMetadata.Tools
                .Where(t => t.ReadOnly && t.WithoutTransaction)
                .Select(t => t.Name)
                .ToArray();

            // WithoutTransaction 是给"受写保护管辖但不能开事务"的工具用的（切换活动视图）。
            // 只读工具本来就不开事务，再标一次说明作者把这两件事搞混了
            Assert.True(contradictory.Length == 0,
                "这些工具既是只读、又标了 WithoutTransaction，两者不该同时出现：" +
                string.Join("、", contradictory));
        }

        [Fact]
        public void EscapeHatchToolsAreNotReadOnly()
        {
            // 逃生舱能在 Revit 进程里执行任意代码。任何时候它被标成只读，
            // 都意味着写保护对它失效了
            foreach (var name in new[] { "revit_invoke_api", "revit_execute_script" })
            {
                var tool = AddinMetadata.Tools.SingleOrDefault(t => t.Name == name);
                if (tool == null) continue;

                Assert.False(tool.ReadOnly, name + " 被标成了只读——它能执行任意代码，绝不能绕过写保护");
                Assert.True(tool.Destructive, name + " 应当声明为破坏性");
            }
        }

        [Fact]
        public void MutatingToolsThatSoundDestructiveSaySo()
        {
            // 名字里带 delete 的工具不声明破坏性，客户端就不会在执行前多问一句
            var understated = AddinMetadata.Tools
                .Where(t => !t.ReadOnly)
                .Where(t => t.Name.Contains("delete") || t.Name.Contains("sync"))
                .Where(t => !t.Destructive)
                .Select(t => t.Name)
                .ToArray();

            Assert.True(understated.Length == 0,
                "这些工具的名字暗示了不可逆的后果，却没声明 Destructive：" +
                string.Join("、", understated));
        }

        // ==================== 超时 ====================

        [Theory]
        [MemberData(nameof(AllTools))]
        public void TimeoutsAreSaneWhenDeclared(string name)
        {
            var timeout = Tool(name).TimeoutSeconds;

            // 0 表示用配置里的默认值，合法
            if (timeout == 0) return;

            Assert.True(timeout >= 5, name + " 的超时只有 " + timeout + " 秒，正常操作都跑不完");
            Assert.True(timeout <= 900, name + " 的超时是 " + timeout + " 秒，客户端早就放弃了");
        }

        // ==================== 工具之间的互相指路 ====================

        [Fact]
        public void CrossReferencedToolNamesAllExist()
        {
            // 描述里写了一个不存在的工具名，模型会照着去调，然后拿到 tool-not-found。
            // 这类错误在代码里完全看不出来，只能这样对一遍
            var known = new HashSet<string>(AddinMetadata.Tools.Select(t => t.Name), StringComparer.Ordinal);
            var dangling = new List<string>();

            foreach (var tool in AddinMetadata.Tools)
                foreach (var referenced in Mentioned(tool.Description))
                    if (!known.Contains(referenced))
                        dangling.Add(tool.Name + " → " + referenced);

            foreach (var param in AddinMetadata.Params)
                foreach (var referenced in Mentioned(param.Description))
                    if (!known.Contains(referenced))
                        dangling.Add(param + " → " + referenced);

            Assert.True(dangling.Count == 0,
                "这些描述指向了不存在的工具：" + string.Join("、", dangling.Distinct().ToArray()));
        }

        private static IEnumerable<string> Mentioned(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;

            foreach (Match match in Regex.Matches(text, @"\brevit_[a-z_]+\b"))
                yield return match.Value;
        }

        // ==================== 单位约定 ====================

        [Fact]
        public void LengthParametersSayWhichUnit()
        {
            // 全项目长度一律毫米。一个不写单位的长度参数，调用方只能猜，
            // 而猜错一个数量级在模型上是看得见的灾难
            var silent = AddinMetadata.Params
                .Where(IsInput)
                .Where(p => p.PropertyType == "Double")
                .Where(p => p.PropertyName.EndsWith("Mm", StringComparison.Ordinal) ||
                            LooksLikeLength(p.PropertyName))
                .Where(p => p.Description == null || p.Description.IndexOf("毫米", StringComparison.Ordinal) < 0)
                .Select(p => p.ToString())
                .ToArray();

            Assert.True(silent.Length == 0,
                "这些长度参数的描述里没写单位：" + string.Join("、", silent));
        }

        private static bool LooksLikeLength(string propertyName)
        {
            foreach (var hint in new[] { "Height", "Width", "Depth", "Offset", "Elevation", "Radius", "Thickness" })
                if (propertyName.IndexOf(hint, StringComparison.Ordinal) >= 0) return true;

            return false;
        }
    }
}
