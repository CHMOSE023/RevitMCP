using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Compat
{
    /// <summary>
    /// 参数的"数据类型"与"所属分组"在 2022 被整个换掉了：
    /// <c>ParameterType</c> / <c>BuiltInParameterGroup</c> 这两个枚举
    /// 被 <c>ForgeTypeId</c> 体系（<c>SpecTypeId</c> / <c>GroupTypeId</c>）取代，
    /// 旧的构造函数在 2024 已经彻底移除。差异只允许出现在这里。
    ///
    /// 对外一律用小写的短名（text、length、yesNo…）而不是任何一版 Revit 的枚举名：
    /// 把某一版的枚举名暴露到协议里，就等于让调用方去猜它面对的是哪个 Revit。
    /// </summary>
    public static class ParameterDefinitionCompat
    {
        /// <summary>对外支持的参数数据类型。</summary>
        public static readonly string[] DataTypes =
        {
            "text", "multilineText", "integer", "number", "length", "area", "volume",
            "angle", "yesNo", "material", "url"
        };

        /// <summary>对外支持的参数分组（决定它出现在属性面板的哪一栏）。</summary>
        public static readonly string[] Groups =
        {
            "data", "identityData", "text", "geometry", "construction", "reference"
        };

        /// <summary>
        /// 按对外的类型名创建一个共享参数定义的选项对象。
        /// 类型名不认识时抛出说得清的参数错误——悄悄退回 text 会让调用方
        /// 拿到一个"建成了但类型不对"的参数，那比失败更难查。
        /// </summary>
        public static ExternalDefinitionCreationOptions CreateOptions(string dataType, string name)
        {
            var key = (dataType ?? "text").Trim().ToLowerInvariant();

#if REVIT2022_OR_GREATER
            ForgeTypeId spec;
            switch (key)
            {
                case "text": spec = SpecTypeId.String.Text; break;
                case "multilinetext": spec = SpecTypeId.String.MultilineText; break;
                case "integer": spec = SpecTypeId.Int.Integer; break;
                case "number": spec = SpecTypeId.Number; break;
                case "length": spec = SpecTypeId.Length; break;
                case "area": spec = SpecTypeId.Area; break;
                case "volume": spec = SpecTypeId.Volume; break;
                case "angle": spec = SpecTypeId.Angle; break;
                case "yesno": spec = SpecTypeId.Boolean.YesNo; break;
                case "material": spec = SpecTypeId.Reference.Material; break;
                case "url": spec = SpecTypeId.String.Url; break;
                default: throw Unknown("dataType", dataType, DataTypes);
            }

            return new ExternalDefinitionCreationOptions(name, spec);
#else
            ParameterType type;
            switch (key)
            {
                case "text": type = ParameterType.Text; break;
                case "multilinetext": type = ParameterType.MultilineText; break;
                case "integer": type = ParameterType.Integer; break;
                case "number": type = ParameterType.Number; break;
                case "length": type = ParameterType.Length; break;
                case "area": type = ParameterType.Area; break;
                case "volume": type = ParameterType.Volume; break;
                case "angle": type = ParameterType.Angle; break;
                case "yesno": type = ParameterType.YesNo; break;
                case "material": type = ParameterType.Material; break;
                case "url": type = ParameterType.URL; break;
                default: throw Unknown("dataType", dataType, DataTypes);
            }

            return new ExternalDefinitionCreationOptions(name, type);
#endif
        }

        /// <summary>把定义绑定到项目上，并放进指定的属性分组。</summary>
        public static void Bind(Document document, Definition definition, Binding binding, string group)
        {
            var key = (group ?? "data").Trim().ToLowerInvariant();

#if REVIT2022_OR_GREATER
            ForgeTypeId groupId;
            switch (key)
            {
                case "data": groupId = GroupTypeId.Data; break;
                case "identitydata": groupId = GroupTypeId.IdentityData; break;
                case "text": groupId = GroupTypeId.Text; break;
                case "geometry": groupId = GroupTypeId.Geometry; break;
                case "construction": groupId = GroupTypeId.Construction; break;
                case "reference": groupId = GroupTypeId.Reference; break;
                default: throw Unknown("group", group, Groups);
            }

            if (!document.ParameterBindings.Insert(definition, binding, groupId))
                throw Rejected(definition);
#else
            BuiltInParameterGroup groupId;
            switch (key)
            {
                case "data": groupId = BuiltInParameterGroup.PG_DATA; break;
                case "identitydata": groupId = BuiltInParameterGroup.PG_IDENTITY_DATA; break;
                case "text": groupId = BuiltInParameterGroup.PG_TEXT; break;
                case "geometry": groupId = BuiltInParameterGroup.PG_GEOMETRY; break;
                case "construction": groupId = BuiltInParameterGroup.PG_CONSTRUCTION; break;
                case "reference": groupId = BuiltInParameterGroup.PG_REFERENCE; break;
                default: throw Unknown("group", group, Groups);
            }

            if (!document.ParameterBindings.Insert(definition, binding, groupId))
                throw Rejected(definition);
#endif
        }

        /// <summary>读出一个已绑定参数所属的分组，用对外的短名表示。读不出来时返回 null。</summary>
        public static string GroupNameOf(Definition definition)
        {
            try
            {
#if REVIT2022_OR_GREATER
                var id = definition.GetGroupTypeId();
                if (id == null) return null;

                // 反查一遍已知分组；不在列表里的直接给出 Revit 的标识符，
                // 总比返回 null 让调用方以为"没有分组"要好
                if (id == GroupTypeId.Data) return "data";
                if (id == GroupTypeId.IdentityData) return "identityData";
                if (id == GroupTypeId.Text) return "text";
                if (id == GroupTypeId.Geometry) return "geometry";
                if (id == GroupTypeId.Construction) return "construction";
                if (id == GroupTypeId.Reference) return "reference";

                return id.TypeId;
#else
                switch (definition.ParameterGroup)
                {
                    case BuiltInParameterGroup.PG_DATA: return "data";
                    case BuiltInParameterGroup.PG_IDENTITY_DATA: return "identityData";
                    case BuiltInParameterGroup.PG_TEXT: return "text";
                    case BuiltInParameterGroup.PG_GEOMETRY: return "geometry";
                    case BuiltInParameterGroup.PG_CONSTRUCTION: return "construction";
                    case BuiltInParameterGroup.PG_REFERENCE: return "reference";
                    default: return definition.ParameterGroup.ToString();
                }
#endif
            }
            catch { return null; }
        }

        /// <summary>读出一个参数定义的数据类型，用对外的短名表示。认不出来时给 Revit 自己的标识。</summary>
        public static string DataTypeOf(Definition definition)
        {
            try
            {
#if REVIT2022_OR_GREATER
                var spec = definition.GetDataType();
                if (spec == null) return null;

                if (spec == SpecTypeId.String.Text) return "text";
                if (spec == SpecTypeId.String.MultilineText) return "multilineText";
                if (spec == SpecTypeId.Int.Integer) return "integer";
                if (spec == SpecTypeId.Number) return "number";
                if (spec == SpecTypeId.Length) return "length";
                if (spec == SpecTypeId.Area) return "area";
                if (spec == SpecTypeId.Volume) return "volume";
                if (spec == SpecTypeId.Angle) return "angle";
                if (spec == SpecTypeId.Boolean.YesNo) return "yesNo";
                if (spec == SpecTypeId.Reference.Material) return "material";
                if (spec == SpecTypeId.String.Url) return "url";

                return spec.TypeId;
#else
                switch (definition.ParameterType)
                {
                    case ParameterType.Text: return "text";
                    case ParameterType.MultilineText: return "multilineText";
                    case ParameterType.Integer: return "integer";
                    case ParameterType.Number: return "number";
                    case ParameterType.Length: return "length";
                    case ParameterType.Area: return "area";
                    case ParameterType.Volume: return "volume";
                    case ParameterType.Angle: return "angle";
                    case ParameterType.YesNo: return "yesNo";
                    case ParameterType.Material: return "material";
                    case ParameterType.URL: return "url";
                    default: return definition.ParameterType.ToString();
                }
#endif
            }
            catch { return null; }
        }

        private static ToolFailureException Unknown(string field, string value, IEnumerable<string> allowed)
        {
            return new ToolFailureException(McpDomainError.InvalidParameter,
                "无法识别的 " + field + " \"" + value + "\"。可用值：" + string.Join("、", allowed) + "。");
        }

        private static ToolFailureException Rejected(Definition definition)
        {
            return new ToolFailureException(McpDomainError.TransactionFailed,
                "Revit 拒绝把参数「" + definition.Name + "」绑定到项目上。" +
                "同名参数可能已经存在且绑定方式不同（实例 vs 类型）——" +
                "一个参数名在项目里只能有一种绑定方式。");
        }
    }
}
