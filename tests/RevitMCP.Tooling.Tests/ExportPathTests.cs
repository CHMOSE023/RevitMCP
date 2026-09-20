using System;
using System.IO;
using RevitMCP.Tooling;
using Xunit;

namespace RevitMCP.Tooling.Tests
{
    /// <summary>
    /// 导出路径的边界。
    ///
    /// 这是整个服务里唯一会在模型之外留下痕迹的一类操作，而**文件名很可能来自
    /// 模型刚读过的某个构件名、某段用户输入**——不是"模型会使坏"，是它会被喂坏数据。
    /// 写坏用户的文件不可逆，所以这里的每条拒绝都要有测试盯着。
    /// </summary>
    public class ExportPathTests : IDisposable
    {
        private readonly string _root;

        public ExportPathTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "revitmcp-export-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch { }
        }

        private string Resolve(string fileName) => ExportPaths.Resolve(_root, fileName, ".png");

        private static void Rejects(Func<string> act, params string[] mustMention)
        {
            var ex = Assert.Throws<ToolFailureException>(() => act());
            Assert.Equal(McpDomainError.InvalidParameter, ex.Code);
            foreach (var fragment in mustMention) Assert.Contains(fragment, ex.Message);
        }

        [Fact]
        public void PlainFileNameLandsInsideTheRoot()
        {
            var path = Resolve("一层平面.png");

            Assert.Equal(Path.Combine(_root, "一层平面.png"), path);
            Assert.True(Directory.Exists(_root), "解析时应当把导出目录建好");
        }

        [Fact]
        public void MissingExtensionGetsTheDefault()
        {
            Assert.EndsWith(".png", Resolve("平面"));
        }

        [Fact]
        public void ExtensionMatchingIsCaseInsensitive()
        {
            Assert.EndsWith(".PNG", Resolve("平面.PNG"));
        }

        [Theory]
        [InlineData("sub/plan.png")]
        [InlineData("sub\\plan.png")]
        public void PathSeparatorsAreRejected(string name)
        {
            // 允许子目录就等于允许一层层往上爬，不如从源头只收文件名
            Rejects(() => Resolve(name), "只能是文件名");
        }

        [Theory]
        [InlineData("../escape.png")]
        [InlineData("..\\escape.png")]
        [InlineData("a/../../../windows/system32/evil.png")]
        public void ParentDirectoryTraversalIsRejected(string name)
        {
            Rejects(() => Resolve(name));
        }

        [Theory]
        [InlineData("C:\\windows\\evil.png")]
        [InlineData("\\\\server\\share\\evil.png")]
        public void AbsolutePathsAreRejected(string name)
        {
            Rejects(() => Resolve(name));
        }

        [Fact]
        public void InvalidFileNameCharactersAreRejected()
        {
            Rejects(() => Resolve("pl<an>.png"), "不允许的字符");
        }

        [Theory]
        [InlineData("payload.exe")]
        [InlineData("script.ps1")]
        [InlineData("config.json")]
        public void OnlyImageExtensionsAreAllowed(string name)
        {
            // 白名单而非黑名单：能把任意扩展名写进磁盘的工具不该存在
            Rejects(() => Resolve(name), "不支持的文件扩展名");
        }

        // ---------- 每类导出各自的白名单 ----------

        private string ResolveAs(string fileName, string defaultExtension, string[] allowed) =>
            ExportPaths.Resolve(_root, fileName, defaultExtension, allowed);

        [Theory]
        [InlineData("model.dwg")]
        [InlineData("model.ifc")]
        [InlineData("图纸集.pdf")]
        public void DocumentExportAcceptsDeliveryFormats(string name)
        {
            var path = ResolveAs(name, ".dwg", ExportPaths.DocumentExtensions);

            Assert.Equal(Path.Combine(_root, name), path);
        }

        [Fact]
        public void DocumentExportStillRejectsImages()
        {
            // 白名单跟着导出工具走，而不是全局合并一份——
            // 导 DWG 的工具写出一个 .png，下游拿到才会发现
            Rejects(() => ResolveAs("plan.png", ".dwg", ExportPaths.DocumentExtensions),
                "不支持的文件扩展名");
        }

