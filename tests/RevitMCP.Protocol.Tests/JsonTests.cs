using System;
using RevitMCP.Protocol.Json;
using Xunit;

namespace RevitMCP.Protocol.Tests
{
    public class JsonParseTests
    {
        [Theory]
        [InlineData("null", JsonKind.Null)]
        [InlineData("true", JsonKind.Bool)]
        [InlineData("false", JsonKind.Bool)]
        [InlineData("0", JsonKind.Number)]
        [InlineData("-1.5e10", JsonKind.Number)]
        [InlineData("\"\"", JsonKind.String)]
        [InlineData("[]", JsonKind.Array)]
        [InlineData("{}", JsonKind.Object)]
        public void ParsesScalarKinds(string text, JsonKind expected)
        {
            Assert.Equal(expected, JsonValue.Parse(text).Kind);
        }

        [Fact]
        public void PreservesObjectMemberOrder()
        {
            // 顺序稳定是为了配置文件对人友好，也让协议报文的 diff 可读
            var v = JsonValue.Parse("{\"z\":1,\"a\":2,\"m\":3}");
            Assert.Equal(new[] { "z", "a", "m" }, v.Keys);
        }

        [Fact]
        public void RoundTripsLargeIntegerWithoutPrecisionLoss()
        {
            // JSON-RPC 的 id 和 Revit 2024 的 ElementId 都可能超出 double 的安全整数范围
            const long big = 9007199254740993L;   // 2^53 + 1
            var json = JsonValue.Number(big).ToJson();
            Assert.Equal("9007199254740993", json);
            Assert.Equal(big, JsonValue.Parse(json).AsInt64);
        }

        [Fact]
        public void KeepsIntegerLiteralShape()
        {
            Assert.Equal("1", JsonValue.Parse("1").ToJson());
            Assert.Equal("1.0", JsonValue.Parse("1.0").ToJson());
        }

        [Fact]
        public void ParsesEscapesIncludingSurrogatePairs()
        {
            var v = JsonValue.Parse("\"a\\nb\\t\\u4e2d\\uD83D\\uDE00\"");
            Assert.Equal("a\nb\t中😀", v.AsString);
        }

        [Fact]
        public void RoundTripsControlCharactersAndQuotes()
        {
            const string original = "引号\" 反斜杠\\ 换行\n 制表\t 退格\b 控制";
            var text = JsonValue.String(original).ToJson();
            Assert.Equal(original, JsonValue.Parse(text).AsString);
        }

        [Fact]
        public void KeepsNonAsciiRaw()
        {
            // 传输层统一按 UTF-8 编码，没必要把中文转成 \uXXXX
            Assert.Equal("\"中文\"", JsonValue.String("中文").ToJson());
        }

        [Theory]
        [InlineData("")]
        [InlineData("{")]
        [InlineData("{\"a\":}")]
        [InlineData("{\"a\" 1}")]
        [InlineData("[1,]")]
        [InlineData("01")]
        [InlineData("1.")]
        [InlineData("\"unterminated")]
        [InlineData("\"bad \\q escape\"")]
        [InlineData("{} garbage")]
        [InlineData("tru")]
        public void RejectsMalformedInput(string text)
        {
            Assert.Throws<JsonException>(() => JsonValue.Parse(text));
        }

        [Fact]
        public void RejectsRawControlCharacterInString()
        {
            Assert.Throws<JsonException>(() => JsonValue.Parse("\"a\nb\""));
        }

        [Fact]
        public void RejectsExcessiveNesting()
        {
            // 深层嵌套会打爆调用栈，而 StackOverflowException 无法捕获，会带走整个 Revit 进程
            var text = new string('[', 500) + new string(']', 500);
            Assert.Throws<JsonException>(() => JsonValue.Parse(text));
        }
    }

    public class JsonBuildTests
    {
        [Fact]
        public void MissingKeyReturnsNullButJsonNullReturnsNullValue()
        {
            var v = JsonValue.Parse("{\"present\":null}");
            Assert.Null(v["absent"]);                 // 键不存在
            Assert.Same(JsonValue.Null, v["present"]); // 键存在，值为 JSON null
        }

        [Fact]
        public void SetOverwritesWithoutDuplicatingKey()
        {
            var v = JsonValue.NewObject().Set("a", 1).Set("b", 2).Set("a", 3);
            Assert.Equal(2, v.Count);
            Assert.Equal(new[] { "a", "b" }, v.Keys);
            Assert.Equal(3, v["a"].AsInt64);
        }

        [Fact]
        public void IndentedOutputIsStable()
        {
            var v = JsonValue.NewObject()
                .Set("port", 7801)
                .Set("autoStart", true)
                .Set("tools", JsonValue.NewArray().Add("a").Add("b"));

            Assert.Equal(
                "{\n  \"port\": 7801,\n  \"autoStart\": true,\n  \"tools\": [\n    \"a\",\n    \"b\"\n  ]\n}",
                v.ToJson(indented: true));
        }

        [Fact]
        public void EmptyContainersStayCompactWhenIndented()
        {
            var v = JsonValue.NewObject().Set("a", JsonValue.NewArray()).Set("b", JsonValue.NewObject());
            Assert.Equal("{\n  \"a\": [],\n  \"b\": {}\n}", v.ToJson(indented: true));
        }

        [Fact]
        public void TypeMismatchThrowsInsteadOfSilentlyCoercing()
        {
            var v = JsonValue.Parse("{\"a\":\"text\"}");
            Assert.Throws<JsonException>(() => v["a"].AsInt64);
            Assert.Throws<JsonException>(() => v["a"].AsBool);
        }

        [Fact]
        public void RejectsNaNAndInfinity()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => JsonValue.Number(double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => JsonValue.Number(double.PositiveInfinity));
        }
    }
}
