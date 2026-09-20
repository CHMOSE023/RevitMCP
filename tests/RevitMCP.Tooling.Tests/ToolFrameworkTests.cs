using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RevitMCP.Protocol.Json;
using RevitMCP.Protocol.Mcp;
using RevitMCP.Tooling;
using RevitMCP.Tooling.Dispatch;
using RevitMCP.Tooling.Schema;
using Xunit;

namespace RevitMCP.Tooling.Tests
{
    // ==================== 测试用的假宿主与工具 ====================

    /// <summary>对应插件里的 UIApplication。</summary>
    public sealed class FakeHost
    {
        public string DocumentTitle { get; set; } = "项目1.rvt";
        public int CallCount;
    }

    /// <summary>同步执行，不涉及线程编组——这里要测的是管线而非调度。</summary>
    internal sealed class ImmediateDispatcher : IWorkDispatcher<FakeHost>
    {
        private readonly FakeHost _host;
        private readonly Exception _throwInstead;

        public ImmediateDispatcher(FakeHost host = null, Exception throwInstead = null)
        {
            _host = host ?? new FakeHost();
            _throwInstead = throwInstead;
        }

        public TimeSpan LastTimeout { get; private set; }

        public Task<TResult> InvokeAsync<TResult>(
            Func<FakeHost, TResult> work, TimeSpan timeout, CancellationToken cancellationToken)
        {
            LastTimeout = timeout;
            if (_throwInstead != null) return Task.FromException<TResult>(_throwInstead);

            try { return Task.FromResult(work(_host)); }
            catch (Exception ex) { return Task.FromException<TResult>(ex); }
        }
    }

    public sealed class GreetInput
    {
        [McpParam("要问候的名字", Required = true)]
        public string Name { get; set; }

        [McpParam("重复次数，默认 1")]
        public int? Times { get; set; }
    }

    public sealed class GreetOutput
    {
        public string Message { get; set; }
        public string Document { get; set; }
    }

    [McpTool("test_greet", Title = "问候", Description = "拼一句问候语。", ReadOnly = true, TimeoutSeconds = 5)]
    public sealed class GreetTool : McpTool<FakeHost, GreetInput, GreetOutput>
    {
        public override GreetOutput Execute(GreetInput input, ToolExecutionContext<FakeHost> context)
        {
            context.Host.CallCount++;
            return new GreetOutput
            {
                Message = string.Concat(Enumerable.Repeat("你好 " + input.Name + "。", input.Times ?? 1)),
                Document = context.Host.DocumentTitle
            };
        }
    }

    public sealed class MutateInput
    {
        [McpParam("新标题", Required = true)]
        public string Title { get; set; }
    }

    [McpTool("test_mutate", Title = "改标题", Description = "修改模型。", ReadOnly = false)]
    public sealed class MutateTool : McpTool<FakeHost, MutateInput, GreetOutput>
    {
        public override GreetOutput Execute(MutateInput input, ToolExecutionContext<FakeHost> context)
        {
            context.Host.DocumentTitle = input.Title;
            return new GreetOutput { Message = "已修改", Document = input.Title };
        }
    }

    public sealed class EmptyInput
    {
    }

    [McpTool("test_fail", Description = "总是失败。", ReadOnly = true)]
    public sealed class FailingTool : McpTool<FakeHost, EmptyInput, GreetOutput>
    {
        public override GreetOutput Execute(EmptyInput input, ToolExecutionContext<FakeHost> context) =>
            throw new ToolFailureException(McpDomainError.NoActiveDocument, "没有打开的文档。");
    }

    [McpTool("test_boom", Description = "抛未预期异常。", ReadOnly = true)]
    public sealed class ExplodingTool : McpTool<FakeHost, EmptyInput, GreetOutput>
    {
        public override GreetOutput Execute(EmptyInput input, ToolExecutionContext<FakeHost> context) =>
            throw new InvalidOperationException("内部炸了");
    }

    // ==================== Schema 生成 ====================

    public class SchemaGeneratorTests
    {
        private enum Shade { Light, Dark }

        private sealed class AllTypes
        {
            [McpParam("必填字符串", Required = true)]
            public string Text { get; set; }

            [McpParam("不可空值类型 → 自动必填")]
            public int Count { get; set; }

            [McpParam("可空 → 可选")]
            public int? Optional { get; set; }

