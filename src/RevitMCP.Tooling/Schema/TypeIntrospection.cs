using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace RevitMCP.Tooling.Schema
{
    /// <summary>
    /// Schema 生成、入参绑定、出参序列化三者共用的类型判定。
    /// 集中在一处，免得三个地方对"什么算可空""什么算集合"给出不一致的答案——
    /// 那种不一致会表现为"Schema 说可选、绑定却报必填"这类难查的问题。
    /// </summary>
    internal static class TypeIntrospection
    {
        public static Type UnwrapNullable(Type type) =>
            Nullable.GetUnderlyingType(type) ?? type;

        public static bool IsNullable(Type type) =>
            !type.IsValueType || Nullable.GetUnderlyingType(type) != null;

        /// <summary>
        /// 必填判定，全框架唯一定义：
        /// 显式标注 Required，或者是不可空的值类型（int 必填，int? 可选）。
        /// </summary>
        public static bool IsRequired(PropertyInfo property)
        {
            var attribute = property.GetCustomAttribute<McpParamAttribute>();
            if (attribute != null && attribute.Required) return true;
            return property.PropertyType.IsValueType &&
                   Nullable.GetUnderlyingType(property.PropertyType) == null;
        }

        public static bool IsCollection(Type type, out Type elementType)
        {
            elementType = null;
            if (type == typeof(string)) return false;

            if (type.IsArray)
            {
                elementType = type.GetElementType();
                return true;
            }

            if (type.IsGenericType)
            {
                var definition = type.GetGenericTypeDefinition();
                if (definition == typeof(List<>) || definition == typeof(IList<>) ||
                    definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyList<>) ||
                    definition == typeof(ICollection<>))
                {
                    elementType = type.GetGenericArguments()[0];
                    return true;
                }
            }

            // 非泛型 IEnumerable 无法推断元素类型，当作不支持而不是猜成 object
            return false;
        }

        public static bool IsSimple(Type type)
        {
            var actual = UnwrapNullable(type);
            return actual.IsPrimitive
                   || actual.IsEnum
                   || actual == typeof(string)
                   || actual == typeof(decimal)
                   || actual == typeof(DateTime)
                   || actual == typeof(Guid);
        }

        /// <summary>可参与映射的公共读写属性，按声明顺序稳定排列。</summary>
        public static PropertyInfo[] MappableProperties(Type type) =>
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0)
                .Where(p => p.CanRead)
                .Where(p => p.GetCustomAttribute<McpIgnoreAttribute>() == null)
                .OrderBy(p => p.MetadataToken)   // 保持声明顺序，让 Schema 与输出可读且稳定
                .ToArray();

        /// <summary>DTO 属性名 → 协议字段名。C# 用 PascalCase，JSON 惯例是 camelCase。</summary>
        public static string ToJsonName(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName)) return propertyName;
            if (propertyName.Length == 1) return propertyName.ToLowerInvariant();
            if (char.IsLower(propertyName[0])) return propertyName;
            return char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1);
        }
    }

    /// <summary>标注后该属性不出现在 Schema、入参绑定与出参序列化中。</summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = false)]
    public sealed class McpIgnoreAttribute : Attribute
    {
    }
}
