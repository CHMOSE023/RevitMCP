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
    public sealed class ParameterValueSpec
    {
        [McpParam("参数名，须与 revit_get_element_parameters 返回的名称完全一致", Required = true)]
        public string Name { get; set; }

        [McpParam("要写入的值，一律用字符串。数值按项目显示单位解释", Required = true)]
        public string Value { get; set; }

        [McpParam("数值按哪种单位解释：projectUnits（默认，项目显示单位）、internal（Revit 内部单位：长度英尺、角度弧度）。" +
                  "写不进去会报错，不会在两种单位之间回退猜测",
                  AllowedValues = new[] { "projectUnits", "internal" })]
        public string ValueMode { get; set; }
    }

    public sealed class DuplicateTypeInput
    {
        [McpParam("要复制的源类型 ID，来自 revit_list_types", Required = true)]
        public string SourceTypeId { get; set; }

        [McpParam("新类型的名称。项目内同族下必须唯一", Required = true)]
        public string Name { get; set; }

        [McpParam("新类型的总厚度，毫米。仅墙、楼板、天花、屋顶这类有层构造的类型支持。" +
                  "多层构造只会调整结构层的厚度，其余层保持原样")]
        public double? ThicknessMm { get; set; }

        [McpParam("要修改的其他类型参数")]
        public List<ParameterValueSpec> Parameters { get; set; }
    }

    public sealed class LayerInfo
    {
        [McpParam("第几层，从 1 起，按从外到内的顺序")]
        public int Index { get; set; }

        [McpParam("层功能：Structure（结构）、Finish1/Finish2（面层）、Insulation（保温）、" +
                  "Substrate（衬底）、Membrane（涂膜层，厚度恒为 0）")]
        public string Function { get; set; }

        [McpParam("这一层的厚度，毫米")]
        public double WidthMm { get; set; }

        [McpParam("材质名")]
        public string Material { get; set; }

        [McpParam("本次厚度调整是否加在了这一层")]
        public bool Adjusted { get; set; }

        [McpParam("Revit 报的 CanLayerWidthBeNonZero。**它不等于「厚度能不能改」**——" +
                  "实测普通隔墙的结构层也会报 false，仅供参考，工具不拿它做判断")]
        public bool CanAdjust { get; set; }
    }

    public sealed class DuplicateTypeOutput : IReportsAffectedElements
    {
        [McpParam("新类型的 ID。建模工具的 typeId 参数可以直接用它")]
        public string Id { get; set; }

        [McpParam("新类型名")]
        public string Name { get; set; }

        [McpParam("族名")]
        public string Family { get; set; }

        [McpParam("源类型名")]
        public string CopiedFrom { get; set; }

        [McpParam("整体厚度，毫米。无层构造的类型为 null")]
        public double? ThicknessMm { get; set; }

        [McpParam("实际改动的参数")]
        public List<string> Changed { get; set; } = new List<string>();

        [McpParam("层构造明细，从外到内。**改了厚度就要看这里**——" +
                  "总厚度对了不代表加在了该加的那层。无层构造的类型为 null")]
        public List<LayerInfo> Layers { get; set; }

        int IReportsAffectedElements.AffectedElements => 1;
    }

    /// <summary>
    /// 复制一个类型并改参数——就是 Revit「类型属性」对话框里那个「复制」按钮。
    ///
    /// M6 时刻意没做这件事，理由是"按尺寸动态建类型会往用户的类型库里塞垃圾"。
    /// 那个担心仍然成立，所以这里不提供"给我一个 250 厚的墙"这种模糊入口：
    /// **必须显式指定从哪个类型复制、新类型叫什么名字**。
    /// 类型库是用户的资产，新增什么、叫什么，得是明确的决定而不是副作用。
    /// </summary>
    [McpTool("revit_duplicate_type",
        Title = "复制族类型",
        Description = "复制一个已有的族类型，改名并调整参数——对应 Revit「类型属性」里的「复制」按钮。" +
                      "用来生成项目需要但样板里没有的规格，比如从「常规-200mm」复制出「外墙-250」。" +
                      "**新类型会永久留在项目里**，不要为了一次性建模随手创建。" +
                      "墙、楼板这类有层构造的类型可以用 thicknessMm 直接调厚度；" +
                      "其余参数用 parameters 设置，写法和 revit_set_element_parameters 一致。",
        Destructive = false,
        TimeoutSeconds = 60)]
    public sealed class DuplicateTypeTool : RevitTool<DuplicateTypeInput, DuplicateTypeOutput>
    {
        /// <summary>本次厚度调整落在了哪一层（0 基），没调则为 -1。</summary>
        private int _adjustedLayer = -1;

        public override DuplicateTypeOutput Execute(
            DuplicateTypeInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);

            if (string.IsNullOrWhiteSpace(input.Name))
                throw new ToolFailureException(McpDomainError.InvalidParameter, "name 不能为空。");

            var source = RequireElement(document, input.SourceTypeId) as ElementType;
            if (source == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "sourceTypeId " + input.SourceTypeId + " 不是族类型。用 revit_list_types 取类型 ID。");

            var sourceName = SafeName(source);

            ElementType created;
            try
            {
                created = source.Duplicate(input.Name.Trim());
            }
            catch (Exception ex)
            {
                // 重名是最常见的失败，Revit 的原话不会点出这一点
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "Revit 拒绝复制类型：" + ex.Message +
                    "。最常见的原因是同族下已有名为「" + input.Name + "」的类型——" +
                    "用 revit_list_types 查一下，或者换个名字。");
            }

            if (created == null)
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "Revit 未能复制类型，但也没有报错。");

            var output = new DuplicateTypeOutput
            {
                Id = created.Id.ToProtocolString(),
                Name = SafeName(created),
                Family = SafeFamilyName(created),
                CopiedFrom = sourceName
            };

            if (input.ThicknessMm.HasValue)
                ApplyThickness(created, input.ThicknessMm.Value, output, context);

            if (input.Parameters != null)
                ApplyParameters(created, input.Parameters, output);

            output.ThicknessMm = ThicknessOf(created);
            output.Layers = DescribeLayers(document, created, _adjustedLayer);

            return output;
        }

        /// <summary>
        /// 调整整体厚度。
        ///
        /// **厚度不是一个能直接写的参数**——Revit 的类型属性里它是灰的，
        /// 由层构造算出来。所以要改的是某一层的宽度。
        ///
        /// 单层构造没有歧义；多层构造改哪一层是个真问题，
        /// 这里改 Revit 自己标记的"结构层"，并把这个决定说出来——
        /// 一面 120 厚的隔墙要变 200，加厚的该是龙骨层而不是两侧的面层。
        /// </summary>
        private void ApplyThickness(
            ElementType type, double thicknessMm, DuplicateTypeOutput output,
            ToolExecutionContext<UIApplication> context)
        {
            if (thicknessMm < Units.MinLength)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "thicknessMm 必须为正且不小于 " + Units.MinLength + " 毫米，收到 " + thicknessMm + "。");

            var host = type as HostObjAttributes;
            if (host == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "源类型没有层构造，不能直接设厚度（整个操作已回滚，新类型没有被创建）。" +
                    "thicknessMm 只适用于墙、楼板、天花、屋顶；门窗家具的尺寸请用 parameters 设置。");

            CompoundStructure structure;
            try { structure = host.GetCompoundStructure(); }
            catch { structure = null; }

            if (structure == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "源类型没有可编辑的层构造（幕墙、叠层墙就是这样），无法设厚度。" +
                    "整个操作已回滚，**新类型没有被创建**——" +
                    "要复制幕墙类型请去掉 thicknessMm，幕墙的厚度由嵌板和竖梃决定。");

            // 只排除涂膜层——它的厚度必须为 0。
            //
            // **不要用 CanLayerWidthBeNonZero 做筛选**：实测它对一面普通隔墙的
            // 结构层返回 false，而那一层在 Revit 的「编辑部件」里明明能改；
            // 同样是 15.5mm 的两个面层，它一个报 true 一个报 false。
            // 这个 API 管的是"厚度能不能非零"，和"能不能改"不是一回事，
            // 拿它当筛子，恰好会把该加厚的那层筛掉。
            var candidates = new List<int>();
            for (var i = 0; i < structure.LayerCount; i++)
            {
                if (IsMembrane(structure, i)) continue;
                candidates.Add(i);
            }

            if (candidates.Count == 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "类型「" + SafeName(type) + "」只有涂膜层，没有可以承载厚度的构造层。");

            var targetFeet = Units.ToFeet(thicknessMm);

            // 按优先级排队：结构层优先，同级取厚的。
            // 排好队依次真去设，而不是预判——Revit 到底让不让改，试一次就知道
            var queue = candidates.Where(i => IsStructure(structure, i))
                                  .OrderByDescending(i => SafeWidth(structure, i))
                                  .Concat(candidates.Where(i => !IsStructure(structure, i))
                                                    .OrderByDescending(i => SafeWidth(structure, i)))
                                  .ToList();

            var failures = new List<string>();
            var applied = -1;

            foreach (var index in queue)
            {
                double others = 0;
                for (var i = 0; i < structure.LayerCount; i++)
                {
                    if (i == index) continue;
                    others += SafeWidth(structure, i);
                }

                var remaining = targetFeet - others;
                if (remaining < Units.ToFeet(Units.MinLength))
                {
                    failures.Add("第 " + (index + 1) + " 层：其余层已占满目标厚度");
                    continue;
                }

                try
                {
                    structure.SetLayerWidth(index, remaining);
                    host.SetCompoundStructure(structure);
                    applied = index;
                    break;
                }
                catch (Exception ex)
                {
                    failures.Add("第 " + (index + 1) + " 层：" + ex.Message);

                    // 设失败可能已经动过这个副本，重新取一份干净的再试下一层
                    try { structure = host.GetCompoundStructure(); }
                    catch { break; }
                }
            }

            if (applied < 0)
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "没有任何一层的厚度能改成目标值，整个操作已回滚。逐层结果：" +
                    string.Join("；", failures.Take(6).ToArray()));

            _adjustedLayer = applied;

            var isStructural = IsStructure(structure, applied);
            if (!isStructural)
            {
                var blocked = queue.TakeWhile(i => i != applied)
                                   .Where(i => IsStructure(structure, i))
                                   .ToList();

                if (blocked.Count > 0)
                    context.Warnings.Add(
                        "「" + SafeName(type) + "」的结构层（第 " + (blocked[0] + 1) + " 层）改不动（" +
                        (failures.Count > 0 ? failures[0] : "原因不明") + "），" +
                        "厚度加在了第 " + (applied + 1) + " 层上。" +
                        "**这多半不是你要的**——请核对返回的 layers，或在 Revit 的「编辑部件」里手动调整。");
                else
                    context.Warnings.Add(
                        "「" + SafeName(type) + "」没有标着「结构」的构造层，" +
                        "厚度加在了第 " + (applied + 1) + " 层上。请核对返回的 layers。");
            }
            else if (structure.LayerCount > 1)
            {
                context.Warnings.Add(
                    "「" + SafeName(type) + "」有 " + structure.LayerCount + " 层构造，" +
                    "厚度调整加在了结构层（第 " + (applied + 1) + " 层）上，面层保持原厚。");
            }

            output.Changed.Add("厚度 → " + thicknessMm + " mm");
        }

        private static bool IsStructure(CompoundStructure structure, int index)
        {
            try { return structure.GetLayerFunction(index) == MaterialFunctionAssignment.Structure; }
            catch { return false; }
        }

        /// <summary>涂膜层的厚度必须为 0，不能拿来承载厚度。</summary>
        private static bool IsMembrane(CompoundStructure structure, int index)
        {
            try { return structure.GetLayerFunction(index) == MaterialFunctionAssignment.Membrane; }
            catch { return false; }
        }

        private static double SafeWidth(CompoundStructure structure, int index)
        {
            try { return structure.GetLayerWidth(index); }
            catch { return 0; }
        }

        private static int Thickest(CompoundStructure structure, List<int> candidates)
        {
            var pick = candidates[0];
            var max = -1.0;

            foreach (var i in candidates)
            {
                double width;
                try { width = structure.GetLayerWidth(i); }
                catch { continue; }

                if (width <= max) continue;
                max = width;
                pick = i;
            }

            return pick;
        }

        private static void ApplyParameters(
            ElementType type, List<ParameterValueSpec> parameters, DuplicateTypeOutput output)
        {
            foreach (var entry in parameters)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Name)) continue;

                var parameter = Lookup(type, entry.Name);

                if (parameter == null)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "类型上没有名为「" + entry.Name + "」的参数。新类型已创建，但这个参数没设上——" +
                        "用 revit_get_element_parameters 查一下它到底有哪些参数。");

                if (parameter.IsReadOnly)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "参数「" + entry.Name + "」是只读的（厚度这类由层构造算出来的值就是只读的，" +
                        "请改用 thicknessMm）。新类型已创建，但这个参数没设上。");

                ParameterWriter.Write(parameter, entry.Value, type, ParameterWriter.ParseInternalMode(entry.ValueMode));
                output.Changed.Add(entry.Name + " → " + ParameterWriter.DisplayOf(parameter));
            }
        }

        private static Parameter Lookup(Element element, string name)
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
        /// 列出层构造。**改了厚度就必须能看到改在哪一层**——
        /// 总厚度对了不代表加对了地方：一面隔墙该加厚的是龙骨层，不是两侧的石膏板。
        /// 在这个字段出现之前，只能去 Revit 里开"编辑部件"核对。
        /// </summary>
        private static List<LayerInfo> DescribeLayers(Document document, ElementType type, int adjusted)
        {
            var host = type as HostObjAttributes;
            if (host == null) return null;

            CompoundStructure structure;
            try { structure = host.GetCompoundStructure(); }
            catch { return null; }

            if (structure == null) return null;

            var layers = new List<LayerInfo>();

            for (var i = 0; i < structure.LayerCount; i++)
            {
                var info = new LayerInfo { Index = i + 1, Adjusted = i == adjusted };

                try { info.CanAdjust = structure.CanLayerWidthBeNonZero(i); }
                catch { info.CanAdjust = false; }

                try { info.Function = structure.GetLayerFunction(i).ToString(); }
                catch { }

                try { info.WidthMm = Units.Round(Units.FromFeet(structure.GetLayerWidth(i))); }
                catch { }

                try
                {
                    var materialId = structure.GetMaterialId(i);
                    if (materialId != null && materialId != ElementId.InvalidElementId)
                        info.Material = SafeName(document.GetElement(materialId));
                }
                catch { }

                layers.Add(info);
            }

            return layers;
        }

        private static double? ThicknessOf(ElementType type)
        {
            var host = type as HostObjAttributes;
            if (host == null) return null;

            try
            {
                var structure = host.GetCompoundStructure();
                return structure == null ? (double?)null : Units.Round(Units.FromFeet(structure.GetWidth()));
            }
            catch
            {
                return null;
            }
        }

        private static string SafeName(Element element)
        {
            try { return element?.Name; }
            catch { return null; }
        }

        private static string SafeFamilyName(ElementType type)
        {
            try { return type.FamilyName; }
            catch { return null; }
        }
    }
}