            public bool Flag { get; set; }
            public double Ratio { get; set; }
            public Shade Shade { get; set; }
            public List<string> Tags { get; set; }
            public Nested Child { get; set; }
            [McpIgnore]
            public string Hidden { get; set; }
            public string ReadOnlyProperty => "x";
        }

        private sealed class Nested
        {
            public string Inner { get; set; }
        }

        [Fact]
        public void MapsClrTypesToJsonSchemaTypes()
        {
            var schema = SchemaGenerator.Generate(typeof(AllTypes));
            var properties = schema["properties"];

            Assert.Equal("object", schema["type"].AsString);
            Assert.Equal("string", properties["text"]["type"].AsString);
            Assert.Equal("integer", properties["count"]["type"].AsString);
            Assert.Equal("integer", properties["optional"]["type"].AsString);
            Assert.Equal("boolean", properties["flag"]["type"].AsString);
            Assert.Equal("number", properties["ratio"]["type"].AsString);
            Assert.Equal("array", properties["tags"]["type"].AsString);
            Assert.Equal("string", properties["tags"]["items"]["type"].AsString);
            Assert.Equal("object", properties["child"]["type"].AsString);
            Assert.Equal("string", properties["child"]["properties"]["inner"]["type"].AsString);
        }

        [Fact]
        public void EnumBecomesStringWithAllowedValues()
        {
            // 枚举用字符串传：数字值对模型没有意义，也经不起枚举顺序调整
            var shade = SchemaGenerator.Generate(typeof(AllTypes))["properties"]["shade"];

            Assert.Equal("string", shade["type"].AsString);
            Assert.Equal(new[] { "Light", "Dark" }, shade["enum"].Items.Select(i => i.AsString));
        }

        /// <summary>
        /// 判别式参数（operation / kind / viewType 这类）的取值必须进 schema 的 enum。
        ///
        /// 只写在 description 的散文里等于要求调用方从自然语言里把枚举抠出来，
        /// 客户端也没法据此做约束——那一类错误本不该发生在运行期。
        /// </summary>
        private sealed class Discriminated
        {
            [McpParam("操作类型", Required = true,
                      AllowedValues = new[] { "move", "copy", "rotate" })]
            public string Operation { get; set; }

            [McpParam("要导出的格式，可以多选",
                      AllowedValues = new[] { "dwg", "pdf" })]
            public List<string> Formats { get; set; }

            [McpParam("没有取值约束的自由文本")]
            public string Note { get; set; }
        }

        [Fact]
        public void AllowedValuesBecomesEnumOnStringProperties()
        {
            var properties = SchemaGenerator.Generate(typeof(Discriminated))["properties"];
            var operation = properties["operation"];

            Assert.Equal("string", operation["type"].AsString);
            Assert.Equal(new[] { "move", "copy", "rotate" },
                operation["enum"].Items.Select(i => i.AsString));
        }

        [Fact]
        public void AllowedValuesOnArrayLandsOnItemsNotTheArray()
        {
            // 挂错地方的 schema 不会报错，只会悄悄失去约束力——
            // 所以这一条必须有测试盯着
            var formats = SchemaGenerator.Generate(typeof(Discriminated))["properties"]["formats"];

            Assert.Equal("array", formats["type"].AsString);
            Assert.Null(formats["enum"]);
            Assert.Equal(new[] { "dwg", "pdf" },
                formats["items"]["enum"].Items.Select(i => i.AsString));
        }

        [Fact]
        public void PropertiesWithoutAllowedValuesGetNoEnum()
        {
            var note = SchemaGenerator.Generate(typeof(Discriminated))["properties"]["note"];

            Assert.Equal("string", note["type"].AsString);
            Assert.Null(note["enum"]);
        }

        [Fact]
        public void RequiredCoversExplicitFlagAndNonNullableValueTypes()
        {
            var required = SchemaGenerator.Generate(typeof(AllTypes))["required"]
                .Items.Select(i => i.AsString).ToArray();

            Assert.Contains("text", required);      // 显式 Required
            Assert.Contains("count", required);     // int → 必填
            Assert.Contains("flag", required);      // bool → 必填
            Assert.DoesNotContain("optional", required);  // int? → 可选
            Assert.DoesNotContain("tags", required);      // 引用类型 → 可选
        }

