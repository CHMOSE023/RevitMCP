using System;
using System.Collections.Generic;
using System.Reflection;
using RevitMCP.Protocol.Json;

namespace RevitMCP.Tooling.Schema
{
    /// <summary>
    /// 从 Input DTO 反射生成 JSON Schema，供 tools/list 使用。
    ///
    /// 不引入 NJsonSchema 之类的库：任何新依赖在 Revit 单 AppDomain 下都是版本冲突风险
    /// （见 docs/architecture.md §2-C2）。这里只覆盖工具入参真正会用到的类型。
    /// </summary>
    public static class SchemaGenerator
    {
        private const int MaxDepth = 8;

        /// <summary>
        /// 所有写工具都收的前置条件参数。不写进每个 Input DTO，
        /// 是因为它不属于任何一个工具的业务——它是"这次写入允许作用在谁身上"，
        /// 由管线在编组到主线程之后、执行之前统一校验。
        /// </summary>
        public const string ExpectedDocumentParameter = "expectedDocumentId";

        public static JsonValue Generate(Type inputType)
        {
            if (inputType == null) throw new ArgumentNullException(nameof(inputType));
            return BuildObjectSchema(inputType, 0, new HashSet<Type>());
        }

        /// <summary>
        /// 给写工具的 schema 补上 <see cref="ExpectedDocumentParameter"/>。
        ///
        /// 写操作永远作用在**当前活动文档**上，而活动文档会在两次调用之间变：
        /// 用户点了另一个窗口、上一步的另存把当前文件换掉了。
        /// 带上这个参数，就能把"我以为在改哪个文档"表达出来，由服务端在真正动手前拦下来。
        /// </summary>
        public static JsonValue WithExpectedDocument(JsonValue schema)
        {
            if (schema == null || !schema.IsObject) return schema;

            JsonValue properties;
            if (!schema.TryGet("properties", out properties) || !properties.IsObject)
            {
                properties = JsonValue.NewObject();
                schema.Set("properties", properties);
            }

            // 工具自己已经声明过同名参数就别覆盖它——那是工具的业务参数
            if (properties.TryGet(ExpectedDocumentParameter, out _)) return schema;

            properties.Set(ExpectedDocumentParameter, JsonValue.NewObject()
                .Set("type", "string")
                .Set("description",
                    "前置条件：本次写入**必须**作用在这个文档上，来自 revit_list_documents 的 id " +
                    "（或 revit_get_document_info 的 pathName）。活动文档不是它就直接拒绝，模型不会被改动。" +
                    "用户随时可能在 Revit 里切换文档，另存也会把活动文档换成新文件——" +
                    "凡是拿着上一步查到的构件 ID 做的写入，都该带上它"));

            // 之前声明过"只接受空对象"的无参工具，现在多了这一个参数
            schema.Remove("additionalProperties");

            return schema;
        }

        private static JsonValue BuildObjectSchema(Type type, int depth, HashSet<Type> path)
        {
            var schema = JsonValue.NewObject().Set("type", "object");

            // 自引用类型（树形结构）会无限展开——JSON Schema 的 $ref 我们不支持，直接截断
            if (depth >= MaxDepth || path.Contains(type))
            {
                schema.Set("description", "（嵌套层级过深，已省略细节）");
                return schema;
            }

            path.Add(type);
            try
            {
                var properties = JsonValue.NewObject();
                var required = JsonValue.NewArray();
                var count = 0;

                foreach (var property in TypeIntrospection.MappableProperties(type))
                {
                    if (!property.CanWrite) continue;   // 入参必须可写才能绑定

                    var name = TypeIntrospection.ToJsonName(property.Name);
                    properties.Set(name, BuildProperty(property, depth, path));
                    if (TypeIntrospection.IsRequired(property)) required.Add(name);
                    count++;
                }

                schema.Set("properties", properties);
                if (required.Count > 0) schema.Set("required", required);

                // 无参工具：显式只接受空对象，规范推荐这种写法
                if (count == 0) schema.Set("additionalProperties", false);

                return schema;
            }
            finally
            {
                path.Remove(type);
            }
        }

        private static JsonValue BuildProperty(PropertyInfo property, int depth, HashSet<Type> path)
        {
            var schema = BuildType(property.PropertyType, depth, path);

            var attribute = property.GetCustomAttribute<McpParamAttribute>();
            if (attribute == null) return schema;

            if (!string.IsNullOrEmpty(attribute.Description))
                schema.Set("description", attribute.Description);

            ApplyAllowedValues(schema, attribute, property);

            return schema;
        }

        /// <summary>
        /// 把 <see cref="McpParamAttribute.AllowedValues"/> 落成 JSON Schema 的 <c>enum</c>。
        ///
        /// 数组属性的取值约束要挂到 <c>items</c> 上而不是数组本身——
        /// 挂错地方的 schema 不会报错，只会悄悄失去约束力。
        /// </summary>
        private static void ApplyAllowedValues(
            JsonValue schema, McpParamAttribute attribute, PropertyInfo property)
        {
            var allowed = attribute.AllowedValues;
            if (allowed == null || allowed.Length == 0) return;

            var values = JsonValue.NewArray();
            foreach (var value in allowed) values.Add(JsonValue.String(value));

            var target = schema;

            var actual = TypeIntrospection.UnwrapNullable(property.PropertyType);
            if (TypeIntrospection.IsCollection(actual, out _))
            {
                var items = schema["items"];

                // items 一定存在（数组分支刚建的）。取不到说明 BuildType 改了结构，
                // 这时宁可不加约束，也不能把 enum 挂到数组上去
                if (items == null || !items.IsObject) return;

                target = items;
            }

            target.Set("enum", values);
        }

        private static JsonValue BuildType(Type type, int depth, HashSet<Type> path)
        {
            var actual = TypeIntrospection.UnwrapNullable(type);

            if (actual == typeof(string) || actual == typeof(Guid))
                return JsonValue.NewObject().Set("type", "string");

            if (actual == typeof(bool))
                return JsonValue.NewObject().Set("type", "boolean");

            if (actual == typeof(byte) || actual == typeof(sbyte) ||
                actual == typeof(short) || actual == typeof(ushort) ||
                actual == typeof(int) || actual == typeof(uint) ||
                actual == typeof(long) || actual == typeof(ulong))
                return JsonValue.NewObject().Set("type", "integer");

            if (actual == typeof(float) || actual == typeof(double) || actual == typeof(decimal))
                return JsonValue.NewObject().Set("type", "number");

            if (actual == typeof(DateTime))
                return JsonValue.NewObject().Set("type", "string").Set("format", "date-time");

            if (actual.IsEnum)
            {
                // 枚举以字符串传递：数字值对模型毫无意义，也经不起枚举顺序调整
                var values = JsonValue.NewArray();
                foreach (var name in Enum.GetNames(actual)) values.Add(name);
                return JsonValue.NewObject().Set("type", "string").Set("enum", values);
            }

            if (TypeIntrospection.IsCollection(actual, out var elementType))
                return JsonValue.NewObject()
                    .Set("type", "array")
                    .Set("items", BuildType(elementType, depth + 1, path));

            if (actual == typeof(JsonValue))
                return JsonValue.NewObject();   // 任意 JSON，不加约束

            if (actual.IsClass)
                return BuildObjectSchema(actual, depth + 1, path);

            throw new NotSupportedException(
                "工具入参不支持类型 " + actual.FullName + "。请改用基础类型、枚举、List<T> 或嵌套 DTO。");
        }
    }
}
