using System.Collections.Generic;
using System.Linq;
using RevitMCP.Protocol.Json;
using RevitMCP.Tooling;
using RevitMCP.Tooling.Schema;
using Xunit;

namespace RevitMCP.Tooling.Tests
{
    // ==================== 与 M6 建模工具同构的入参 ====================
    //
    // 这些 DTO 是 CreateLineBasedTool / CreateSurfaceBasedTool 入参的翻版，
    // 只是不引用 Revit API，因此能在没装 Revit 的机器上跑。
    //
    // 要验证的是架构文档 §M6「框架无需扩展」那句断言：
    // 数组套对象、对象再套对象、对象再套数组这条链路，Schema 生成与入参绑定
    // 在 M3 就已经支持。断言写成测试，改坏了才有人知道。

    public sealed class ShapePoint
    {
        [McpParam("X 坐标，毫米", Required = true)]
        public double X { get; set; }

        [McpParam("Y 坐标，毫米", Required = true)]
        public double Y { get; set; }

        [McpParam("Z 坐标，毫米")]
        public double? Z { get; set; }
    }

    public sealed class ShapeLine
    {
        [McpParam("起点", Required = true)]
        public ShapePoint P0 { get; set; }

        [McpParam("终点", Required = true)]
        public ShapePoint P1 { get; set; }
    }

    public sealed class ShapeSpec
    {
        [McpParam("类别", Required = true)]
        public string Category { get; set; }

        [McpParam("类型 ID")]
        public string TypeId { get; set; }

        [McpParam("定位线", Required = true)]
        public ShapeLine LocationLine { get; set; }

        [McpParam("高度，毫米")]
        public double? Height { get; set; }
    }

    public sealed class ShapeBoundary
    {
        [McpParam("外轮廓", Required = true)]
        public List<ShapeLine> OuterLoop { get; set; }
    }

    public sealed class ShapeSurfaceSpec
    {
        [McpParam("类别", Required = true)]
        public string Category { get; set; }

        [McpParam("边界", Required = true)]
        public ShapeBoundary Boundary { get; set; }
    }

    public sealed class ShapeBatchInput
    {
        [McpParam("要创建的构件", Required = true)]
        public List<ShapeSpec> Elements { get; set; }

        [McpParam("超过上限时确认")]
        public bool? Confirm { get; set; }
    }

    public sealed class ShapeSurfaceBatchInput
    {
        [McpParam("要创建的构件", Required = true)]
        public List<ShapeSurfaceSpec> Elements { get; set; }
    }

    // ==================== Schema ====================

    public class BatchSchemaTests
    {
        [Fact]
        public void BatchInputIsAnArrayOfObjects()
        {
            var elements = SchemaGenerator.Generate(typeof(ShapeBatchInput))["properties"]["elements"];

            Assert.Equal("array", elements["type"].AsString);
            Assert.Equal("object", elements["items"]["type"].AsString);
        }

        [Fact]
        public void NestedObjectsExpandAllTheWayDown()
        {
            // locationLine.p0.x —— 三层下去仍然是个有类型的字段，
            // 而不是退化成"任意对象"
            var x = SchemaGenerator.Generate(typeof(ShapeBatchInput))
                ["properties"]["elements"]["items"]["properties"]["locationLine"]
                ["properties"]["p0"]["properties"]["x"];

            Assert.Equal("number", x["type"].AsString);
            Assert.Contains("毫米", x["description"].AsString);
        }

        [Fact]
        public void RequiredFlagsSurviveNesting()
        {
            var items = SchemaGenerator.Generate(typeof(ShapeBatchInput))["properties"]["elements"]["items"];

            var specRequired = items["required"].Items.Select(i => i.AsString).ToArray();
            Assert.Contains("category", specRequired);
            Assert.Contains("locationLine", specRequired);
            Assert.DoesNotContain("typeId", specRequired);

            var pointRequired = items["properties"]["locationLine"]["properties"]["p0"]["required"]
                .Items.Select(i => i.AsString).ToArray();

            // double 必填、double? 可选——这条规则在嵌套层里必须和顶层一致，
            // 否则模型会以为 z 是必须给的
            Assert.Contains("x", pointRequired);
            Assert.Contains("y", pointRequired);
            Assert.DoesNotContain("z", pointRequired);
        }

        [Fact]
        public void ArrayInsideObjectInsideArrayStillExpands()
        {
            // boundary.outerLoop[] —— 数组 → 对象 → 数组 → 对象
            var segment = SchemaGenerator.Generate(typeof(ShapeSurfaceBatchInput))
                ["properties"]["elements"]["items"]["properties"]["boundary"]
                ["properties"]["outerLoop"];

            Assert.Equal("array", segment["type"].AsString);
            Assert.Equal("object", segment["items"]["type"].AsString);
            Assert.Equal("number", segment["items"]["properties"]["p1"]["properties"]["y"]["type"].AsString);
        }
    }

    // ==================== 绑定 ====================

    public class BatchBindingTests
    {
        private static JsonValue Point(double x, double y)
        {
            return JsonValue.NewObject().Set("x", x).Set("y", y);
        }

        private static JsonValue Line(double x0, double y0, double x1, double y1)
        {
            return JsonValue.NewObject().Set("p0", Point(x0, y0)).Set("p1", Point(x1, y1));
        }

        private static JsonValue Spec(string category, double x0, double y0, double x1, double y1)
        {
            return JsonValue.NewObject()
                .Set("category", category)
                .Set("locationLine", Line(x0, y0, x1, y1));
        }