        [Fact]
        public void IgnoredAndReadOnlyPropertiesAreExcluded()
        {
            var properties = SchemaGenerator.Generate(typeof(AllTypes))["properties"];

            Assert.False(properties.ContainsKey("hidden"));
            Assert.False(properties.ContainsKey("readOnlyProperty"));   // 入参必须可写才能绑定
        }

        [Fact]
        public void DescriptionsComeFromMcpParam()
        {
            var properties = SchemaGenerator.Generate(typeof(AllTypes))["properties"];
            Assert.Equal("必填字符串", properties["text"]["description"].AsString);
        }

        [Fact]
        public void ParameterlessToolDeclaresEmptyObject()
        {
            // 规范推荐：无参工具显式声明只接受空对象
            var schema = SchemaGenerator.Generate(typeof(EmptyInput));
            Assert.Equal("object", schema["type"].AsString);
            Assert.False(schema["additionalProperties"].AsBool);
        }

        private sealed class SelfReferencing
        {
            public string Name { get; set; }
            public SelfReferencing Child { get; set; }
        }

        [Fact]
        public void SelfReferencingTypeDoesNotRecurseForever()
        {
            var schema = SchemaGenerator.Generate(typeof(SelfReferencing));
            Assert.Equal("object", schema["type"].AsString);   // 截断而非栈溢出
        }
    }

    // ==================== 入参绑定 ====================

    public class JsonMapperTests
    {
        private sealed class Target
        {
            public string Text { get; set; }
            public int Count { get; set; }
            public int? Optional { get; set; }
            public double Ratio { get; set; }
            public bool Flag { get; set; }
            public List<string> Tags { get; set; }
        }

        private static JsonValue Minimal() =>
            JsonValue.NewObject().Set("text", "a").Set("count", 1).Set("ratio", 1.5).Set("flag", true);

        [Fact]
        public void BindsAllSupportedTypes()
        {
            var json = Minimal().Set("optional", 7)
                .Set("tags", JsonValue.NewArray().Add("x").Add("y"));

            var result = (Target)JsonMapper.Bind(json, typeof(Target));

            Assert.Equal("a", result.Text);
            Assert.Equal(1, result.Count);
            Assert.Equal(7, result.Optional);
            Assert.Equal(1.5, result.Ratio);
            Assert.True(result.Flag);
            Assert.Equal(new[] { "x", "y" }, result.Tags);
        }

        [Fact]
        public void MissingRequiredFieldIsReported()
        {
            // count 是不可空值类型 → 必填
            var json = JsonValue.NewObject().Set("text", "a").Set("ratio", 1.0).Set("flag", false);
            var ex = Assert.Throws<ToolInputException>(() => JsonMapper.Bind(json, typeof(Target)));
            Assert.Contains("count", ex.Message);
        }

        [Fact]
        public void ReferenceTypeWithoutExplicitRequiredIsOptional()
        {
            // 必填规则只有一条：显式 Required，或不可空值类型。
            // string 是引用类型，因此可选——Schema 生成与入参绑定必须对此给出一致的答案。
            var json = JsonValue.NewObject().Set("count", 1).Set("ratio", 1.0).Set("flag", false);

            var result = (Target)JsonMapper.Bind(json, typeof(Target));
            Assert.Null(result.Text);

            var required = SchemaGenerator.Generate(typeof(Target))["required"]
                .Items.Select(i => i.AsString).ToArray();
            Assert.DoesNotContain("text", required);
            Assert.Contains("count", required);
        }

        [Fact]
        public void UnknownFieldIsRejectedRatherThanIgnored()
        {
            // 静默忽略拼错的参数名，模型会一直以为自己传对了
            var json = Minimal().Set("txet", "typo");
            var ex = Assert.Throws<ToolInputException>(() => JsonMapper.Bind(json, typeof(Target)));

            Assert.Contains("txet", ex.Message);
            Assert.Contains("可用参数", ex.Message);   // 错误信息里要带出正确拼写
        }

        [Fact]
        public void MetaFieldIsNotTreatedAsAToolArgument()
        {
            // modern era 的协议元数据会混在 params 里，不能当成工具入参
            var json = Minimal().Set("_meta", JsonValue.NewObject().Set("x", 1));
            var result = (Target)JsonMapper.Bind(json, typeof(Target));
            Assert.Equal("a", result.Text);
        }

