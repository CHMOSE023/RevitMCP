using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    // ==================== 批量设置参数 ====================

    public sealed class SetParametersInput
    {
        [McpParam("要修改的构件 ID 列表（字符串形式，来自 revit_query_elements）", Required = true)]
        public List<string> ElementIds { get; set; }

        [McpParam("参数名，须与 revit_get_element_parameters 返回的名称完全一致", Required = true)]
        public string ParameterName { get; set; }

        [McpParam("要写入的值，一律用字符串传递。数值按项目显示单位解释（如长度 \"3000\" 即 3000 毫米）；" +
                  "是/否类参数可用 true/false 或 是/否；ElementId 类参数传目标构件的 ID", Required = true)]
        public string Value { get; set; }

        [McpParam("影响构件数超过上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    public sealed class ParameterChange
    {
        [McpParam("构件 ID")]
        public string Id { get; set; }

        [McpParam("构件名称")]
        public string Name { get; set; }

        [McpParam("修改前的显示值")]
        public string OldValue { get; set; }

        [McpParam("修改后的显示值")]
        public string NewValue { get; set; }
    }

    public sealed class SetParametersOutput : IReportsAffectedElements
    {
        [McpParam("被修改的参数名")]
        public string ParameterName { get; set; }

        [McpParam("成功修改的构件数")]
        public int Changed { get; set; }

        [McpParam("每个构件的新旧值")]
        public List<ParameterChange> Elements { get; set; } = new List<ParameterChange>();

        // 显式实现：不是公共属性，所以不会被序列化成多出来的一个输出字段
        int IReportsAffectedElements.AffectedElements => Changed;
    }

    [McpTool("revit_set_element_parameters",
        Title = "批量设置构件参数",
        Description = "把一批构件的同一个参数设成同一个值。" +
                      "先用 revit_get_element_parameters 确认参数名与当前值、确认参数不是只读的，再调用本工具。" +
                      "全部构件要么一起改成功、要么一个都不改（单个事务，用户可一步撤销）。",
        TimeoutSeconds = 120)]
    public sealed class SetParametersTool : RevitTool<SetParametersInput, SetParametersOutput>
    {
        public override SetParametersOutput Execute(
            SetParametersInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);

            if (input.ElementIds == null || input.ElementIds.Count == 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter, "elementIds 不能为空。");

            if (string.IsNullOrWhiteSpace(input.ParameterName))
                throw new ToolFailureException(McpDomainError.InvalidParameter, "parameterName 不能为空。");

            if (input.Value == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter, "value 不能为空。");

            GuardScale(input.ElementIds.Count, input.Confirm, context, "修改");

            // 先把所有构件和目标参数解析出来并校验一遍，再动手写。
            // 事务虽然能回滚，但"先全部检查通过再改"能让失败信息一次说全，
            // 而不是改到第三个才发现第七个的参数是只读的。
            var targets = Resolve(document, input, context);

            var output = new SetParametersOutput { ParameterName = input.ParameterName };
            var total = targets.Count;
            var done = 0;

            foreach (var target in targets)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var before = ParameterWriter.DisplayOf(target.Parameter);
                ParameterWriter.Write(target.Parameter, input.Value, target.Element);

                output.Elements.Add(new ParameterChange
                {
                    Id = target.Element.Id.ToProtocolString(),
                    Name = SafeName(target.Element),
                    OldValue = before,
                    NewValue = ParameterWriter.DisplayOf(target.Parameter)
                });

                done++;
                ProgressTicker.Tick(context.Progress, done, total, "已修改");
            }

            output.Changed = output.Elements.Count;
            return output;
        }

        private sealed class Target
        {
            public Element Element;
            public Parameter Parameter;
        }

        private static List<Target> Resolve(
            Document document, SetParametersInput input, ToolExecutionContext<UIApplication> context)
        {
            var targets = new List<Target>(input.ElementIds.Count);
            var missing = new List<string>();
            var readOnly = new List<string>();
            var viaType = new List<string>();

            foreach (var rawId in input.ElementIds)
            {
                var element = RequireElement(document, rawId);
                bool onType;
                var parameter = FindParameter(element, input.ParameterName, out onType);

                if (parameter == null) { missing.Add(rawId); continue; }
                if (parameter.IsReadOnly) { readOnly.Add(rawId); continue; }

                if (onType) viaType.Add(rawId);
                targets.Add(new Target { Element = element, Parameter = parameter });
            }

            if (viaType.Count > 0)
                context.Warnings.Add(
                    "参数 \"" + input.ParameterName + "\" 在这些构件上位于族类型而非实例：" + Join(viaType) +
                    "。改类型参数会影响模型里所有使用该类型的构件，不只是你点名的这几个。");

            if (missing.Count > 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "以下构件上没有名为 \"" + input.ParameterName + "\" 的参数：" + Join(missing) +
                    "。一个都没改。可用 revit_get_element_parameters 查看这些构件实际有哪些参数。");

            if (readOnly.Count > 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "参数 \"" + input.ParameterName + "\" 在以下构件上是只读的：" + Join(readOnly) +
                    "。一个都没改。只读参数通常由 Revit 依几何或其他参数算出，只能间接改变。");

            return targets;
        }

        /// <summary>
        /// 按名字找参数。优先实例参数，找不到再找类型参数——
        /// 改类型参数会波及所有同类型构件，所以只在实例上确实没有时才退到那一步。
        /// </summary>
        private static Parameter FindParameter(Element element, string name, out bool onType)
        {
            onType = false;

            var parameter = LookupByName(element, name);
            if (parameter != null) return parameter;

            var typeId = element.GetTypeId();
            if (typeId == null || typeId == ElementId.InvalidElementId) return null;

            var type = element.Document.GetElement(typeId);
            if (type == null) return null;

            var typeParameter = LookupByName(type, name);
            onType = typeParameter != null;
            return typeParameter;
        }

        private static Parameter LookupByName(Element element, string name)
        {
            foreach (Parameter parameter in element.Parameters)
            {
                var definition = parameter.Definition;
                if (definition != null &&
                    string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase))
                    return parameter;
            }
            return null;
        }

        private static string SafeName(Element element)
        {
            try { return element.Name; }
            catch { return null; }
        }

        private static string Join(IEnumerable<string> ids)
        {
            return string.Join("、", ids.Take(10).ToArray());
        }
    }
}
