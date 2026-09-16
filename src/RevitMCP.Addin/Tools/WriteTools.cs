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

    public sealed class SetParametersOutput
    {
        [McpParam("被修改的参数名")]
        public string ParameterName { get; set; }

        [McpParam("成功修改的构件数")]
        public int Changed { get; set; }

        [McpParam("每个构件的新旧值")]
        public List<ParameterChange> Elements { get; set; } = new List<ParameterChange>();
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

            foreach (var target in targets)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var before = DisplayOf(target.Parameter);
                Write(target.Parameter, input.Value, target.Element);

                output.Elements.Add(new ParameterChange
                {
                    Id = target.Element.Id.ToProtocolString(),
                    Name = SafeName(target.Element),
                    OldValue = before,
                    NewValue = DisplayOf(target.Parameter)
                });
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

        /// <summary>
        /// 写入一个参数值。字符串到各 StorageType 的转换全在这里。
        /// Double 优先走 SetValueString：它按用户的项目单位解释输入，
        /// 模型写 "3000" 得到的就是 3000 毫米，而不是 3000 英尺。
        /// </summary>
        private static void Write(Parameter parameter, string value, Element element)
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
                    ElementId id;
                    if (!ElementIdCompat.TryParse(value.Trim(), out id))
                        throw new ToolFailureException(McpDomainError.InvalidParameter,
                            "参数 \"" + name + "\" 需要一个构件 ID，但收到 \"" + value + "\"。");
                    if (!parameter.Set(id)) throw Rejected(name, value, element);
                    return;

                default:
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "参数 \"" + name + "\" 的存储类型 " + parameter.StorageType + " 暂不支持写入。");
            }
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

        private static string DisplayOf(Parameter parameter)
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

    // ==================== 创建墙 ====================

    public sealed class CreateWallInput
    {
        [McpParam("起点 X，毫米", Required = true)]
        public double StartX { get; set; }

        [McpParam("起点 Y，毫米", Required = true)]
        public double StartY { get; set; }

        [McpParam("终点 X，毫米", Required = true)]
        public double EndX { get; set; }

        [McpParam("终点 Y，毫米", Required = true)]
        public double EndY { get; set; }

        [McpParam("墙高，毫米。默认 3000")]
        public double? Height { get; set; }

        [McpParam("底标高的构件 ID。省略时用活动视图所在标高，再不行取最低标高")]
        public string LevelId { get; set; }

        [McpParam("墙类型的构件 ID。省略时用项目的默认墙类型")]
        public string WallTypeId { get; set; }

        [McpParam("是否为结构墙，默认 false")]
        public bool? Structural { get; set; }
    }

    public sealed class CreateWallOutput
    {
        [McpParam("新建墙的构件 ID")]
        public string Id { get; set; }

        [McpParam("使用的墙类型名")]
        public string WallType { get; set; }

        [McpParam("所在标高名")]
        public string Level { get; set; }

        [McpParam("墙长，毫米")]
        public double LengthMm { get; set; }

        [McpParam("墙高，毫米")]
        public double HeightMm { get; set; }
    }

    [McpTool("revit_create_wall",
        Title = "创建墙",
        Description = "在指定标高上按起止点创建一面直墙。坐标和尺寸一律用毫米，原点与项目坐标系一致。" +
                      "省略标高或墙类型时会选用合理的默认值，并在 warnings 中说明选了哪个。" +
                      "创建后可用 revit_query_elements 查 OST_Walls 复查。",
        TimeoutSeconds = 60)]
    public sealed class CreateWallTool : RevitTool<CreateWallInput, CreateWallOutput>
    {
        /// <summary>1 英尺 = 304.8 毫米，是精确定义值。硬编码它以绕开 UnitUtils 在 2021 前后的 API 断层。</summary>
        private const double MmPerFoot = 304.8;

        /// <summary>Revit 拒绝短于约 1/32 英尺的曲线。提前拦下，给出比 Revit 原生报错清楚得多的信息。</summary>
        private const double MinLengthMm = 1.0;

        private const double DefaultHeightMm = 3000.0;

        public override CreateWallOutput Execute(CreateWallInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);

            var heightMm = input.Height ?? DefaultHeightMm;
            if (heightMm <= MinLengthMm)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "墙高必须为正且不小于 " + MinLengthMm + " 毫米，收到 " + heightMm + "。");

            var lengthMm = Math.Sqrt(
                Math.Pow(input.EndX - input.StartX, 2) + Math.Pow(input.EndY - input.StartY, 2));

            if (lengthMm < MinLengthMm)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "起点和终点相距 " + lengthMm.ToString("0.###", CultureInfo.InvariantCulture) +
                    " 毫米，太短，Revit 无法创建墙。两点至少相距 " + MinLengthMm + " 毫米。");

            var start = new XYZ(ToFeet(input.StartX), ToFeet(input.StartY), 0);
            var end = new XYZ(ToFeet(input.EndX), ToFeet(input.EndY), 0);

            var level = ResolveLevel(document, context, input.LevelId);
            var wallType = ResolveWallType(document, context, input.WallTypeId);

            var wall = Wall.Create(
                document,
                Line.CreateBound(start, end),
                wallType.Id,
                level.Id,
                ToFeet(heightMm),
                0,      // offset
                false,  // flip
                input.Structural ?? false);

            if (wall == null)
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "Revit 未能创建墙，但也没有报错。请检查标高与墙类型是否匹配。");

            return new CreateWallOutput
            {
                Id = wall.Id.ToProtocolString(),
                WallType = wallType.Name,
                Level = level.Name,
                LengthMm = Math.Round(lengthMm, 3),
                HeightMm = Math.Round(heightMm, 3)
            };
        }

        private static Level ResolveLevel(
            Document document, ToolExecutionContext<UIApplication> context, string rawId)
        {
            if (!string.IsNullOrWhiteSpace(rawId))
            {
                var element = RequireElement(document, rawId);
                var explicitLevel = element as Level;
                if (explicitLevel == null)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "构件 " + rawId + " 不是标高，而是「" + (element.Category?.Name ?? element.GetType().Name) +
                        "」。可用 revit_query_elements 查 OST_Levels 取得标高 ID。");
                return explicitLevel;
            }

            // 活动视图所在的标高最接近用户此刻在看的位置，优先用它
            var viewLevel = document.ActiveView == null ? null : document.ActiveView.GenLevel;
            if (viewLevel != null)
            {
                context.Warnings.Add("未指定 levelId，使用活动视图所在标高「" + viewLevel.Name + "」。");
                return viewLevel;
            }

            var lowest = new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .FirstOrDefault();

            if (lowest == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "模型里没有任何标高，无法创建墙。");

            context.Warnings.Add("未指定 levelId 且活动视图没有关联标高，使用最低标高「" + lowest.Name + "」。");
            return lowest;
        }

        private static WallType ResolveWallType(
            Document document, ToolExecutionContext<UIApplication> context, string rawId)
        {
            if (!string.IsNullOrWhiteSpace(rawId))
            {
                var element = RequireElement(document, rawId);
                var explicitType = element as WallType;
                if (explicitType == null)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "构件 " + rawId + " 不是墙类型。可用 revit_query_elements 查 OST_Walls 取一面已有墙，" +
                        "其 typeId 即可用作 wallTypeId。");
                return explicitType;
            }

            var defaultId = document.GetDefaultElementTypeId(ElementTypeGroup.WallType);
            var byDefault = defaultId == null ? null : document.GetElement(defaultId) as WallType;
            if (byDefault != null)
            {
                context.Warnings.Add("未指定 wallTypeId，使用项目默认墙类型「" + byDefault.Name + "」。");
                return byDefault;
            }

            // 默认类型没设时退而求其次：任取一个基本墙。比直接失败有用
            var any = new FilteredElementCollector(document)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .FirstOrDefault(t => t.Kind == WallKind.Basic);

            if (any == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "模型里没有可用的基本墙类型，无法创建墙。");

            context.Warnings.Add("项目未设置默认墙类型，使用「" + any.Name + "」。");
            return any;
        }

        private static double ToFeet(double millimeters)
        {
            return millimeters / MmPerFoot;
        }
    }
}