        [Fact]
        public void ImageExportStillRejectsDocuments()
        {
            Rejects(() => Resolve("model.dwg"), "不支持的文件扩展名");
        }

        [Fact]
        public void TableExportAcceptsCsv()
        {
            var path = ResolveAs("门明细表", ".csv", ExportPaths.TableExtensions);

            Assert.Equal(Path.Combine(_root, "门明细表.csv"), path);
        }

        [Fact]
        public void EscapeAttemptsAreRejectedForEveryWhitelist()
        {
            // 越界检查在扩展名检查**之前**，换一个白名单不应当把它绕过去
            Rejects(() => ResolveAs(@"..\escape.dwg", ".dwg", ExportPaths.DocumentExtensions));
            Rejects(() => ResolveAs(@"C:\windows\evil.pdf", ".pdf", ExportPaths.DocumentExtensions));
            Rejects(() => ResolveAs("sub/model.ifc", ".ifc", ExportPaths.DocumentExtensions),
                "只能是文件名");
        }

        [Fact]
        public void EmptyWhitelistIsRejected()
        {
            // 空白名单意味着“什么都不允许”，静默放行才是真的危险
            Rejects(() => ResolveAs("a.dwg", ".dwg", new string[0]));
        }

        [Fact]
        public void EmptyNameIsRejected()
        {
            Rejects(() => Resolve("   "), "不能为空");
        }

        [Fact]
        public void StagingDirectoryIsCreatedInsideTheRootAndCleansUp()
        {
            Directory.CreateDirectory(_root);

            var staging = ExportPaths.CreateStagingDirectory(_root);

            Assert.True(Directory.Exists(staging));
            Assert.StartsWith(_root, staging);

            ExportPaths.SafeDelete(staging);
            Assert.False(Directory.Exists(staging));
        }

        [Fact]
        public void DeletingAMissingStagingDirectoryIsHarmless()
        {
            // 清理失败不该把一次成功的导出拖成失败
            ExportPaths.SafeDelete(Path.Combine(_root, "never-existed"));
        }

        // ==================== [M10] 按调用点传入的扩展名白名单 ====================

        private string ResolveProject(string fileName) =>
            ExportPaths.Resolve(_root, fileName, ".rvt", ExportPaths.ProjectExtensions);

        [Fact]
        public void ProjectWhitelistAcceptsRvt()
        {
            Assert.Equal(Path.Combine(_root, "办公楼.rvt"), ResolveProject("办公楼.rvt"));
        }

        [Fact]
        public void ProjectWhitelistFillsInTheDefaultExtension()
        {
            Assert.EndsWith(".rvt", ResolveProject("办公楼"));
        }

        [Theory]
        [InlineData("plan.png")]
        [InlineData("payload.exe")]
        public void ProjectWhitelistRejectsEverythingElse(string name)
        {
            // 两组白名单互不放行：能存模型的工具不该顺手能写图片，反之亦然
            Rejects(() => ResolveProject(name), "不支持的文件扩展名");
        }

        [Fact]
        public void ImageWhitelistStillRejectsRvt()
        {
            Rejects(() => Resolve("model.rvt"), "不支持的文件扩展名");
        }

        [Theory]
        [InlineData("../escape.rvt")]
        [InlineData("sub/model.rvt")]
        [InlineData("C:\\windows\\evil.rvt")]
        public void ProjectWhitelistKeepsEveryOtherBoundary(string name)
        {
            // 换一组扩展名不该顺带松开目录边界——这些拒绝对两个重载必须一样
            Rejects(() => ResolveProject(name));
        }

        [Fact]
        public void EmptyWhitelistIsRefused()
        {
            // 调用点忘了声明白名单时要当场失败，而不是默默放行一切
            Rejects(() => ExportPaths.Resolve(_root, "x.rvt", ".rvt", new string[0]), "未声明允许的扩展名");
        }
    }
}
