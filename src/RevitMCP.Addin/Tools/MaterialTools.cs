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
    /// <summary>
    /// 本文件的失败构造：把下标前缀写成 <c>materials[i]</c>，而不是通用的 elements[i]。
    ///
    /// 报错里出现的数组名必须是调用方真的传过的那个，否则它会去找一个不存在的参数。
    /// </summary>
    internal static class MaterialFail
    {
        public static ToolFailureException At(int index, string code, string message)
        {
            return CreateSupport.Failure(index, code, message, "materials", "整批未创建");
        }
    }

    // ==================== 材质清单 ====================

    public sealed class ListMaterialsInput
    {
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档")]
        public string DocumentId { get; set; }

        [McpParam("按材质名过滤（不区分大小写的子串匹配）")]
        public string NameContains { get; set; }

        [McpParam("按材质类别过滤，如「混凝土」「金属」。随项目语言变化")]
        public string MaterialClass { get; set; }

        [McpParam("true 时只返回模型中实际用到的材质，默认 false")]
        public bool? OnlyInUse { get; set; }

        [McpParam("最多返回多少条，默认 200，上限 1000")]
        public int? Limit { get; set; }
    }

    public sealed class MaterialInfo
    {
        [McpParam("材质 ID。写材质参数时用它作为 value")]
        public string Id { get; set; }

        [McpParam("材质名")]
        public string Name { get; set; }

        [McpParam("材质类别，如「混凝土」「金属」")]
        public string MaterialClass { get; set; }

        [McpParam("着色颜色，#RRGGBB")]
        public string Color { get; set; }

        [McpParam("透明度，0 到 100")]
        public int Transparency { get; set; }

        [McpParam("使用这个材质的构件数。仅 onlyInUse 为 true 时准确")]
        public int? UsedByCount { get; set; }
    }

    public sealed class ListMaterialsOutput
    {
        [McpParam("匹配到的材质总数（不受 limit 影响）")]
        public int Total { get; set; }

        [McpParam("本次实际返回的条数")]
        public int Returned { get; set; }

        [McpParam("是否因 limit 而被截断")]
        public bool Truncated { get; set; }

        [McpParam("材质列表")]
        public List<MaterialInfo> Materials { get; set; } = new List<MaterialInfo>();
    }

    [McpTool("revit_list_materials",
        Toolsets = new[] { Toolsets.Authoring },
        Title = "列出材质",
        Description = "列出项目里的材质及其 ID、类别、颜色。" +
                      "**给构件设材质用的就是这里的 ID**：拿到 ID 后调 revit_set_element_parameters，" +
                      "参数名填材质参数的名字（用 revit_get_element_parameters 查），value 填材质 ID。" +
                      "墙、楼板这类有层构造的构件，材质在类型的层里——" +
                      "那种要用 revit_duplicate_type 改。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class ListMaterialsTool : RevitTool<ListMaterialsInput, ListMaterialsOutput>
    {
        private const int DefaultLimit = 200;
        private const int MaxLimit = 1000;

        public override ListMaterialsOutput Execute(
            ListMaterialsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = ResolveDocument(context, input.DocumentId);
            var limit = Math.Min(Math.Max(input.Limit ?? DefaultLimit, 1), MaxLimit);
            var onlyInUse = input.OnlyInUse ?? false;

            var usage = onlyInUse ? CountUsage(document, context) : null;

            var results = new List<MaterialInfo>();

            foreach (var material in new FilteredElementCollector(document)
                         .OfClass(typeof(Material))
                         .Cast<Material>())
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var name = AnnotationSupport.SafeName(material);

                if (!string.IsNullOrWhiteSpace(input.NameContains) &&
                    (name == null || name.IndexOf(input.NameContains, StringComparison.OrdinalIgnoreCase) < 0))
                    continue;

                string materialClass;
                try { materialClass = material.MaterialClass; }
                catch { materialClass = null; }

                if (!string.IsNullOrWhiteSpace(input.MaterialClass) &&
                    (materialClass == null ||
                     materialClass.IndexOf(input.MaterialClass, StringComparison.OrdinalIgnoreCase) < 0))
                    continue;

                int? used = null;
                if (usage != null)
                {
                    usage.TryGetValue(material.Id.GetValue(), out var count);
                    if (count == 0) continue;
                    used = count;
                }

                var info = new MaterialInfo
                {
                    Id = material.Id.ToProtocolString(),
                    Name = name,
                    MaterialClass = materialClass,
                    UsedByCount = used
                };

                try
                {
                    info.Color = FormatColor(material.Color);
                    info.Transparency = material.Transparency;
                }
                catch { /* 少数材质读不到外观属性 */ }

                results.Add(info);
            }

            results = results
                .OrderBy(m => m.MaterialClass, StringComparer.CurrentCulture)
                .ThenBy(m => m.Name, StringComparer.CurrentCulture)
                .ToList();

            var output = new ListMaterialsOutput { Total = results.Count };

            if (results.Count > limit)
            {
                output.Truncated = true;
                results = results.Take(limit).ToList();
            }

            output.Materials = results;
            output.Returned = results.Count;
            return output;
        }

        /// <summary>
        /// 每个材质被多少构件用到。
        /// 走 <c>GetMaterialIds</c> 而不是只看材质参数——
        /// 墙、楼板的材质藏在类型的层构造里，参数上根本看不到。
        /// </summary>
        private static Dictionary<long, int> CountUsage(
            Document document, ToolExecutionContext<UIApplication> context)
        {
            var counts = new Dictionary<long, int>();

            foreach (var element in new FilteredElementCollector(document).WhereElementIsNotElementType())
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                ICollection<ElementId> ids;
                try { ids = element.GetMaterialIds(false); }
                catch { continue; }

                foreach (var id in ids)
                {
                    if (id == null || id == ElementId.InvalidElementId) continue;

                    var key = id.GetValue();
                    counts.TryGetValue(key, out var count);
                    counts[key] = count + 1;
                }
            }

            return counts;
        }

        internal static string FormatColor(Color color)
        {
            if (color == null || !color.IsValid) return null;

            return "#" + color.Red.ToString("X2") + color.Green.ToString("X2") + color.Blue.ToString("X2");
        }
    }

    // ==================== 创建材质 ====================

    public sealed class MaterialSpec
    {
        [McpParam("材质名。项目内必须唯一", Required = true)]
        public string Name { get; set; }

        [McpParam("从哪个已有材质复制，ID 来自 revit_list_materials。" +
                  "省略则新建一个空白材质——**空白材质没有物理与外观资源**，" +
                  "渲染和算量都用不上它。有条件的话一律从最接近的材质复制")]
        public string CopyFromId { get; set; }

        [McpParam("着色颜色，#RRGGBB。省略则沿用源材质或 Revit 默认色")]
        public string Color { get; set; }

        [McpParam("透明度，0（不透明）到 100（全透明）")]
        public int? Transparency { get; set; }

        [McpParam("材质类别，如「混凝土」「金属」。它决定材质在浏览器里怎么归类")]
        public string MaterialClass { get; set; }
    }

    public sealed class CreateMaterialsInput
    {
        [McpParam("要创建的材质，一次调用可建多个", Required = true)]
        public List<MaterialSpec> Materials { get; set; }

        [McpParam("数量超过单次上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    public sealed class CreatedMaterial
    {
        [McpParam("对应 materials 数组中的下标，从 0 起")]
        public int Index { get; set; }

        [McpParam("新材质 ID")]
        public string Id { get; set; }

        [McpParam("材质名")]
        public string Name { get; set; }

        [McpParam("材质类别")]
        public string MaterialClass { get; set; }

        [McpParam("颜色，#RRGGBB")]
        public string Color { get; set; }

        [McpParam("是从哪个材质复制来的，空白新建则为 null")]
        public string CopiedFrom { get; set; }
    }

    public sealed class CreateMaterialsOutput : IReportsAffectedElements
    {
        [McpParam("成功创建的材质数")]
        public int Created { get; set; }

        [McpParam("新建的材质，顺序与入参一致")]
        public List<CreatedMaterial> Materials { get; set; } = new List<CreatedMaterial>();

        int IReportsAffectedElements.AffectedElements => Created;
    }

    /// <summary>
    /// 创建材质。
    ///
    /// 与 <see cref="DuplicateTypeTool"/> 同一个道理：材质是项目的长期资产，
    /// 建出来就永久留在那里。所以这里不提供"给我一个红色材质"这种模糊入口，
    /// 名字必须显式给出；并且强烈建议从已有材质复制——
    /// <c>Material.Create</c> 造出来的是一个没有物理与外观资源的空壳，
    /// 渲染和算量都用不上，而这一点在模型里完全看不出来。
    /// </summary>
    [McpTool("revit_create_materials",
        Toolsets = new[] { Toolsets.Authoring },
        Title = "创建材质",
        Description = "批量创建材质。**尽量给 copyFromId 从已有材质复制**：" +
                      "空白新建的材质没有物理属性与外观资源，渲染和算量都用不上它，" +
                      "而这一点在模型里完全看不出来。" +
                      "材质会永久留在项目里，不要为了一次性需求随手创建。" +
                      "整批要么全部建成、要么一个都不建。",
        Destructive = false,
        TimeoutSeconds = 120)]
    public sealed class CreateMaterialsTool : RevitTool<CreateMaterialsInput, CreateMaterialsOutput>
    {
        public override CreateMaterialsOutput Execute(
            CreateMaterialsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);
            RequireBatch(input.Materials, input.Confirm, context, "创建");

            var output = new CreateMaterialsOutput();
            var total = input.Materials.Count;

            for (var index = 0; index < total; index++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var spec = input.Materials[index];
                if (spec == null)
                    throw MaterialFail.At(index, McpDomainError.InvalidParameter, "该项为 null。");

                output.Materials.Add(CreateOne(document, context, spec, index));
                ProgressTicker.Tick(context.Progress, index + 1, total, "已创建");
            }

            output.Created = output.Materials.Count;
            return output;
        }

        private static CreatedMaterial CreateOne(
            Document document, ToolExecutionContext<UIApplication> context, MaterialSpec spec, int index)
        {
            if (string.IsNullOrWhiteSpace(spec.Name))
                throw MaterialFail.At(index, McpDomainError.InvalidParameter, "name 不能为空。");

            var name = spec.Name.Trim();
            Material material;
            string copiedFrom = null;

            if (!string.IsNullOrWhiteSpace(spec.CopyFromId))
            {
                var element = CreateSupport.RequireElement(document, spec.CopyFromId, index);
                var source = element as Material;

                if (source == null)
                    throw MaterialFail.At(index, McpDomainError.InvalidParameter,
                        "copyFromId " + spec.CopyFromId + " 不是材质。用 revit_list_materials 取 ID。");

                copiedFrom = AnnotationSupport.SafeName(source);

                try
                {
                    material = source.Duplicate(name);
                }
                catch (Exception ex)
                {
                    throw MaterialFail.At(index, McpDomainError.TransactionFailed,
                        "复制材质「" + copiedFrom + "」为「" + name + "」失败：" + ex.Message +
                        "。材质名在项目内必须唯一，多半是重名了。");
                }
            }
            else
            {
                context.Warnings.Add(
                    "materials[" + index + "]：「" + name + "」是空白新建的，" +
                    "没有物理属性与外观资源——渲染和算量用不上它。" +
                    "建议改用 copyFromId 从最接近的已有材质复制。");

                ElementId id;
                try
                {
                    id = Material.Create(document, name);
                }
                catch (Exception ex)
                {
                    throw MaterialFail.At(index, McpDomainError.TransactionFailed,
                        "创建材质「" + name + "」失败：" + ex.Message + "。材质名在项目内必须唯一。");
                }

                material = document.GetElement(id) as Material;
            }

            if (material == null)
                throw MaterialFail.At(index, McpDomainError.TransactionFailed,
                    "Revit 未能创建材质「" + name + "」，但也没有报错。");

            ApplyAppearance(material, spec, index);

            return new CreatedMaterial
            {
                Index = index,
                Id = material.Id.ToProtocolString(),
                Name = AnnotationSupport.SafeName(material),
                MaterialClass = SafeClass(material),
                Color = SafeColor(material),
                CopiedFrom = copiedFrom
            };
        }

        private static void ApplyAppearance(Material material, MaterialSpec spec, int index)
        {
            if (!string.IsNullOrWhiteSpace(spec.Color))
                material.Color = ParseColor(spec.Color, index);

            if (spec.Transparency != null)
            {
                if (spec.Transparency.Value < 0 || spec.Transparency.Value > 100)
                    throw MaterialFail.At(index, McpDomainError.InvalidParameter,
                        "transparency 必须在 0 到 100 之间，收到 " + spec.Transparency.Value + "。");

                material.Transparency = spec.Transparency.Value;
            }

            if (!string.IsNullOrWhiteSpace(spec.MaterialClass))
                material.MaterialClass = spec.MaterialClass.Trim();
        }

        private static Color ParseColor(string value, int index)
        {
            var text = value.Trim().TrimStart('#');

            if (text.Length != 6 ||
                !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                throw MaterialFail.At(index, McpDomainError.InvalidParameter,
                    "color 必须是 #RRGGBB 形式的十六进制颜色，收到 \"" + value + "\"。");

            return new Color(
                (byte)((rgb >> 16) & 0xFF),
                (byte)((rgb >> 8) & 0xFF),
                (byte)(rgb & 0xFF));
        }

        private static string SafeClass(Material material)
        {
            try { return material.MaterialClass; }
            catch { return null; }
        }

        private static string SafeColor(Material material)
        {
            try { return ListMaterialsTool.FormatColor(material.Color); }
            catch { return null; }
        }
    }

    // ==================== 材质用量 ====================

    public sealed class MaterialQuantitiesInput
    {
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档")]
        public string DocumentId { get; set; }

        [McpParam("只统计这些构件。与 category 二选一。ElementId 与 uniqueId 两种写法都接受")]
        public List<string> ElementIds { get; set; }

        [McpParam("只统计这个类别的构件，如 OST_Walls。与 elementIds 二选一。" +
                  "两个都不给则统计整个模型——大模型上这会很慢")]
        public string Category { get; set; }

        [McpParam("只统计这个阶段创建的构件，ID 来自 revit_list_phases。" +
                  "改造项目不给它，「现有」与「新建」的量会加在一起")]
        public string PhaseId { get; set; }

        [McpParam("是否按构件逐条给出明细，默认 false（只给按材质汇总的结果）。" +
                  "构件多时明细会非常长")]
        public bool? IncludeElements { get; set; }
    }

    public sealed class MaterialQuantity
    {
        [McpParam("材质 ID")]
        public string MaterialId { get; set; }

        [McpParam("材质名")]
        public string Material { get; set; }

        [McpParam("材质类别")]
        public string MaterialClass { get; set; }

        [McpParam("合计体积，立方米")]
        public double VolumeCbm { get; set; }

        [McpParam("合计面积，平方米")]
        public double AreaSqm { get; set; }

        [McpParam("涉及的构件数")]
        public int ElementCount { get; set; }
    }

    public sealed class ElementMaterialQuantity
    {
        [McpParam("构件 ID")]
        public string ElementId { get; set; }

        [McpParam("构件名")]
        public string ElementName { get; set; }

        [McpParam("类别")]
        public string Category { get; set; }

        [McpParam("材质名")]
        public string Material { get; set; }

        [McpParam("体积，立方米")]
        public double VolumeCbm { get; set; }

        [McpParam("面积，平方米")]
        public double AreaSqm { get; set; }
    }

    public sealed class MaterialQuantitiesOutput
    {
        [McpParam("参与统计的构件数")]
        public int ElementsScanned { get; set; }

        [McpParam("其中有材质用量数据的构件数。两者差得多说明大部分构件没有实体几何")]
        public int ElementsWithQuantities { get; set; }

        [McpParam("按材质汇总的用量，体积降序")]
        public List<MaterialQuantity> Materials { get; set; } = new List<MaterialQuantity>();

        [McpParam("逐构件明细。仅 includeElements 为 true 时有值")]
        public List<ElementMaterialQuantity> Elements { get; set; }
    }

    /// <summary>
    /// 统计材质用量。
    ///
    /// 数据来自 <c>Element.GetMaterialVolume/GetMaterialArea</c>——
    /// 这是 Revit 自己算的量，与明细表里的「材质：体积」是同一个数，
    /// 不是这个工具另外估出来的。
    /// </summary>
    [McpTool("revit_calculate_material_quantities",
        Toolsets = new[] { Toolsets.Authoring },
        Title = "统计材质用量",
        Description = "按材质汇总体积（立方米）与面积（平方米）。" +
                      "数字来自 Revit 自己的材质算量，与明细表里的「材质：体积」一致。" +
                      "**改造项目请给 phaseId**，否则「现有」与「新建」的量会加在一起。" +
                      "只有带实体几何的构件才有用量——回执里的 elementsWithQuantities " +
                      "远小于 elementsScanned 时，说明大部分构件本来就没有体积。",
        ReadOnly = true,
        TimeoutSeconds = 300)]
    public sealed class MaterialQuantitiesTool : RevitTool<MaterialQuantitiesInput, MaterialQuantitiesOutput>
    {
        public override MaterialQuantitiesOutput Execute(
            MaterialQuantitiesInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = ResolveDocument(context, input.DocumentId);

            var hasIds = input.ElementIds != null && input.ElementIds.Count > 0;
            if (hasIds && !string.IsNullOrWhiteSpace(input.Category))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "elementIds 与 category 只能给一个。");

            var elements = Collect(document, input, hasIds);
            var phaseId = ResolvePhaseId(document, input.PhaseId);

            var output = new MaterialQuantitiesOutput();
            var includeElements = input.IncludeElements ?? false;
            if (includeElements) output.Elements = new List<ElementMaterialQuantity>();

            var totals = new Dictionary<long, MaterialQuantity>();
            var elementCounts = new Dictionary<long, HashSet<long>>();

            var total = elements.Count;

            for (var index = 0; index < total; index++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var element = elements[index];

                if (phaseId != null && !CreatedIn(element, phaseId.Value)) continue;

                output.ElementsScanned++;

                ICollection<ElementId> materialIds;
                try { materialIds = element.GetMaterialIds(false); }
                catch { continue; }

                var counted = false;

                foreach (var materialId in materialIds)
                {
                    if (materialId == null || materialId == ElementId.InvalidElementId) continue;

                    double volume, area;
                    try
                    {
                        volume = element.GetMaterialVolume(materialId);
                        area = element.GetMaterialArea(materialId, false);
                    }
                    catch { continue; }

                    if (volume <= 0 && area <= 0) continue;

                    counted = true;

                    var key = materialId.GetValue();
                    if (!totals.TryGetValue(key, out var entry))
                    {
                        var material = document.GetElement(materialId) as Material;

                        entry = new MaterialQuantity
                        {
                            MaterialId = materialId.ToProtocolString(),
                            Material = AnnotationSupport.SafeName(material),
                            MaterialClass = SafeClass(material)
                        };

                        totals[key] = entry;
                        elementCounts[key] = new HashSet<long>();
                    }

                    entry.VolumeCbm += Units.CubicMeters(volume);
                    entry.AreaSqm += Units.SquareMeters(area);
                    elementCounts[key].Add(element.Id.GetValue());

                    if (includeElements)
                        output.Elements.Add(new ElementMaterialQuantity
                        {
                            ElementId = element.Id.ToProtocolString(),
                            ElementName = AnnotationSupport.SafeName(element),
                            Category = element.Category?.Name,
                            Material = entry.Material,
                            VolumeCbm = Units.CubicMeters(volume),
                            AreaSqm = Units.SquareMeters(area)
                        });
                }

                if (counted) output.ElementsWithQuantities++;

                ProgressTicker.Tick(context.Progress, index + 1, total, "已统计");
            }

            foreach (var pair in totals)
            {
                pair.Value.ElementCount = elementCounts[pair.Key].Count;

                // 累加之后再取整：先四舍五入再累加，几百个构件下来误差会攒得很明显
                pair.Value.VolumeCbm = Math.Round(pair.Value.VolumeCbm, 3);
                pair.Value.AreaSqm = Math.Round(pair.Value.AreaSqm, 3);
            }

            output.Materials = totals.Values
                .OrderByDescending(m => m.VolumeCbm)
                .ThenByDescending(m => m.AreaSqm)
                .ToList();

            if (output.ElementsScanned > 0 && output.ElementsWithQuantities == 0)
                context.Warnings.Add(
                    "扫描了 " + output.ElementsScanned + " 个构件，但一个都没有材质用量。" +
                    "常见原因：这些构件没有实体几何（如房间、标记），" +
                    "或它们的类型里根本没指定材质。");

            return output;
        }

        private List<Element> Collect(Document document, MaterialQuantitiesInput input, bool hasIds)
        {
            if (hasIds)
                return input.ElementIds.Select(id => RequireElement(document, id)).ToList();

            var collector = new FilteredElementCollector(document).WhereElementIsNotElementType();

            if (!string.IsNullOrWhiteSpace(input.Category))
                collector = collector.OfCategory(ParseCategory(input.Category));

            return collector.ToList();
        }

        private long? ResolvePhaseId(Document document, string rawId)
        {
            if (string.IsNullOrWhiteSpace(rawId)) return null;

            var element = RequireElement(document, rawId);
            var phase = element as Phase;

            if (phase == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "phaseId " + rawId + " 不是阶段。用 revit_list_phases 取 ID。");

            return phase.Id.GetValue();
        }

        private static bool CreatedIn(Element element, long phaseId)
        {
            try
            {
                var parameter = element.get_Parameter(BuiltInParameter.PHASE_CREATED);
                if (parameter == null || !parameter.HasValue) return false;

                var id = parameter.AsElementId();
                return id != null && id.GetValue() == phaseId;
            }
            catch { return false; }
        }

        private static string SafeClass(Material material)
        {
            try { return material?.MaterialClass; }
            catch { return null; }
        }
    }
}
