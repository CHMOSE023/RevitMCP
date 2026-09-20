using RevitMCP.Protocol.Json;
using Xunit;

namespace RevitMCP.Protocol.Tests
{
    /// <summary>
    /// 规范化序列化。它只有一个用途——给幂等键算指纹，
    /// 所以判据只有一条：**同样的内容必须得到同样的字符串，不管 key 是什么顺序进来的**。
    /// 这一条不成立，客户端重试时幂等就会失效（多数语言的字典不保证顺序）。
    /// </summary>
    public class JsonCanonicalTests
    {
        [Fact]
        public void KeyOrderDoesNotMatter()
        {
            var a = JsonValue.Parse("{\"b\":1,\"a\":2}");
            var b = JsonValue.Parse("{\"a\":2,\"b\":1}");

            Assert.Equal(JsonCanonical.Write(a), JsonCanonical.Write(b));
            Assert.Equal("{\"a\":2,\"b\":1}", JsonCanonical.Write(a));
        }

        [Fact]
        public void NestedObjectsAreSortedToo()
        {
            var a = JsonValue.Parse("{\"x\":{\"q\":1,\"p\":2},\"w\":3}");
            var b = JsonValue.Parse("{\"w\":3,\"x\":{\"p\":2,\"q\":1}}");

            Assert.Equal(JsonCanonical.Write(a), JsonCanonical.Write(b));
        }

        [Fact]
        public void ArrayOrderIsPreserved()
        {
            // 数组顺序是内容的一部分：[1,2] 和 [2,1] 是不同的参数，
            // 墙的定位线方向就靠它
            var a = JsonValue.Parse("{\"a\":[1,2]}");
            var b = JsonValue.Parse("{\"a\":[2,1]}");

            Assert.NotEqual(JsonCanonical.Write(a), JsonCanonical.Write(b));
        }

        [Fact]
        public void DifferentContentGivesDifferentFingerprint()
        {
            var one = JsonCanonical.Fingerprint("tool", "k1", "doc", "{\"a\":1}");
            var two = JsonCanonical.Fingerprint("tool", "k1", "doc", "{\"a\":2}");

            Assert.NotEqual(one, two);
        }

        [Fact]
        public void SameContentGivesSameFingerprint()
        {
            var one = JsonCanonical.Fingerprint("tool", "k1", "doc", "{\"a\":1}");
            var two = JsonCanonical.Fingerprint("tool", "k1", "doc", "{\"a\":1}");

            Assert.Equal(one, two);
        }

        [Fact]
        public void TheDocumentIsPartOfTheIdentity()
        {
            // 同一批参数打到另一个文档上是**另一次操作**，结果不能复用
            var a = JsonCanonical.Fingerprint("tool", "k1", "docA", "{}");
            var b = JsonCanonical.Fingerprint("tool", "k1", "docB", "{}");

            Assert.NotEqual(a, b);
        }

        [Fact]
        public void FingerprintIsUrlSafeAndShort()
        {
            var fingerprint = JsonCanonical.Fingerprint("tool", "k1", "doc", "{}");

            Assert.DoesNotContain("+", fingerprint);
            Assert.DoesNotContain("/", fingerprint);
            Assert.DoesNotContain("=", fingerprint);
            Assert.True(fingerprint.Length <= 24, "指纹要短到能放进报错文本里：" + fingerprint);
        }

        [Fact]
        public void NullAndEmptyPartsAreTheSameThing()
        {
            // 指纹里 null 与空串等价——这不是疏忽：管线在更早的地方就用
            // IsNullOrWhiteSpace 把两者都当成"没给 requestKey"，
            // 到这一步它们已经不可能表示不同的意图了
            Assert.Equal(
                JsonCanonical.Fingerprint("tool", null, "doc", "{}"),
                JsonCanonical.Fingerprint("tool", "", "doc", "{}"));
        }

        [Fact]
        public void PartsDoNotBleedIntoEachOther()
        {
            // 用分隔符拼接，而不是简单相连：否则 ("ab","c") 和 ("a","bc") 会撞成同一个指纹
            Assert.NotEqual(
                JsonCanonical.Fingerprint("ab", "c", "doc", "{}"),
                JsonCanonical.Fingerprint("a", "bc", "doc", "{}"));
        }
    }
}
