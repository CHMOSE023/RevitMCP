using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using RevitMCP.Protocol.Json;

namespace RevitMCP.Tooling.Schema
{
    /// <summary>入参绑定失败。消息直接面向模型，必须说清楚哪个字段、错在哪。</summary>
    public sealed class ToolInputException : Exception
    {
        public ToolInputException(string message) : base(message) { }
    }

    /// <summary>
    /// DTO 与 JSON 之间的双向映射。
    ///
    /// 绑定（JSON → DTO）刻意严格：多余字段、类型不符一律报错而不是静默忽略。
    /// 模型拿到明确的错误才能自我纠正；静默忽略会让它以为参数生效了，
    /// 然后对着一个错误的结果继续推理。
    /// </summary>
    public static class JsonMapper
    {
        // ---------- JSON → DTO ----------

        public static object Bind(JsonValue json, Type targetType)
        {
            if (targetType == null) throw new ArgumentNullException(nameof(targetType));
            json = json ?? JsonValue.NewObject();

            if (!json.IsObject)
                throw new ToolInputException("arguments 必须是 JSON 对象。");

            return BindObject(json, targetType, string.Empty);
        }

        private static object BindObject(JsonValue json, Type type, string path)
        {
            object instance;
            try
            {
                instance = Activator.CreateInstance(type);
            }
            catch (Exception ex)
            {
                throw new ToolInputException(type.Name + " 缺少公共无参构造函数：" + ex.Message);
            }

            var properties = TypeIntrospection.MappableProperties(type)
                .Where(p => p.CanWrite)
                .ToArray();

            var known = new HashSet<string>(
                properties.Select(p => TypeIntrospection.ToJsonName(p.Name)), StringComparer.Ordinal);

            // 未知字段必须先于必填检查：字段拼错时两种错误会同时出现，
            // 而"你写的 nmaeContains 不认识，可用的是 nameContains"比"缺少 nameContains"
            // 更能让模型一次改对。静默忽略则最糟——模型会一直以为自己传对了。
            foreach (var key in json.Keys)
            {
                if (known.Contains(key)) continue;
                if (key == "_meta") continue;   // 协议元数据，不属于工具入参
                throw new ToolInputException(
                    "未知参数 " + (string.IsNullOrEmpty(path) ? key : path + "." + key) +
                    "。可用参数：" + string.Join("、", ToArray(known)) + "。");
            }

            foreach (var property in properties)
            {
                var name = TypeIntrospection.ToJsonName(property.Name);
                var fieldPath = string.IsNullOrEmpty(path) ? name : path + "." + name;

                if (!json.TryGet(name, out var value) || value.IsNull)
                {
                    if (TypeIntrospection.IsRequired(property))
                        throw new ToolInputException("缺少必填参数 " + fieldPath + "。");
                    continue;
                }

                property.SetValue(instance, BindValue(value, property.PropertyType, fieldPath));
            }

            return instance;
        }

        private static object BindValue(JsonValue value, Type type, string path)
        {
            var actual = TypeIntrospection.UnwrapNullable(type);

            if (actual == typeof(JsonValue)) return value;

            if (actual == typeof(string))
                return Expect(value, JsonKind.String, path, "字符串").AsString;

            if (actual == typeof(bool))
                return Expect(value, JsonKind.Bool, path, "布尔值").AsBool;

            if (actual.IsEnum)
            {
                var text = Expect(value, JsonKind.String, path, "字符串").AsString;
                try
                {
                    return Enum.Parse(actual, text, ignoreCase: true);
                }
                catch (ArgumentException)
                {
                    throw new ToolInputException(
                        path + " 的值 \"" + text + "\" 无效。可选值：" + string.Join("、", Enum.GetNames(actual)) + "。");
                }
            }

            if (IsIntegerType(actual))
            {
                var number = Expect(value, JsonKind.Number, path, "整数");
                long raw;
                try { raw = number.AsInt64; }
                catch (JsonException ex) { throw new ToolInputException(path + " 必须是整数：" + ex.Message); }

                try { return Convert.ChangeType(raw, actual, CultureInfo.InvariantCulture); }
                catch (OverflowException) { throw new ToolInputException(path + " 的数值超出 " + actual.Name + " 范围。"); }
            }