        [Fact]
        public void BindsEveryItemOfTheBatch()
        {
            var json = JsonValue.NewObject().Set("elements", JsonValue.NewArray()
                .Add(Spec("OST_Walls", 0, 0, 6000, 0))
                .Add(Spec("OST_Walls", 6000, 0, 6000, 4000)));

            var input = (ShapeBatchInput)JsonMapper.Bind(json, typeof(ShapeBatchInput));

            Assert.Equal(2, input.Elements.Count);
            Assert.Equal("OST_Walls", input.Elements[1].Category);
            Assert.Equal(6000, input.Elements[1].LocationLine.P0.X);
            Assert.Equal(4000, input.Elements[1].LocationLine.P1.Y);
        }

        [Fact]
        public void OmittedOptionalsStayNullAcrossTheWholeTree()
        {
            var json = JsonValue.NewObject().Set("elements", JsonValue.NewArray()
                .Add(Spec("OST_Walls", 0, 0, 1000, 0)));

            var input = (ShapeBatchInput)JsonMapper.Bind(json, typeof(ShapeBatchInput));

            Assert.Null(input.Confirm);
            Assert.Null(input.Elements[0].TypeId);
            Assert.Null(input.Elements[0].Height);
            Assert.Null(input.Elements[0].LocationLine.P0.Z);
        }

        [Fact]
        public void ErrorMessageNamesTheOffendingItemAndField()
        {
            // 批量调用里，"第几项的哪个字段"是模型唯一能据以改正的信息。
            // 只说"参数错误"，它只能把整批重猜一遍
            var json = JsonValue.NewObject().Set("elements", JsonValue.NewArray()
                .Add(Spec("OST_Walls", 0, 0, 6000, 0))
                .Add(JsonValue.NewObject()
                    .Set("category", "OST_Walls")
                    .Set("locationLine", JsonValue.NewObject()
                        .Set("p0", JsonValue.NewObject().Set("x", "零").Set("y", 0))
                        .Set("p1", Point(1000, 0)))));

            var ex = Assert.Throws<ToolInputException>(() => JsonMapper.Bind(json, typeof(ShapeBatchInput)));

            Assert.Contains("elements[1]", ex.Message);
            Assert.Contains("p0.x", ex.Message);
        }

        [Fact]
        public void MissingNestedRequiredFieldIsNamedByFullPath()
        {
            var json = JsonValue.NewObject().Set("elements", JsonValue.NewArray()
                .Add(JsonValue.NewObject()
                    .Set("category", "OST_Walls")
                    .Set("locationLine", JsonValue.NewObject().Set("p0", Point(0, 0)))));

            var ex = Assert.Throws<ToolInputException>(() => JsonMapper.Bind(json, typeof(ShapeBatchInput)));

            Assert.Contains("elements[0].locationLine.p1", ex.Message);
        }

        [Fact]
        public void UnsupportedFieldInsideAnItemIsRejectedNotIgnored()
        {
            // 参考项目的建模工具有 thickness 字段，本项目刻意没有——墙厚由类型决定。
            // 模型照着别处的记忆传了 thickness，必须当场说"没这个参数"并列出有哪些，
            // 而不是默默忽略、建出一面厚度不对的墙
            var json = JsonValue.NewObject().Set("elements", JsonValue.NewArray()
                .Add(Spec("OST_Walls", 0, 0, 6000, 0).Set("thickness", 200)));

            var ex = Assert.Throws<ToolInputException>(() => JsonMapper.Bind(json, typeof(ShapeBatchInput)));

            Assert.Contains("thickness", ex.Message);
            Assert.Contains("可用参数", ex.Message);
        }

        [Fact]
        public void BindsTheDoubleNestedBoundaryArray()
        {
            var json = JsonValue.NewObject().Set("elements", JsonValue.NewArray()
                .Add(JsonValue.NewObject()
                    .Set("category", "OST_Floors")
                    .Set("boundary", JsonValue.NewObject()
                        .Set("outerLoop", JsonValue.NewArray()
                            .Add(Line(0, 0, 6000, 0))
                            .Add(Line(6000, 0, 6000, 4000))
                            .Add(Line(6000, 4000, 0, 0))))));

            var input = (ShapeSurfaceBatchInput)JsonMapper.Bind(json, typeof(ShapeSurfaceBatchInput));

            Assert.Equal(3, input.Elements[0].Boundary.OuterLoop.Count);
            Assert.Equal(4000, input.Elements[0].Boundary.OuterLoop[1].P1.Y);
        }

        [Fact]
        public void ErrorPathReachesIntoTheDoubleNestedArray()
        {
            var json = JsonValue.NewObject().Set("elements", JsonValue.NewArray()
                .Add(JsonValue.NewObject()
                    .Set("category", "OST_Floors")
                    .Set("boundary", JsonValue.NewObject()
                        .Set("outerLoop", JsonValue.NewArray()
                            .Add(Line(0, 0, 6000, 0))
                            .Add(JsonValue.NewObject()
                                .Set("p0", Point(6000, 0))
                                .Set("p1", JsonValue.NewObject().Set("x", 6000).Set("y", true)))))));

            var ex = Assert.Throws<ToolInputException>(() => JsonMapper.Bind(json, typeof(ShapeSurfaceBatchInput)));

            Assert.Contains("elements[0].boundary.outerLoop[1].p1.y", ex.Message);
        }
    }
}
