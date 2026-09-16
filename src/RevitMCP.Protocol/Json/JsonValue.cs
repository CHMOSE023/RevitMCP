using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RevitMCP.Protocol.Json
{
    public enum JsonKind { Null, Bool, Number, String, Array, Object }

    /// <summary>
    /// 极简 JSON DOM。
    ///
    /// 为什么不用 Newtonsoft / System.Text.Json：Revit 把所有插件加载进同一个 AppDomain，
    /// 且不应用插件自身的绑定重定向。Revit 2019–2024 各自捆绑的 Newtonsoft 版本并不一致，
    /// 而 System.Text.Json 会拖进一长串 BCL 包。自带实现是唯一能同时满足
    /// "覆盖六个 Revit 版本" 和 "零程序集冲突风险" 的方案。详见 docs/architecture.md §4。
    ///
    /// 数值以原始字面量保存，保证 round-trip 不会把 1 写成 1.0，
    /// 也不会丢失 JSON-RPC id 中 64 位整数的精度。
    /// </summary>
    public sealed class JsonValue
    {
        private readonly string _text;                              // String 的值 / Number 的字面量
        private readonly bool _bool;
        private readonly List<JsonValue> _items;                    // Array
        private readonly List<string> _keys;                        // Object：保持插入顺序，便于人读
        private readonly Dictionary<string, JsonValue> _members;    // Object：O(1) 查找

        public JsonKind Kind { get; }

        public static readonly JsonValue Null = new JsonValue(JsonKind.Null);
        public static readonly JsonValue True = new JsonValue(true);
        public static readonly JsonValue False = new JsonValue(false);

        private JsonValue(JsonKind kind)
        {
            Kind = kind;
            if (kind == JsonKind.Array) _items = new List<JsonValue>();
            if (kind == JsonKind.Object)
            {
                _keys = new List<string>();
                _members = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            }
        }

        private JsonValue(bool value) { Kind = JsonKind.Bool; _bool = value; }
        private JsonValue(JsonKind kind, string text) { Kind = kind; _text = text; }

        // ---------- 构造 ----------
        public static JsonValue Bool(bool v) => v ? True : False;
        public static JsonValue String(string v) => v == null ? Null : new JsonValue(JsonKind.String, v);
        public static JsonValue Number(long v) => new JsonValue(JsonKind.Number, v.ToString(CultureInfo.InvariantCulture));
        public static JsonValue Number(int v) => Number((long)v);
        public static JsonValue Number(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
                throw new ArgumentOutOfRangeException(nameof(v), "JSON 不支持 NaN / Infinity。");
            return new JsonValue(JsonKind.Number, v.ToString("R", CultureInfo.InvariantCulture));
        }
        internal static JsonValue RawNumber(string literal) => new JsonValue(JsonKind.Number, literal);
        public static JsonValue NewArray() => new JsonValue(JsonKind.Array);
        public static JsonValue NewObject() => new JsonValue(JsonKind.Object);

        public static implicit operator JsonValue(string v) => String(v);
        public static implicit operator JsonValue(long v) => Number(v);
        public static implicit operator JsonValue(int v) => Number(v);
        public static implicit operator JsonValue(double v) => Number(v);
        public static implicit operator JsonValue(bool v) => Bool(v);

        // ---------- 判定 ----------
        public bool IsNull => Kind == JsonKind.Null;
        public bool IsObject => Kind == JsonKind.Object;
        public bool IsArray => Kind == JsonKind.Array;

        // ---------- 取值 ----------
        public string AsString => Kind == JsonKind.String ? _text : throw Expected("string");
        public bool AsBool => Kind == JsonKind.Bool ? _bool : throw Expected("bool");

        public long AsInt64
        {
            get
            {
                if (Kind != JsonKind.Number) throw Expected("number");
                if (long.TryParse(_text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
                // 容忍 1.0 / 1e3 这类写法
                var d = AsDouble;
                if (d < long.MinValue || d > long.MaxValue || d != Math.Floor(d))
                    throw new JsonException($"数值 {_text} 不是整数。");
                return (long)d;
            }
        }

        public double AsDouble => Kind == JsonKind.Number
            ? double.Parse(_text, NumberStyles.Float, CultureInfo.InvariantCulture)
            : throw Expected("number");

        /// <summary>数值的原始字面量，仅用于诊断与 round-trip。</summary>
        public string NumberLiteral => Kind == JsonKind.Number ? _text : null;

        // ---------- 容器 ----------
        public int Count =>
            Kind == JsonKind.Array ? _items.Count :
            Kind == JsonKind.Object ? _keys.Count : 0;

        /// <summary>对象成员访问。键不存在返回 null（注意与 JSON null 区分：后者返回 JsonValue.Null）。</summary>
        public JsonValue this[string key]
        {
            get
            {
                if (Kind != JsonKind.Object) throw Expected("object");
                return _members.TryGetValue(key, out var v) ? v : null;
            }
            set => Set(key, value);
        }

        public JsonValue this[int index]
        {
            get
            {
                if (Kind != JsonKind.Array) throw Expected("array");
                return _items[index];
            }
        }

        public IEnumerable<string> Keys => Kind == JsonKind.Object ? (IEnumerable<string>)_keys : Array.Empty<string>();
        public IEnumerable<JsonValue> Items => Kind == JsonKind.Array ? (IEnumerable<JsonValue>)_items : Array.Empty<JsonValue>();

        public bool ContainsKey(string key) => Kind == JsonKind.Object && _members.ContainsKey(key);

        public bool TryGet(string key, out JsonValue value)
        {
            value = null;
            return Kind == JsonKind.Object && _members.TryGetValue(key, out value);
        }

        /// <summary>链式写法：JsonValue.NewObject().Set("a", 1).Set("b", "x")</summary>
        public JsonValue Set(string key, JsonValue value)
        {
            if (Kind != JsonKind.Object) throw Expected("object");
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (!_members.ContainsKey(key)) _keys.Add(key);
            _members[key] = value ?? Null;
            return this;
        }

        public JsonValue Add(JsonValue value)
        {
            if (Kind != JsonKind.Array) throw Expected("array");
            _items.Add(value ?? Null);
            return this;
        }

        public bool Remove(string key)
        {
            if (Kind != JsonKind.Object) throw Expected("object");
            if (!_members.Remove(key)) return false;
            _keys.Remove(key);
            return true;
        }

        // ---------- 序列化 ----------
        public static JsonValue Parse(string text) => JsonParser.Parse(text);
        public string ToJson(bool indented = false) => JsonWriter.Write(this, indented);
        public override string ToString() => ToJson();

        private JsonException Expected(string expected) =>
            new JsonException($"期望 {expected}，实际为 {Kind}。");
    }

    public sealed class JsonException : Exception
    {
        public JsonException(string message) : base(message) { }
        public JsonException(string message, Exception inner) : base(message, inner) { }
    }
}