            if (actual == typeof(float) || actual == typeof(double) || actual == typeof(decimal))
            {
                var number = Expect(value, JsonKind.Number, path, "数字");
                return Convert.ChangeType(number.AsDouble, actual, CultureInfo.InvariantCulture);
            }

            if (actual == typeof(DateTime))
            {
                var text = Expect(value, JsonKind.String, path, "ISO 8601 时间字符串").AsString;
                if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                    throw new ToolInputException(path + " 不是合法的时间字符串。");
                return parsed;
            }

            if (actual == typeof(Guid))
            {
                var text = Expect(value, JsonKind.String, path, "GUID 字符串").AsString;
                if (!Guid.TryParse(text, out var guid))
                    throw new ToolInputException(path + " 不是合法的 GUID。");
                return guid;
            }

            if (TypeIntrospection.IsCollection(actual, out var elementType))
            {
                if (!value.IsArray) throw new ToolInputException(path + " 必须是数组。");

                var listType = typeof(List<>).MakeGenericType(elementType);
                var list = (IList)Activator.CreateInstance(listType);

                var index = 0;
                foreach (var item in value.Items)
                    list.Add(BindValue(item, elementType, path + "[" + index++ + "]"));

                if (actual.IsArray)
                {
                    var array = Array.CreateInstance(elementType, list.Count);
                    list.CopyTo(array, 0);
                    return array;
                }
                return list;
            }

            if (actual.IsClass)
            {
                if (!value.IsObject) throw new ToolInputException(path + " 必须是对象。");
                return BindObject(value, actual, path);
            }

            throw new ToolInputException(path + " 使用了不支持的类型 " + actual.Name + "。");
        }

        private static JsonValue Expect(JsonValue value, JsonKind kind, string path, string expected)
        {
            if (value.Kind != kind)
                throw new ToolInputException(path + " 必须是" + expected + "，实际收到 " + Describe(value.Kind) + "。");
            return value;
        }

        private static string Describe(JsonKind kind)
        {
            switch (kind)
            {
                case JsonKind.Null: return "null";
                case JsonKind.Bool: return "布尔值";
                case JsonKind.Number: return "数字";
                case JsonKind.String: return "字符串";
                case JsonKind.Array: return "数组";
                default: return "对象";
            }
        }

        private static bool IsIntegerType(Type type) =>
            type == typeof(byte) || type == typeof(sbyte) ||
            type == typeof(short) || type == typeof(ushort) ||
            type == typeof(int) || type == typeof(uint) ||
            type == typeof(long) || type == typeof(ulong);

        private static string[] ToArray(HashSet<string> values)
        {
            var result = new string[values.Count];
            values.CopyTo(result);
            Array.Sort(result, StringComparer.Ordinal);
            return result;
        }

        // ---------- DTO → JSON ----------

        public static JsonValue ToJson(object value)
        {
            if (value == null) return JsonValue.Null;

            if (value is JsonValue json) return json;

            var type = value.GetType();

            if (value is string s) return JsonValue.String(s);
            if (value is bool b) return JsonValue.Bool(b);
            if (value is Guid guid) return JsonValue.String(guid.ToString());
            if (value is DateTime time) return JsonValue.String(time.ToString("o", CultureInfo.InvariantCulture));
            if (type.IsEnum) return JsonValue.String(value.ToString());

            if (value is float f) return JsonValue.Number(f);
            if (value is double d) return JsonValue.Number(d);
            if (value is decimal m) return JsonValue.Number((double)m);
            if (IsIntegerType(type)) return JsonValue.Number(Convert.ToInt64(value, CultureInfo.InvariantCulture));

            if (value is IDictionary dictionary)
            {
                var obj = JsonValue.NewObject();
                foreach (DictionaryEntry entry in dictionary)
                    obj.Set(Convert.ToString(entry.Key, CultureInfo.InvariantCulture), ToJson(entry.Value));
                return obj;
            }

            if (value is IEnumerable enumerable)
            {
                var array = JsonValue.NewArray();
                foreach (var item in enumerable) array.Add(ToJson(item));
                return array;
            }

            var result = JsonValue.NewObject();
            foreach (var property in TypeIntrospection.MappableProperties(type))
            {
                object propertyValue;
                try { propertyValue = property.GetValue(value); }
                catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }

                result.Set(TypeIntrospection.ToJsonName(property.Name), ToJson(propertyValue));
            }
            return result;
        }
    }
}
