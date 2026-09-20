using System.Linq;
using RevitMCP.Addin.Guide;
using RevitMCP.Addin.Server;
using Xunit;

namespace RevitMCP.Addin.Tests
{
    /// <summary>
    /// 建模指引"存在且被告知"的守护测试。
    ///
    /// 指引本身写得再好，调用方不知道它在那儿就等于没有。
    /// 而"知道"只有两个入口：<c>initialize</c> 的 instructions，
    /// 和 <c>tools/list</c> 里的 revit_get_modeling_guide。
    /// 这两处都是一句话，很容易在某次"精简提示词"里被顺手删掉——
    /// 删掉之后一切照常工作，没有任何报错，只是从此再没人读指引。
    /// </summary>
    public class ModelingGuideAnnouncementTests
    {
        private static string Instructions => ServerHost.BuildInstructions();

        [Fact]
        public void GuideIsEmbeddedInTheAssembly()
        {
            // 五节全在。少一节意味着 csproj 的 EmbeddedResource 和
            // ModelingGuide.FileNames 已经不同步了（那两处必须一起改）。
            Assert.Equal(
                new[] { "limitations", "overview", "recovery", "sequence", "validation" },
                ModelingGuide.Sections.Select(section => section.Name).OrderBy(name => name).ToArray());

            Assert.All(ModelingGuide.Sections, section => Assert.False(string.IsNullOrWhiteSpace(section.Text)));
        }

        [Fact]
        public void InstructionsNameTheGuideTool()
        {
            Assert.Contains("revit_get_modeling_guide", Instructions);
        }

        [Fact]
        public void InstructionsNameTheResourceUri()
        {
            // 不支持 resources/* 的客户端用不上这条，但支持的能省下整个工具调用
            Assert.Contains(ModelingGuide.UriPrefix, Instructions);
        }

        [Theory]
        [InlineData("overview")]
        [InlineData("sequence")]
        [InlineData("validation")]
        [InlineData("recovery")]
        [InlineData("limitations")]
        public void InstructionsRouteToEverySection(string section)
        {
            // 每一节都要在开场白里有一个"什么时候读它"的触发条件。
            // 只宣传 overview 的话，调用方失败时不会想到还有 recovery。
            Assert.Contains(section, Instructions);
        }

        [Fact]
        public void InstructionsStaySmallEnoughToAlwaysCarry()
        {
            // 这段每次会话都进系统提示词，没有"按需加载"可言。
            // 上限不是美学要求：越界说明有人开始在这里写背景介绍了。
            Assert.InRange(Instructions.Length, 120, 320);
        }
    }
}