        [Fact]
        public void TypeMismatchNamesTheFieldAndBothTypes()
        {
            var json = Minimal().Set("count", "not a number");
            var ex = Assert.Throws<ToolInputException>(() => JsonMapper.Bind(json, typeof(Target)));

            Assert.Contains("count", ex.Message);
            Assert.Contains("整数", ex.Message);
            Assert.Contains("字符串", ex.Message);
        }

        [Fact]
        public void NestedPathAppearsInErrorMessage()
        {
            var json = JsonValue.NewObject()
                .Set("text", "a").Set("count", 1).Set("ratio", 1.0).Set("flag", true)
                .Set("tags", JsonValue.NewArray().Add("ok").Add(5));

            var ex = Assert.Throws<ToolInputException>(() => JsonMapper.Bind(json, typeof(Target)));
            Assert.Contains("tags[1]", ex.Message);
        }

        [Fact]
        public void NullIsTreatedAsAbsent()
        {
            var json = Minimal().Set("optional", JsonValue.Null);
            var result = (Target)JsonMapper.Bind(json, typeof(Target));
            Assert.Null(result.Optional);
        }

        [Fact]
        public void InvalidEnumValueListsTheAllowedOnes()
        {
            var json = JsonValue.NewObject().Set("mode", "sideways");
            var ex = Assert.Throws<ToolInputException>(() => JsonMapper.Bind(json, typeof(EnumTarget)));

            Assert.Contains("Fast", ex.Message);
            Assert.Contains("Slow", ex.Message);
        }

        private sealed class EnumTarget
        {
            public Speed Mode { get; set; }
        }

        private enum Speed { Fast, Slow }

        [Fact]
        public void SerializesOutputWithCamelCaseNames()
        {
            var json = JsonMapper.ToJson(new GreetOutput { Message = "hi", Document = "a.rvt" });

            Assert.Equal("hi", json["message"].AsString);
            Assert.Equal("a.rvt", json["document"].AsString);
        }

        [Fact]
        public void SerializesNestedListsAndNulls()
        {
            var json = JsonMapper.ToJson(new Target
            {
                Text = null,
                Count = 3,
                Tags = new List<string> { "a", "b" }
            });

            Assert.True(json["text"].IsNull);
            Assert.Equal(3, json["count"].AsInt64);
            Assert.Equal(2, json["tags"].Count);
        }

        [Fact]
        public void LargeIntegerSurvivesSerialization()
        {
            // ElementId 在 Revit 2024 起是 64 位，不能退化成 double
            const long big = 9007199254740993L;
            Assert.Equal(big, JsonMapper.ToJson(big).AsInt64);
        }
    }

    // ==================== 注册表 ====================

    public class ToolRegistryTests
    {
        private static ToolRegistry<FakeHost> Build(IEnumerable<string> disabled = null)
        {
            var registry = new ToolRegistry<FakeHost>();
            registry.RegisterAssembly(typeof(GreetTool).Assembly, disabled);
            return registry;
        }

        [Fact]
        public void DiscoversAttributedToolsAndBuildsDefinitions()
        {
            var registry = Build();

            Assert.True(registry.TryGet("test_greet", out var tool));
            Assert.Equal("问候", tool.Definition.Title);
            Assert.Equal("string", tool.Definition.InputSchema["properties"]["name"]["type"].AsString);
            Assert.True(tool.IsReadOnly);
        }

        [Fact]
        public void OrderIsStableAcrossRegistrations()
        {
            // 规范要求 tools/list 顺序稳定，客户端才能缓存
            var first = Build().Definitions.Select(d => d.Name).ToArray();
            var second = Build().Definitions.Select(d => d.Name).ToArray();
            Assert.Equal(first, second);
        }

        [Fact]
        public void DisabledToolsAreSkipped()
        {
            var registry = Build(new[] { "test_mutate" });

            Assert.False(registry.TryGet("test_mutate", out _));
            Assert.True(registry.TryGet("test_greet", out _));
        }

        [Fact]
        public void DuplicateNameIsRejected()
        {
            var registry = new ToolRegistry<FakeHost>();
            registry.Register(typeof(GreetTool));
            Assert.Throws<InvalidOperationException>(() => registry.Register(typeof(GreetTool)));
        }

