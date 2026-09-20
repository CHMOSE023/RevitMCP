using System;
using System.Globalization;
using Autodesk.Revit.DB;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    /// <summary>
    /// 把字符串写进 Revit 参数。
    ///
    /// 抽出来是因为现在有两个地方要写参数：批量改构件、复制类型时设新值。
    /// **两套写入逻辑迟早会长歪**——一边认 true/false 另一边不认，
    /// 一边按项目单位解释数字另一边按内部单位，这种差异没人查得出来。
    /// </summary>
    internal static class ParameterWriter
    {
        /// <summary>
        /// 写入一个参数值。字符串到各 StorageType 的转换全在这里。
        /// Double 优先走 SetValueString：它按用户的项目单位解释输入，
        /// 模型写 "3000" 得到的就是 3000 毫米，而不是 3000 英尺。
        /// </summary>
        public static void Write(Parameter parameter, string value, Element element)
        {
            var name = parameter.Definition?.Name ?? "(未命名)";

            switch (parameter.StorageType)
            {
                case StorageType.String:
                    if (!parameter.Set(value)) throw Rejected(name, value, element);
                    return;

                case StorageType.Integer:
                    WriteInteger(parameter, name, value, element);
                    return;

                case StorageType.Double:
                    WriteDouble(parameter, name, value, element);
                    return;

                case StorageType.ElementId:
                    WriteElementId(parameter, name, value, element);
                    return;

                default:
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "参数 \"" + name + "\" 的存储类型 " + parameter.StorageType + " 暂不支持写入。");
            }
        }

        /// <summary>
        /// 写入 ElementId 类参数（材质、标高、填充样式这些，值是另一个构件）。
        ///
        /// **空值要单独处理。** -1（<c>ElementId.InvalidElementId</c>）与空字符串是
        /// "把这个参数清空"的正规写法，不是一个找不到的构件——
        /// 拿它去 <c>GetElement</c> 必然返回 null，于是"清空参数"这件事会变成一句
        /// "这个文档里不存在 ID 为 -1 的构件"，而调用方根本无从知道该怎么办。
        /// </summary>
        private static void WriteElementId(Parameter parameter, string name, string value, Element element)
        {
            var text = (value ?? string.Empty).Trim();

            if (text.Length == 0 || text == "-1")
            {
                if (!parameter.Set(ElementId.InvalidElementId))
                    throw new ToolFailureException(McpDomainError.TransactionFailed,
                        "Revit 拒绝清空参数 \"" + name + "\"。这个参数可能是必填的。");
                return;
            }

            // 其余情况两种写法都收，否则"把材质设成 uniqueId"会莫名其妙地失败
            string problem;
            var target = ElementRef.Resolve(element.Document, text, out problem);

            if (target == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "参数 \"" + name + "\" 需要一个构件 ID：" + problem +
                    "（要清空这个参数，传 -1 或空字符串）");

            if (!parameter.Set(target.Id)) throw Rejected(name, value, element);
        }

        private static void WriteDouble(Parameter parameter, string name, string value, Element element)
        {
            // 带单位解释成功就用它——这是用户在 Revit 界面里输入同一个值时得到的结果
            try { if (parameter.SetValueString(value)) return; }
            catch { /* 部分参数不支持 SetValueString，落到下面按内部单位写 */ }

            double number;
            if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "参数 \"" + name + "\" 需要一个数值，但收到 \"" + value + "\"。");

            if (!parameter.Set(number)) throw Rejected(name, value, element);
        }

        /// <summary>
        /// 写入 Integer 参数。Revit 把三类东西都塞进 Integer：真整数、是/否、以及枚举。
        /// 按"语义明确的先走"排序，只有落到最后的文字才去试 SetValueString。
        /// </summary>
        private static void WriteInteger(Parameter parameter, string name, string value, Element element)
        {
            var text = value.Trim();

            // 是/否类参数在 Revit 里是 Integer。模型多半会传 true/false，
            // 让它必须先知道"Revit 用 1/0"属于毫无必要的刁难
            if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "是")
            {
                if (!parameter.Set(1)) throw Rejected(name, value, element);
                return;
            }

            if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) || text == "否")
            {
                if (!parameter.Set(0)) throw Rejected(name, value, element);
                return;
            }

            int number;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                if (!parameter.Set(number)) throw Rejected(name, value, element);
                return;
            }

            // 走到这里说明传的是文字，多半是从 displayValue 照抄来的枚举显示值。
            // 先让 Revit 自己认一次——成本只是一次 try/catch。
            // 实测 Revit 2019 的枚举类 Integer 参数并不认（SetValueString 只对带单位的参数有效），
            // 所以这一步失败是常态，别指望它；留着是因为别的参数或版本上它可能管用
            try { if (parameter.SetValueString(text)) return; }
            catch { /* 不支持按显示值写入，落到下面报错 */ }

            throw new ToolFailureException(McpDomainError.InvalidParameter,
                "参数 \"" + name + "\" 需要一个整数，但收到 \"" + value + "\"。" +
                DescribeIntegerParameter(parameter));
        }

        /// <summary>
        /// 拼一句"它现在是什么、该怎么找到你要的值"。
        ///
        /// Revit 把是/否和枚举都存成 Integer，但这两者该给的建议完全不同：
        /// 是/否只要知道能传 true/false 就够了，给它一整套枚举发现流程纯属干扰。
        /// 而枚举的整数编码没有公开 API 可枚举、Revit 又不接受写入显示文本，
        /// 不给一条可操作的发现路径，模型只会在同一个错误上反复打转。
        /// </summary>
        private static string DescribeIntegerParameter(Parameter parameter)
        {
            string current = null;
            string display = null;

            if (parameter.HasValue)
            {
                try
                {
                    current = parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
                    display = parameter.AsValueString();
                }
                catch { current = null; }
            }

            if (current == null)
                return "是/否类参数传 true/false 或 1/0；枚举类参数传整数，不接受显示文本。";

            var state = "该参数当前值为 " + current +
                (string.IsNullOrEmpty(display) || display == current
                    ? "。"
                    : "（显示为「" + display + "」）。");

            if (LooksLikeYesNo(display))
                return state + "这是是/否类参数，传 true/false 或 1/0。";

            return state + "这是枚举类参数——Revit 只接受整数，不接受显示文本。" +
                   "要知道目标选项对应哪个整数：在 Revit 界面上把任一构件调成该选项，" +
                   "再用 revit_get_element_parameters 读它的 value；或从 0 起逐个试写、读回 displayValue 确认。";
        }

        /// <summary>
        /// 靠显示值认是/否参数，而不是查 <c>Definition.ParameterType</c>——
        /// 后者从 2022 起被标记弃用，为一句提示文案去 Compat 里加一层不划算。
        /// 误判的代价也极小：一个恰好显示「是/否」的真枚举，true/false 本来就写得进去。
        /// </summary>
        private static bool LooksLikeYesNo(string display)
        {
            if (string.IsNullOrEmpty(display)) return false;

            var text = display.Trim();
            return text == "是" || text == "否" ||
                   string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(text, "no", StringComparison.OrdinalIgnoreCase);
        }

        private static ToolFailureException Rejected(string name, string value, Element element)
        {
            return new ToolFailureException(McpDomainError.TransactionFailed,
                "Revit 拒绝把构件 " + element.Id.ToProtocolString() + " 的参数 \"" + name +
                "\" 设为 \"" + value + "\"。值可能超出允许范围，或与该构件的其他约束冲突。一个都没改。");
        }

        public static string DisplayOf(Parameter parameter)
        {
            try
            {
                var display = parameter.AsValueString();
                if (!string.IsNullOrEmpty(display)) return display;
            }
            catch { /* 落到下面按存储类型取原始值 */ }

            switch (parameter.StorageType)
            {
                case StorageType.String: return parameter.AsString();
                case StorageType.Integer: return parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
                case StorageType.Double: return parameter.AsDouble().ToString("R", CultureInfo.InvariantCulture);
                case StorageType.ElementId: return parameter.AsElementId().ToProtocolString();
                default: return null;
            }
        }

    }
}
