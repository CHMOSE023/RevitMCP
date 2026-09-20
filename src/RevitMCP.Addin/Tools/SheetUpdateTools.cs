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
    /// 本文件的失败构造：把下标前缀写成 <c>sheets[i]</c>，而不是通用的 elements[i]。
    ///
    /// 报错里出现的数组名必须是调用方真的传过的那个，否则它会去找一个不存在的参数。
    /// </summary>
    internal static class SheetUpdateFail
    {
        public static ToolFailureException At(int index, string code, string message)
        {
            return CreateSupport.Failure(index, code, message, "sheets", "整批未改动");
        }
    }

    // ==================== 修改图纸 ====================

    public sealed class SheetUpdateSpec
    {
        [McpParam("要修改的图纸 ID，来自 revit_list_views（viewType 为 DrawingSheet）", Required = true)]
        public string SheetId { get; set; }

        [McpParam("新的图纸编号。省略则不改")]
        public string Number { get; set; }

        [McpParam("新的图纸名称。省略则不改")]
        public string Name { get; set; }

        [McpParam("要写入的图纸参数（「审核者」「设计者」「出图日期」这类）。" +
                  "参数名用 revit_get_sheet_contents 查，不要猜——它们随项目样板和语言变化")]
        public List<ParameterValueSpec> Parameters { get; set; }

        [McpParam("要写入标题栏实例自身的参数。有些项目把项目名称、业主一类的信息" +
                  "放在标题栏族实例上而不是图纸上，那种就用这个")]
        public List<ParameterValueSpec> TitleBlockParameters { get; set; }
    }

    public sealed class UpdateSheetsInput
    {
        [McpParam("要修改的图纸，一次调用可改多张", Required = true)]
        public List<SheetUpdateSpec> Sheets { get; set; }

        [McpParam("数量超过单次上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    public sealed class SheetUpdateResult
    {
        [McpParam("对应 sheets 数组中的下标，从 0 起")]
        public int Index { get; set; }

        [McpParam("图纸 ID")]
        public string Id { get; set; }

        [McpParam("修改前的编号")]
        public string OldNumber { get; set; }

        [McpParam("修改后的编号")]
        public string Number { get; set; }

        [McpParam("修改后的名称")]
        public string Name { get; set; }

        [McpParam("实际被写入的参数名")]
        public List<string> ChangedParameters { get; set; } = new List<string>();
    }

    public sealed class UpdateSheetsOutput : IReportsAffectedElements
    {
        [McpParam("成功修改的图纸数")]
        public int Changed { get; set; }

        [McpParam("是否用了两阶段改号（编号互换或循环时需要）")]
        public bool UsedTwoPhaseRenumber { get; set; }

        [McpParam("逐张图纸的结果")]
        public List<SheetUpdateResult> Sheets { get; set; } = new List<SheetUpdateResult>();

        int IReportsAffectedElements.AffectedElements => Changed;
    }

    /// <summary>
    /// 批量改图纸编号、名称与图签信息。
    ///
    /// 重排编号这件事有个绕不开的坑：图纸编号在项目内必须唯一，
    /// 所以把 A-101 改成 A-102、同时把 A-102 改成 A-103，
    /// 按顺序一个个设一定会在第一步就撞号失败。这个工具检测到这种情况时
    /// 会自动走两阶段——先全部改成临时编号，再改成目标编号。
    /// 这是调用方自己没法可靠做到的事，所以它属于工具而不属于调用方。
    /// </summary>
    [McpTool("revit_update_sheets",
        Toolsets = new[] { Toolsets.Documentation },
        Title = "修改图纸编号与图签",
        Description = "批量修改图纸的编号、名称与图签参数（审核者、设计者、日期这类）。" +
                      "编号成批互换或循环移位时会自动走两阶段改号，不会因为中途撞号而失败。" +
                      "参数名请先用 revit_get_sheet_contents 查——它们随项目样板和语言变化。" +
                      "整批要么全成、要么全不动。",
        TimeoutSeconds = 120)]
    public sealed class UpdateSheetsTool : RevitTool<UpdateSheetsInput, UpdateSheetsOutput>
    {
        /// <summary>
        /// 两阶段改号时的临时前缀。
        ///
        /// **不能用 <c>~</c>。** Revit 的图纸编号有一套非法字符
        /// （<c>\ : { } [ ] | ; &lt; &gt; ? ` ~</c>），用了其中任何一个，
        /// <c>Parameter.Set</c> 会直接返回 false。而这条路径只有在编号互换或循环移位时
        /// 才走得到，常规改号一路正常——所以这个错误极难被发现。
        /// <c>#</c> 合法，且几乎不会出现在真实的图纸编号里。
        /// </summary>
        private const string TempPrefix = "##MCP-TMP-";

        public override UpdateSheetsOutput Execute(
            UpdateSheetsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);
            RequireBatch(input.Sheets, input.Confirm, context, "修改");

            var targets = new List<Tuple<int, ViewSheet, SheetUpdateSpec>>();
            var seen = new HashSet<long>();

            for (var index = 0; index < input.Sheets.Count; index++)
            {
                var spec = input.Sheets[index];
                if (spec == null)
                    throw SheetUpdateFail.At(index, McpDomainError.InvalidParameter, "该项为 null。");

                var sheet = RequireSheet(document, spec.SheetId, index);

                if (!seen.Add(sheet.Id.GetValue()))
                    throw SheetUpdateFail.At(index, McpDomainError.InvalidParameter,
                        "图纸 " + spec.SheetId + " 在本次调用里出现了两次，" +
                        "两条指令会互相覆盖。请合并成一条。");

                targets.Add(Tuple.Create(index, sheet, spec));
            }

            var output = new UpdateSheetsOutput();
            output.UsedTwoPhaseRenumber = ApplyNumbers(document, targets, output);

            foreach (var target in targets)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var index = target.Item1;
                var sheet = target.Item2;
                var spec = target.Item3;

                var result = output.Sheets.First(s => s.Index == index);

                if (!string.IsNullOrEmpty(spec.Name)) SetName(sheet, spec.Name, index);

                WriteParameters(sheet, spec.Parameters, result, index, "parameters");

                if (spec.TitleBlockParameters != null && spec.TitleBlockParameters.Count > 0)
                {
                    var titleBlock = SheetSupport.TitleBlockOf(document, sheet);

                    if (titleBlock == null)
                        throw SheetUpdateFail.At(index, McpDomainError.InvalidParameter,
                            "图纸「" + SheetSupport.NumberOf(sheet) + "」上没有标题栏实例，" +
                            "titleBlockParameters 无处可写。");

                    WriteParameters(titleBlock, spec.TitleBlockParameters, result, index,
                        "titleBlockParameters");
                }

                result.Number = SheetSupport.NumberOf(sheet);
                result.Name = AnnotationSupport.SafeName(sheet);
            }

            output.Changed = output.Sheets.Count;
            return output;
        }

        /// <summary>
        /// 设置编号。检测到目标编号与本批内其他图纸的**当前**编号冲突时走两阶段。
        /// 返回是否用了两阶段。
        /// </summary>
        private static bool ApplyNumbers(
            Document document, IList<Tuple<int, ViewSheet, SheetUpdateSpec>> targets,
            UpdateSheetsOutput output)
        {
            var changes = new List<Tuple<int, ViewSheet, string>>();

            foreach (var target in targets)
            {
                var result = new SheetUpdateResult
                {
                    Index = target.Item1,
                    Id = target.Item2.Id.ToProtocolString(),
                    OldNumber = SheetSupport.NumberOf(target.Item2)
                };
                output.Sheets.Add(result);

                var wanted = target.Item3.Number;
                if (string.IsNullOrWhiteSpace(wanted)) continue;

                wanted = wanted.Trim();
                if (wanted == result.OldNumber) continue;

                changes.Add(Tuple.Create(target.Item1, target.Item2, wanted));
            }

            if (changes.Count == 0) return false;

            RejectDuplicatesWithin(changes);
            RejectCollisionsOutside(document, changes);

            // 目标编号撞上了本批里某张图纸的当前编号 → 顺序设置一定会中途失败。
            // 典型场景就是整体后移一位：A-101→A-102、A-102→A-103……
            var currentNumbers = new HashSet<string>(
                changes.Select(c => SheetSupport.NumberOf(c.Item2)).Where(n => n != null),
                StringComparer.OrdinalIgnoreCase);

            var needsTwoPhase = changes.Any(c => currentNumbers.Contains(c.Item3));

            if (needsTwoPhase)
            {
                var prefix = FreeTempPrefix(document);

                for (var i = 0; i < changes.Count; i++)
                    SetNumber(changes[i].Item2, prefix + i, changes[i].Item1, temporary: true);
            }

            foreach (var change in changes)
                SetNumber(change.Item2, change.Item3, change.Item1, temporary: false);

            return needsTwoPhase;
        }

        /// <summary>
        /// 找一个不与任何现有图纸编号冲突的临时前缀。
        ///
        /// 直接用固定前缀在 99.99% 的项目里都没事，但真撞上时的表现是
        /// 一半图纸顶着临时编号、事务回滚——而排查的人完全想不到是前缀撞了。
        /// 这里的代价只是一次遍历。
        /// </summary>
        private static string FreeTempPrefix(Document document)
        {
            var taken = new HashSet<string>(
                SheetSupport.AllSheets(document)
                    .Select(SheetSupport.NumberOf)
                    .Where(n => n != null),
                StringComparer.OrdinalIgnoreCase);

            for (var attempt = 0; attempt < 100; attempt++)
            {
                var prefix = attempt == 0 ? TempPrefix : TempPrefix + attempt + "-";

                if (!taken.Any(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    return prefix;
            }

            throw new ToolFailureException(McpDomainError.TransactionFailed,
                "找不到可用的临时编号前缀——项目里已经有大量以 " + TempPrefix +
                " 开头的图纸编号。请先把它们改掉。");
        }

        /// <summary>
        /// 本批内两张图纸要同一个编号。这一定失败，且在两阶段改号之后才会暴露——
        /// 那时前一半图纸已经顶着临时编号了。提前拦下来。
        /// </summary>
        private static void RejectDuplicatesWithin(IList<Tuple<int, ViewSheet, string>> changes)
        {
            var duplicate = changes
                .GroupBy(c => c.Item3, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);

            if (duplicate == null) return;

            throw SheetUpdateFail.At(duplicate.First().Item1, McpDomainError.InvalidParameter,
                "本次调用里有 " + duplicate.Count() + " 张图纸都要改成编号「" + duplicate.Key +
                "」。图纸编号在项目内必须唯一。");
        }

        /// <summary>目标编号撞上了本批之外的图纸——那张图纸不会被改，冲突无解。</summary>
        private static void RejectCollisionsOutside(
            Document document, IList<Tuple<int, ViewSheet, string>> changes)
        {
            var changing = new HashSet<long>(changes.Select(c => c.Item2.Id.GetValue()));

            var occupied = new Dictionary<string, ViewSheet>(StringComparer.OrdinalIgnoreCase);
            foreach (var sheet in SheetSupport.AllSheets(document))
            {
                if (changing.Contains(sheet.Id.GetValue())) continue;

                var number = SheetSupport.NumberOf(sheet);
                if (number != null) occupied[number] = sheet;
            }

            foreach (var change in changes)
            {
                ViewSheet blocker;
                if (!occupied.TryGetValue(change.Item3, out blocker)) continue;

                throw SheetUpdateFail.At(change.Item1, McpDomainError.InvalidParameter,
                    "编号「" + change.Item3 + "」已经被图纸「" +
                    AnnotationSupport.SafeName(blocker) + "」（ID " +
                    blocker.Id.ToProtocolString() + "）占用，而那张图纸不在本次修改范围内。" +
                    "把它一起纳入这次调用，或换一个编号。");
            }
        }

        private static void SetNumber(ViewSheet sheet, string number, int index, bool temporary)
        {
            try
            {
                var parameter = sheet.get_Parameter(BuiltInParameter.SHEET_NUMBER);
                if (parameter != null && !parameter.IsReadOnly && parameter.Set(number)) return;
            }
            catch (Exception ex)
            {
                throw SheetUpdateFail.At(index, McpDomainError.TransactionFailed,
                    "图纸编号设为「" + number + "」失败：" + ex.Message);
            }

            // Revit 拒绝的原因只有两种：重号，或者用了非法字符。
            // 后者最容易被忽略——Parameter.Set 只返回 false，不说为什么
            var illegal = IllegalCharactersIn(number);

            throw SheetUpdateFail.At(index, McpDomainError.TransactionFailed,
                "图纸编号设为「" + number + "」被 Revit 拒绝" +
                (illegal != null
                    ? "：它含有 Revit 图纸编号不允许的字符 " + illegal + "。" +
                      "非法字符是 \\ : { } [ ] | ; < > ? ` ~ 这几个。"
                    : temporary
                        ? "（这是两阶段改号的临时编号，正常不该冲突）。"
                        : "，多半是已有同号图纸。"));
        }

        /// <summary>
        /// Revit 不允许出现在图纸编号（以及大多数名称）里的字符。
        /// 返回其中出现了哪些，没有则返回 null。
        /// </summary>
        private static string IllegalCharactersIn(string value)
        {
            const string illegal = "\\:{}[]|;<>?`~";

            var found = value.Where(c => illegal.IndexOf(c) >= 0).Distinct().ToArray();

            return found.Length == 0 ? null : string.Join(" ", found.Select(c => c.ToString()).ToArray());
        }

        private static void SetName(ViewSheet sheet, string name, int index)
        {
            try
            {
                var parameter = sheet.get_Parameter(BuiltInParameter.SHEET_NAME);
                if (parameter != null && !parameter.IsReadOnly && parameter.Set(name)) return;
            }
            catch (Exception ex)
            {
                throw SheetUpdateFail.At(index, McpDomainError.TransactionFailed,
                    "图纸名称设为「" + name + "」失败：" + ex.Message);
            }

            throw SheetUpdateFail.At(index, McpDomainError.TransactionFailed,
                "图纸名称设为「" + name + "」被 Revit 拒绝。");
        }

        private static void WriteParameters(
            Element target, IList<ParameterValueSpec> values, SheetUpdateResult result,
            int index, string fieldName)
        {
            if (values == null) return;

            foreach (var entry in values)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Name))
                    throw SheetUpdateFail.At(index, McpDomainError.InvalidParameter,
                        fieldName + " 里有条目缺少 name。");

                var name = entry.Name.Trim();
                var parameter = target.LookupParameter(name);

                if (parameter == null)
                    throw SheetUpdateFail.At(index, McpDomainError.InvalidParameter,
                        "找不到参数 \"" + name + "\"。用 revit_get_sheet_contents 查这张图纸上" +
                        "实际可用的参数名——参数名随项目样板和语言变化，不要猜。");

                if (parameter.IsReadOnly)
                    throw SheetUpdateFail.At(index, McpDomainError.InvalidParameter,
                        "参数 \"" + name + "\" 是只读的，改不了。" +
                        "图纸上由 Revit 自己维护的字段（如「图纸总数」）都是只读的。");

                ParameterWriter.Write(parameter, entry.Value ?? string.Empty, target);
                result.ChangedParameters.Add(name);
            }
        }

        internal static ViewSheet RequireSheet(Document document, string rawId, int index)
        {
            var element = CreateSupport.RequireElement(document, rawId, index);
            var sheet = element as ViewSheet;

            if (sheet == null)
                throw SheetUpdateFail.At(index, McpDomainError.InvalidParameter,
                    "sheetId " + rawId + " 不是图纸，而是「" +
                    (element.Category?.Name ?? element.GetType().Name) +
                    "」。用 revit_list_views 查 DrawingSheet 取图纸 ID。");

            return sheet;
        }
    }

    // ==================== 复制图纸 ====================

    public sealed class DuplicateSheetSpec
    {
        [McpParam("要复制的图纸 ID", Required = true)]
        public string SheetId { get; set; }

        [McpParam("新图纸的编号。必填——图纸编号必须唯一，没法自动想一个合理的", Required = true)]
        public string Number { get; set; }

        [McpParam("新图纸的名称。省略则沿用原图纸的名称")]
        public string Name { get; set; }
    }

    public sealed class DuplicateSheetsInput
    {
        [McpParam("要复制的图纸，一次调用可复制多张", Required = true)]
        public List<DuplicateSheetSpec> Sheets { get; set; }

        [McpParam("新图纸上要带什么，默认 viewsAndDetailing。" +
                  "empty：只要图纸和图签，纸上是空的。" +
                  "views：连视图一起复制——**会为每个视图生成一份新视图**，" +
                  "不是把原视图挪过去（一个视图只能放在一张图纸上）。" +
                  "detailing：只带图纸上的详图项目与注释，不复制视图。" +
                  "viewsAndDetailing：两者都带",
                  AllowedValues = new[] { "viewsAndDetailing", "views", "detailing", "empty" })]
        public string Contents { get; set; }

        [McpParam("数量超过单次上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    public sealed class DuplicatedSheet
    {
        [McpParam("对应 sheets 数组中的下标，从 0 起")]
        public int Index { get; set; }

        [McpParam("新图纸 ID")]
        public string Id { get; set; }

        [McpParam("新图纸编号")]
        public string Number { get; set; }

        [McpParam("新图纸名称")]
        public string Name { get; set; }

        [McpParam("源图纸编号")]
        public string CopiedFrom { get; set; }

        [McpParam("新图纸上的视口数")]
        public int ViewportCount { get; set; }

        [McpParam("为这张图纸新生成的视图 ID。contents 含 views 时才有——" +
                  "它们是**新的视图**，不是原来那些")]
        public List<string> NewViewIds { get; set; }

        [McpParam("用的是 Revit 原生复制（native）还是工具的手工复制（manual）")]
        public string Method { get; set; }
    }

    public sealed class DuplicateSheetsOutput : IReportsAffectedElements
    {
        [McpParam("成功复制的图纸数")]
        public int Created { get; set; }

        [McpParam("新建的图纸，顺序与入参一致")]
        public List<DuplicatedSheet> Sheets { get; set; } = new List<DuplicatedSheet>();

        int IReportsAffectedElements.AffectedElements => Created;
    }

    /// <summary>
    /// 复制图纸。
    ///
    /// 2022 起 Revit 提供了原生的图纸复制；更早的版本没有这个入口，
    /// 退回"新建图纸 + 逐个复制视图 + 抄一遍可写参数"的手工复制。
    /// 两条路径产出的东西**不完全一样**——手工复制搬不了图纸上的详图项目——
    /// 所以回执里的 method 字段明说用的是哪一条。
    /// 悄悄降级产出一个少了东西的图纸，比直接失败更难发现。
    /// </summary>
    [McpTool("revit_duplicate_sheets",
        Toolsets = new[] { Toolsets.Documentation },
        Title = "复制图纸",
        Description = "复制一批图纸。新编号必须显式给出且唯一。" +
                      "contents 为 views 时，Revit 会**为图纸上的每个视图生成一份新视图**" +
                      "再摆到新图纸上——因为一个视图只能放在一张图纸上。" +
                      "新视图的 ID 在回执的 newViewIds 里。" +
                      "回执的 method 会说明用的是 Revit 原生复制还是工具的手工复制，" +
                      "手工复制（Revit 2021 及以下）搬不了图纸上的详图项目。",
        Destructive = false,
        TimeoutSeconds = 180)]
    public sealed class DuplicateSheetsTool : RevitTool<DuplicateSheetsInput, DuplicateSheetsOutput>
    {
        public override DuplicateSheetsOutput Execute(
            DuplicateSheetsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);
            RequireBatch(input.Sheets, input.Confirm, context, "复制");

            var contents = ParseContents(input.Contents);

            if (!SheetCompat.CanDuplicateNatively && NeedsDetailing(contents))
                context.Warnings.Add(
                    "contents 含 detailing 需要 Revit 2022 及以上，当前是 Revit " +
                    RevitVersionInfo.Year + "。图纸上的详图项目与注释不会被复制过去，" +
                    "其余部分照常复制。");

            var output = new DuplicateSheetsOutput();
            var total = input.Sheets.Count;

            for (var index = 0; index < total; index++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var spec = input.Sheets[index];
                if (spec == null)
                    throw SheetUpdateFail.At(index, McpDomainError.InvalidParameter, "该项为 null。");

                if (string.IsNullOrWhiteSpace(spec.Number))
                    throw SheetUpdateFail.At(index, McpDomainError.InvalidParameter,
                        "number 不能为空——图纸编号必须唯一，工具不会替你想一个。");

                output.Sheets.Add(DuplicateOne(document, context, spec, index, contents));
                ProgressTicker.Tick(context.Progress, index + 1, total, "已复制");
            }

            output.Created = output.Sheets.Count;
            return output;
        }

        private static SheetCopyContents ParseContents(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return SheetCopyContents.ViewsAndDetailing;

            switch (value.Trim().ToLowerInvariant())
            {
                case "empty": return SheetCopyContents.Empty;
                case "views": return SheetCopyContents.Views;
                case "detailing": return SheetCopyContents.Detailing;
                case "viewsanddetailing": return SheetCopyContents.ViewsAndDetailing;

                default:
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "无法识别的 contents \"" + value +
                        "\"。可用值：empty、views、detailing、viewsAndDetailing。");
            }
        }

        private static bool NeedsDetailing(SheetCopyContents contents)
        {
            return contents == SheetCopyContents.Detailing ||
                   contents == SheetCopyContents.ViewsAndDetailing;
        }

        private static bool NeedsViews(SheetCopyContents contents)
        {
            return contents == SheetCopyContents.Views ||
                   contents == SheetCopyContents.ViewsAndDetailing;
        }

        private static DuplicatedSheet DuplicateOne(
            Document document, ToolExecutionContext<UIApplication> context,
            DuplicateSheetSpec spec, int index, SheetCopyContents contents)
        {
            var source = UpdateSheetsTool.RequireSheet(document, spec.SheetId, index);

            var result = new DuplicatedSheet
            {
                Index = index,
                CopiedFrom = SheetSupport.NumberOf(source)
            };

            var nativeId = SheetCompat.TryDuplicate(source, contents);
            ViewSheet sheet;

            if (nativeId != null && nativeId != ElementId.InvalidElementId)
            {
                sheet = document.GetElement(nativeId) as ViewSheet;
                result.Method = "native";
            }
            else
            {
                sheet = ManualDuplicate(document, context, source, index, contents, result);
                result.Method = "manual";
            }

            if (sheet == null)
                throw SheetUpdateFail.At(index, McpDomainError.TransactionFailed,
                    "复制图纸「" + result.CopiedFrom + "」失败：Revit 没有返回新图纸。");

            var viewports = SheetSupport.ViewportsOf(sheet);
            result.ViewportCount = viewports.Count;

            // 原生复制不告诉我们新视图是哪些，从新图纸的视口上回读
            if (result.Method == "native" && NeedsViews(contents))
                result.NewViewIds = viewports
                    .Select(SafeViewId)
                    .Where(id => id != null)
                    .ToList();

            SetNumberOrFail(sheet, spec.Number.Trim(), index);

            if (!string.IsNullOrWhiteSpace(spec.Name))
                TrySetName(sheet, spec.Name.Trim(), context, index);

            result.Id = sheet.Id.ToProtocolString();
            result.Number = SheetSupport.NumberOf(sheet);
            result.Name = AnnotationSupport.SafeName(sheet);

            return result;
        }

        /// <summary>手工复制：新建一张用同一个标题栏的图纸，抄一遍可写参数，按需复制视图。</summary>
        private static ViewSheet ManualDuplicate(
            Document document, ToolExecutionContext<UIApplication> context,
            ViewSheet source, int index, SheetCopyContents contents, DuplicatedSheet result)
        {
            var sourceTitleBlock = SheetSupport.TitleBlockOf(document, source);
            var titleBlockTypeId = sourceTitleBlock?.GetTypeId() ?? ElementId.InvalidElementId;

            ViewSheet sheet;
            try
            {
                sheet = ViewSheet.Create(document, titleBlockTypeId);
            }
            catch (Exception ex)
            {
                throw SheetUpdateFail.At(index, McpDomainError.TransactionFailed,
                    "创建新图纸失败：" + ex.Message);
            }

            if (sheet == null)
                throw SheetUpdateFail.At(index, McpDomainError.TransactionFailed,
                    "Revit 未能创建图纸，但也没有报错。");

            CopyWritableParameters(source, sheet);

            var newTitleBlock = SheetSupport.TitleBlockOf(document, sheet);
            if (sourceTitleBlock != null && newTitleBlock != null)
                CopyWritableParameters(sourceTitleBlock, newTitleBlock);

            // 名称默认跟着源图纸走；调用方给了 name 会在外面再覆盖一次
            var sourceName = AnnotationSupport.SafeName(source);
            if (!string.IsNullOrEmpty(sourceName))
            {
                try
                {
                    var parameter = sheet.get_Parameter(BuiltInParameter.SHEET_NAME);
                    if (parameter != null && !parameter.IsReadOnly) parameter.Set(sourceName);
                }
                catch { /* 名字抄不过去不值得让复制失败 */ }
            }

            if (NeedsViews(contents))
                result.NewViewIds = CopyViews(document, context, source, sheet, index, contents);

            return sheet;
        }

        /// <summary>
        /// 逐个复制视图再摆到新图纸上。
        ///
        /// 复制而不是搬移，是因为一个视图只能放在一张图纸上——
        /// 搬过去就等于把原图纸掏空了，那不是"复制图纸"该有的行为。
        /// </summary>
        private static List<string> CopyViews(
            Document document, ToolExecutionContext<UIApplication> context,
            ViewSheet source, ViewSheet target, int index, SheetCopyContents contents)
        {
            var option = NeedsDetailing(contents)
                ? ViewDuplicateOption.WithDetailing
                : ViewDuplicateOption.Duplicate;

            var newIds = new List<string>();
            var skipped = 0;

            foreach (var viewport in SheetSupport.ViewportsOf(source))
            {
                XYZ center;
                View view;

                try
                {
                    center = viewport.GetBoxCenter();
                    view = document.GetElement(viewport.ViewId) as View;
                }
                catch
                {
                    skipped++;
                    continue;
                }

                if (view == null)
                {
                    skipped++;
                    continue;
                }

                try
                {
                    if (!view.CanViewBeDuplicated(option))
                    {
                        skipped++;
                        continue;
                    }

                    var copyId = view.Duplicate(option);
                    if (copyId == null || copyId == ElementId.InvalidElementId)
                    {
                        skipped++;
                        continue;
                    }

                    Viewport.Create(document, target.Id, copyId, center);
                    newIds.Add(copyId.ToProtocolString());
                }
                catch
                {
                    // 明细表这类不能复制的视图会走到这里。数出来，不让它悄悄消失
                    skipped++;
                }
            }

            if (skipped > 0)
                CreateSupport.Once(context,
                    "sheets[" + index + "]：有 " + skipped +
                    " 个视图没能复制到新图纸上（明细表与部分视图类型不支持复制）。" +
                    "新图纸上会少这几个视口。");

            return newIds;
        }

        /// <summary>
        /// 抄一遍可写参数。
        /// 编号与名称跳过：前者必须唯一、后者由调用方决定，
        /// 在这里抄过去只会立刻被覆盖或直接撞号。
        /// </summary>
        private static void CopyWritableParameters(Element source, Element target)
        {
            foreach (Parameter parameter in source.Parameters)
            {
                if (parameter == null || parameter.IsReadOnly || !parameter.HasValue) continue;

                var definition = parameter.Definition;
                if (definition == null) continue;

                var internalDefinition = definition as InternalDefinition;
                if (internalDefinition != null)
                {
                    var builtIn = internalDefinition.BuiltInParameter;
                    if (builtIn == BuiltInParameter.SHEET_NUMBER ||
                        builtIn == BuiltInParameter.SHEET_NAME) continue;
                }

                var destination = target.get_Parameter(definition);
                if (destination == null || destination.IsReadOnly) continue;

                try
                {
                    switch (parameter.StorageType)
                    {
                        case StorageType.String: destination.Set(parameter.AsString()); break;
                        case StorageType.Integer: destination.Set(parameter.AsInteger()); break;
                        case StorageType.Double: destination.Set(parameter.AsDouble()); break;
                        case StorageType.ElementId: destination.Set(parameter.AsElementId()); break;
                    }
                }
                catch { /* 单个参数抄不过去就跳过，不值得让整张图纸失败 */ }
            }
        }

        private static string SafeViewId(Viewport viewport)
        {
            try { return viewport.ViewId?.ToProtocolString(); }
            catch { return null; }
        }

        private static void SetNumberOrFail(ViewSheet sheet, string number, int index)
        {
            try
            {
                var parameter = sheet.get_Parameter(BuiltInParameter.SHEET_NUMBER);
                if (parameter != null && !parameter.IsReadOnly && parameter.Set(number)) return;
            }
            catch (Exception ex)
            {
                throw SheetUpdateFail.At(index, McpDomainError.TransactionFailed,
                    "新图纸编号设为「" + number + "」失败：" + ex.Message + "（整批未创建）。");
            }

            throw SheetUpdateFail.At(index, McpDomainError.TransactionFailed,
                "新图纸编号设为「" + number + "」被 Revit 拒绝，多半是已有同号图纸（整批未创建）。");
        }

        private static void TrySetName(
            ViewSheet sheet, string name, ToolExecutionContext<UIApplication> context, int index)
        {
            try
            {
                var parameter = sheet.get_Parameter(BuiltInParameter.SHEET_NAME);
                if (parameter != null && !parameter.IsReadOnly && parameter.Set(name)) return;
            }
            catch { /* 落到下面的警告 */ }

            CreateSupport.Once(context,
                "sheets[" + index + "]：新图纸名称没能设成「" + name + "」。");
        }
    }

    // ==================== 图纸内容 ====================

    public sealed class GetSheetContentsInput
    {
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档")]
        public string DocumentId { get; set; }

        [McpParam("图纸 ID。省略则返回所有图纸的内容（图纸多时返回值会很大）")]
        public string SheetId { get; set; }

        [McpParam("是否返回图纸与标题栏的参数清单，默认 true。" +
                  "写图签之前靠它确认参数名——参数名随项目样板和语言变化")]
        public bool? IncludeParameters { get; set; }
    }

    public sealed class ViewportInfo
    {
        [McpParam("视口 ID。要把视图从图纸上拿掉就删这个 ID")]
        public string Id { get; set; }

        [McpParam("视口里那个视图的 ID")]
        public string ViewId { get; set; }

        [McpParam("视图名")]
        public string ViewName { get; set; }

        [McpParam("视图类型")]
        public string ViewType { get; set; }

        [McpParam("视口中心在图纸上的位置，毫米")]
        public Point3D Center { get; set; }

        [McpParam("详图编号（图纸上标注的那个序号）")]
        public string DetailNumber { get; set; }
    }

    public sealed class SheetContents
    {
        [McpParam("图纸 ID")]
        public string Id { get; set; }

        [McpParam("图纸编号")]
        public string Number { get; set; }

        [McpParam("图纸名称")]
        public string Name { get; set; }

        [McpParam("标题栏类型名，没有标题栏则为 null")]
        public string TitleBlock { get; set; }

        [McpParam("标题栏实例 ID，没有则为 null")]
        public string TitleBlockId { get; set; }

        [McpParam("是否已发布（图纸的「已出图」属性）")]
        public bool Placeholder { get; set; }

        [McpParam("图纸上的视口")]
        public List<ViewportInfo> Viewports { get; set; } = new List<ViewportInfo>();

        [McpParam("挂在这张图纸上的修订编号")]
        public List<string> Revisions { get; set; } = new List<string>();

        [McpParam("图纸自身的可写参数。写图签用 revit_update_sheets 的 parameters")]
        public List<ParameterValue> Parameters { get; set; }

        [McpParam("标题栏实例的可写参数。写它们用 revit_update_sheets 的 titleBlockParameters")]
        public List<ParameterValue> TitleBlockParameters { get; set; }
    }

    public sealed class GetSheetContentsOutput
    {
        [McpParam("图纸数")]
        public int Total { get; set; }

        [McpParam("图纸内容，按编号排序")]
        public List<SheetContents> Sheets { get; set; } = new List<SheetContents>();
    }

    [McpTool("revit_get_sheet_contents",
        Toolsets = new[] { Toolsets.Documentation },
        Title = "查看图纸内容",
        Description = "返回图纸上放了哪些视图、用的什么标题栏、挂了哪些修订，" +
                      "以及图纸与标题栏上可写的参数名。" +
                      "**写图签之前先调它**：参数名随项目样板和语言变化，猜不出来。" +
                      "要把某个视图从图纸上拿掉，删回执里的视口 ID（用 revit_delete_elements）。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class GetSheetContentsTool : RevitTool<GetSheetContentsInput, GetSheetContentsOutput>
    {
        public override GetSheetContentsOutput Execute(
            GetSheetContentsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = ResolveDocument(context, input.DocumentId);
            var includeParameters = input.IncludeParameters ?? true;

            var sheets = string.IsNullOrWhiteSpace(input.SheetId)
                ? SheetSupport.AllSheets(document).ToList()
                : new List<ViewSheet> { UpdateSheetsTool.RequireSheet(document, input.SheetId, -1) };

            var output = new GetSheetContentsOutput();

            foreach (var sheet in sheets)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var titleBlock = SheetSupport.TitleBlockOf(document, sheet);

                var contents = new SheetContents
                {
                    Id = sheet.Id.ToProtocolString(),
                    Number = SheetSupport.NumberOf(sheet),
                    Name = AnnotationSupport.SafeName(sheet),
                    TitleBlockId = titleBlock?.Id.ToProtocolString(),
                    TitleBlock = titleBlock == null
                        ? null
                        : AnnotationSupport.SafeName(document.GetElement(titleBlock.GetTypeId()))
                };

                try { contents.Placeholder = sheet.IsPlaceholder; }
                catch { /* 少数版本读不到，留默认值 */ }

                foreach (var viewport in SheetSupport.ViewportsOf(sheet))
                    contents.Viewports.Add(Describe(document, viewport));

                contents.Revisions = RevisionsOf(document, sheet);

                if (includeParameters)
                {
                    contents.Parameters = WritableParameters(sheet);
                    if (titleBlock != null)
                        contents.TitleBlockParameters = WritableParameters(titleBlock);
                }

                output.Sheets.Add(contents);
            }

            output.Sheets = output.Sheets
                .OrderBy(s => s.Number, StringComparer.CurrentCulture)
                .ToList();

            output.Total = output.Sheets.Count;
            return output;
        }

        private static ViewportInfo Describe(Document document, Viewport viewport)
        {
            var info = new ViewportInfo { Id = viewport.Id.ToProtocolString() };

            try
            {
                var viewId = viewport.ViewId;
                info.ViewId = viewId?.ToProtocolString();

                var view = document.GetElement(viewId) as View;
                if (view != null)
                {
                    info.ViewName = AnnotationSupport.SafeName(view);
                    info.ViewType = view.ViewType.ToString();
                }
            }
            catch { /* 视口指向的视图没了，留空 */ }

            try
            {
                var center = viewport.GetBoxCenter();
                info.Center = new Point3D
                {
                    X = Units.Round(Units.FromFeet(center.X)),
                    Y = Units.Round(Units.FromFeet(center.Y)),
                    Z = Units.Round(Units.FromFeet(center.Z))
                };
            }
            catch { /* 拿不到位置不影响其他字段 */ }

            try
            {
                var parameter = viewport.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER);
                info.DetailNumber = parameter?.AsString();
            }
            catch { /* 没有详图编号很正常 */ }

            return info;
        }

        private static List<string> RevisionsOf(Document document, ViewSheet sheet)
        {
            var results = new List<string>();

            try
            {
                foreach (var id in sheet.GetAllRevisionIds())
                {
                    var revision = document.GetElement(id) as Revision;
                    if (revision == null) continue;

                    string number;
                    try { number = revision.RevisionNumber; }
                    catch { number = revision.SequenceNumber.ToString(CultureInfo.InvariantCulture); }

                    results.Add(number);
                }
            }
            catch { /* 这张图纸上没有修订 */ }

            return results;
        }

        /// <summary>
        /// 只列可写参数。
        ///
        /// 这个工具存在的理由是"写图签之前先知道有哪些参数名可用"，
        /// 把几十个只读参数一并倒出来只会淹没那几个真正能写的。
        /// </summary>
        private static List<ParameterValue> WritableParameters(Element element)
        {
            var results = new List<ParameterValue>();

            foreach (Parameter parameter in element.Parameters)
            {
                if (parameter == null || parameter.IsReadOnly) continue;
                if (parameter.Definition == null) continue;

                results.Add(new ParameterValue
                {
                    Name = parameter.Definition.Name,
                    StorageType = parameter.StorageType.ToString(),
                    IsReadOnly = false,
                    DisplayValue = ParameterWriter.DisplayOf(parameter)
                });
            }

            return results
                .OrderBy(p => p.Name, StringComparer.CurrentCulture)
                .ToList();
        }
    }

    // ==================== 图纸共用零件 ====================

    internal static class SheetSupport
    {
        public static IEnumerable<ViewSheet> AllSheets(Document document)
        {
            return new FilteredElementCollector(document)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>();
        }

        public static string NumberOf(ViewSheet sheet)
        {
            try { return sheet.SheetNumber; }
            catch { return null; }
        }

        public static List<Viewport> ViewportsOf(ViewSheet sheet)
        {
            var results = new List<Viewport>();

            try
            {
                foreach (var id in sheet.GetAllViewports())
                {
                    var viewport = sheet.Document.GetElement(id) as Viewport;
                    if (viewport != null) results.Add(viewport);
                }
            }
            catch { /* 拿不到视口时返回空表，调用方按"这张图纸是空的"处理 */ }

            return results;
        }

        /// <summary>图纸上的标题栏实例。一张图纸通常只有一个，多个时取第一个。</summary>
        public static FamilyInstance TitleBlockOf(Document document, ViewSheet sheet)
        {
            try
            {
                return new FilteredElementCollector(document, sheet.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>()
                    .FirstOrDefault();
            }
            catch { return null; }
        }
    }
}