        [Fact]
        public void ToolForAnotherContextTypeIsRejectedWithAClearMessage()
        {
            var registry = new ToolRegistry<string>();
            var ex = Assert.Throws<InvalidOperationException>(() => registry.Register(typeof(GreetTool)));
            Assert.Contains("McpTool", ex.Message);
        }
    }

    // ==================== 执行管线 ====================

    public class ToolPipelineTests
    {
        private static ToolPipeline<FakeHost> Build(
            IWorkDispatcher<FakeHost> dispatcher, bool writeEnabled = false)
        {
            var registry = new ToolRegistry<FakeHost>();
            registry.RegisterAssembly(typeof(GreetTool).Assembly);

            return new ToolPipeline<FakeHost>(registry, dispatcher, new ToolPipelineOptions
            {
                WriteEnabled = () => writeEnabled,
                DefaultTimeoutSeconds = () => 30
            });
        }

        private static JsonValue Args(params (string Key, JsonValue Value)[] pairs)
        {
            var json = JsonValue.NewObject();
            foreach (var pair in pairs) json.Set(pair.Key, pair.Value);
            return json;
        }

        [Fact]
        public async Task SuccessfulCallReturnsTextAndStructuredContent()
        {
            var host = new FakeHost();
            var result = await Build(new ImmediateDispatcher(host))
                .CallToolAsync("test_greet", Args(("name", "世界")), CancellationToken.None);

            Assert.False(result.IsError);
            Assert.Equal("你好 世界。", result.StructuredContent["message"].AsString);
            // 规范建议：结构化内容之外也给一份序列化文本
            Assert.Contains("你好 世界。", result.Text);
            Assert.Equal(1, host.CallCount);
        }

        [Fact]
        public async Task UnknownToolIsAJsonRpcErrorNotAToolFailure()
        {
            // 模型改不了"工具不存在"，所以走协议错误
            await Assert.ThrowsAsync<ToolNotFoundException>(() =>
                Build(new ImmediateDispatcher()).CallToolAsync("nope", JsonValue.NewObject(), CancellationToken.None));
        }

        [Fact]
        public async Task BadArgumentsBecomeIsErrorSoTheModelCanRetry()
        {
            var result = await Build(new ImmediateDispatcher())
                .CallToolAsync("test_greet", JsonValue.NewObject(), CancellationToken.None);

            Assert.True(result.IsError);
            Assert.Contains(McpDomainError.InvalidParameter, result.Text);
            Assert.Contains("name", result.Text);
        }

        [Fact]
        public async Task WriteToolIsRejectedWhenWriteModeIsOff()
        {
            var host = new FakeHost();
            var result = await Build(new ImmediateDispatcher(host), writeEnabled: false)
                .CallToolAsync("test_mutate", Args(("title", "新标题")), CancellationToken.None);

            Assert.True(result.IsError);
            Assert.Contains(McpDomainError.WriteDisabled, result.Text);
            Assert.Equal("项目1.rvt", host.DocumentTitle);   // 必须没被改
        }

        [Fact]
        public async Task WriteToolRunsWhenWriteModeIsOn()
        {
            var host = new FakeHost();
            var result = await Build(new ImmediateDispatcher(host), writeEnabled: true)
                .CallToolAsync("test_mutate", Args(("title", "新标题")), CancellationToken.None);

            Assert.False(result.IsError);
            Assert.Equal("新标题", host.DocumentTitle);
        }

        [Fact]
        public async Task ReadOnlyToolIsUnaffectedByWriteProtection()
        {
            var result = await Build(new ImmediateDispatcher(), writeEnabled: false)
                .CallToolAsync("test_greet", Args(("name", "a")), CancellationToken.None);

            Assert.False(result.IsError);
        }

        [Fact]
        public async Task WriteProtectionIsCheckedBeforeMarshalling()
        {
            // 被拒绝的调用不该占用 Revit 主线程
            var dispatcher = new CountingDispatcher();
            var pipeline = new ToolPipeline<FakeHost>(
                BuildRegistry(), dispatcher, new ToolPipelineOptions { WriteEnabled = () => false });

            await pipeline.CallToolAsync("test_mutate", Args(("title", "x")), CancellationToken.None);

            Assert.Equal(0, dispatcher.Invocations);
        }

        [Fact]
        public async Task ToolFailureKeepsItsDomainCode()
        {
            var result = await Build(new ImmediateDispatcher())
                .CallToolAsync("test_fail", JsonValue.NewObject(), CancellationToken.None);

            Assert.True(result.IsError);
            Assert.StartsWith(McpDomainError.NoActiveDocument, result.Text);
        }

        [Fact]
        public async Task RevitBusyIsMappedToItsOwnCode()
        {
            var dispatcher = new ImmediateDispatcher(throwInstead: new RevitBusyException(TimeSpan.FromSeconds(30)));
            var result = await Build(dispatcher).CallToolAsync("test_greet", Args(("name", "a")), CancellationToken.None);

            Assert.StartsWith(McpDomainError.RevitBusy, result.Text);
        }

        [Fact]
        public async Task DispatchTimeoutIsNotReportedAsRevitBusy()
        {
            // REVIT_BUSY 表示"模型没被碰过"；已经跑起来的操作绝不能用它
            var dispatcher = new ImmediateDispatcher(throwInstead: new DispatchTimeoutException(TimeSpan.FromSeconds(30)));
            var result = await Build(dispatcher).CallToolAsync("test_greet", Args(("name", "a")), CancellationToken.None);

            Assert.StartsWith(McpDomainError.Timeout, result.Text);
            Assert.DoesNotContain(McpDomainError.RevitBusy, result.Text);
        }

        [Fact]
        public async Task UnexpectedExceptionBecomesIsErrorWithoutLeakingStackTrace()
        {
            var result = await Build(new ImmediateDispatcher())
                .CallToolAsync("test_boom", JsonValue.NewObject(), CancellationToken.None);

            Assert.True(result.IsError);
            Assert.Contains("内部炸了", result.Text);
            Assert.DoesNotContain("   at ", result.Text);   // 堆栈对模型无用，只占上下文
        }

        [Fact]
        public async Task ToolDeclaredTimeoutOverridesTheDefault()
        {
            var dispatcher = new ImmediateDispatcher();
            await Build(dispatcher).CallToolAsync("test_greet", Args(("name", "a")), CancellationToken.None);

            Assert.Equal(TimeSpan.FromSeconds(5), dispatcher.LastTimeout);   // [McpTool(TimeoutSeconds = 5)]
        }

        [Fact]
        public async Task ToolWithoutDeclaredTimeoutUsesTheConfiguredDefault()
        {
            var dispatcher = new ImmediateDispatcher();
            await Build(dispatcher).CallToolAsync("test_fail", JsonValue.NewObject(), CancellationToken.None);

            Assert.Equal(TimeSpan.FromSeconds(30), dispatcher.LastTimeout);
        }

        [Fact]
        public async Task WriteModeChangesTakeEffectWithoutRebuildingThePipeline()
        {
            // 用户在 Ribbon 上切换开关后应立即生效，不必重启服务
            var writeEnabled = false;
            var host = new FakeHost();
            var pipeline = new ToolPipeline<FakeHost>(
                BuildRegistry(), new ImmediateDispatcher(host),
                new ToolPipelineOptions { WriteEnabled = () => writeEnabled });

            var before = await pipeline.CallToolAsync("test_mutate", Args(("title", "A")), CancellationToken.None);
            Assert.True(before.IsError);

            writeEnabled = true;
            var after = await pipeline.CallToolAsync("test_mutate", Args(("title", "B")), CancellationToken.None);

            Assert.False(after.IsError);
            Assert.Equal("B", host.DocumentTitle);
        }

        [Fact]
        public void ListToolsExposesEveryRegisteredTool()
        {
            var names = Build(new ImmediateDispatcher()).ListTools().Select(t => t.Name).ToArray();
            Assert.Contains("test_greet", names);
            Assert.Contains("test_mutate", names);
        }

        private static ToolRegistry<FakeHost> BuildRegistry()
        {
            var registry = new ToolRegistry<FakeHost>();
            registry.RegisterAssembly(typeof(GreetTool).Assembly);
            return registry;
        }

        private sealed class CountingDispatcher : IWorkDispatcher<FakeHost>
        {
            public int Invocations;

            public Task<TResult> InvokeAsync<TResult>(
                Func<FakeHost, TResult> work, TimeSpan timeout, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Invocations);
                return Task.FromResult(work(new FakeHost()));
            }
        }
    }
}
