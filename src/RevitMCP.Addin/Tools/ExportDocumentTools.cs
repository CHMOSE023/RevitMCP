using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    // ==================== 模型与图纸导出 ====================

    public sealed class ExportDocumentsInput
    {
        [McpParam("导出格式：dwg、dxf、pdf（图纸/视图），ifc、nwc（整个模型）", Required = true,
                  AllowedValues = new[] { "dwg", "dxf", "pdf", "ifc", "nwc" })]
        public string Format { get; set; }

        [McpParam("文件名（不带路径）。dwg/dxf/pdf 分文件导出时，Revit 会在它后面拼上视图名。" +
                  "所有导出一律落在服务的导出目录下，回执给出完整路径", Required = true)]
        public string FileName { get; set; }

        [McpParam("要导出的视图或图纸 ID，来自 revit_list_views。" +
                  "dwg、dxf、pdf 必填；ifc 与 nwc 导的是整个模型，给了也只用第一个作为导出范围。ElementId 与 uniqueId 两种写法都接受")]
        public List<string> ViewIds { get; set; }

        [McpParam("pdf 专用：是否把所有图纸合成一个 PDF，默认 true。" +
                  "false 表示每张图纸一个文件")]
        public bool? Combine { get; set; }

        [McpParam("ifc 专用：IFC 版本，如 IFC2x3CV2（默认）、IFC4、IFC2x3、IFC4RV。" +
                  "交付前请与对方确认要哪个版本——版本不对，对方的软件可能读不了")]
        public string IfcVersion { get; set; }

        [McpParam("ifc 专用：是否导出基本量（体积、面积这些），默认 false。" +
                  "算量方要的话开它，会让文件变大")]
        public bool? ExportBaseQuantities { get; set; }

        [McpParam("dwg/dxf 专用：图层映射标准，如 AIA（默认）、ISO13567、CP83、BS1192。" +
                  "决定构件落到哪个图层名上",
                  AllowedValues = new[] { "AIA", "ISO13567", "CP83", "BS1192" })]
        public string LayerStandard { get; set; }

        [McpParam("dwg/dxf 专用：是否把图纸上的视图合并成一份（不分外部参照），默认 true")]
        public bool? MergeViews { get; set; }

        [McpParam("视图数超过单次上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    public sealed class ExportedFile
    {
        [McpParam("文件的完整路径")]
        public string Path { get; set; }

        [McpParam("文件大小，字节")]
        public long FileSizeBytes { get; set; }
    }

    public sealed class ExportDocumentsOutput
    {
        [McpParam("实际使用的格式")]
        public string Format { get; set; }

        [McpParam("产出的文件数")]
        public int FileCount { get; set; }

        [McpParam("产出的文件")]
        public List<ExportedFile> Files { get; set; } = new List<ExportedFile>();

        [McpParam("本次导出覆盖的视图/图纸名")]
        public List<string> Views { get; set; } = new List<string>();
    }

    /// <summary>
    /// 把模型或图纸导成交付格式。
    ///
    /// 五种格式合成一个工具：它们在 Revit 里都是 <c>Document.Export</c>，
    /// 而调用方要做的决定只有三个——导什么格式、导哪些视图、落到哪个文件名。
    /// 分成五个工具会把"文件名怎么落地"这件事重复五遍，
    /// 而那恰恰是最容易在几处之间走样的地方。
    /// </summary>
    [McpTool("revit_export_documents",
        Toolsets = new[] { Toolsets.Documentation },
        Title = "导出 DWG/DXF/PDF/IFC/NWC",
        Description = "把视图、图纸或整个模型导成交付格式。" +
                      "dwg、dxf、pdf 按视图导（需要 viewIds）；ifc、nwc 导整个模型。" +
                      "文件一律落在服务的导出目录下，回执给出完整路径。" +
                      "PDF 需要 Revit 2022 及以上；NWC 需要机器上装了 Navisworks 导出器——" +
                      "两者缺失时会明确告知，不会给出一个空文件。",
        Destructive = false,
        TimeoutSeconds = 600)]
    public sealed class ExportDocumentsTool : RevitTool<ExportDocumentsInput, ExportDocumentsOutput>
    {
        public override ExportDocumentsOutput Execute(
            ExportDocumentsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);
            var format = (input.Format ?? string.Empty).Trim().ToLowerInvariant();

            var extension = ExtensionOf(format);
            var perView = format == "dwg" || format == "dxf" || format == "pdf";

            var views = perView
                ? RequireViews(document, input.ViewIds, input.Confirm, context, format)
                : OptionalScopeView(document, input.ViewIds);

            var root = ExportRoot();
            var target = ExportPaths.Resolve(root, input.FileName, extension, ExportPaths.DocumentExtensions);

            var actualExtension = Path.GetExtension(target);
            if (!string.Equals(actualExtension, extension, StringComparison.OrdinalIgnoreCase))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "format 是 " + format + "，但 fileName 的扩展名是 " + actualExtension +
                    "。扩展名与实际内容对不上的文件，下游拿到才会发现——请改成 " + extension + "。");

            // Revit 导出时会自己决定最终文件名（拼视图名、分页、加序号），
            // 所以先让它落进一个空目录，再把产物挪到承诺的位置上。
            // 不这么做，回执里的路径就是错的
            var staging = ExportPaths.CreateStagingDirectory(root);
            var output = new ExportDocumentsOutput { Format = format };

            try
            {
                var baseName = Path.GetFileNameWithoutExtension(target);

                switch (format)
                {
                    case "dwg":
                    case "dxf":
                        ExportCad(document, staging, baseName, views, format, input);
                        break;

                    case "pdf":
                        PdfExportCompat.Export(
                            document, staging, baseName,
                            views.Select(v => v.Id).ToList(), input.Combine ?? true);
                        break;

                    case "ifc":
                        ExportIfc(document, staging, baseName, views.FirstOrDefault(), input);
                        break;

                    case "nwc":
                        ExportNavisworks(document, staging, baseName, views.FirstOrDefault());
                        break;

                    default:
                        throw new ToolFailureException(McpDomainError.InvalidParameter,
                            "无法识别的 format \"" + input.Format +
                            "\"。可用值：dwg、dxf、pdf、ifc、nwc。" +
                            "要导图片用 revit_export_image，要导明细表用 revit_export_schedules。");
                }

                CollectResults(staging, target, output, context);
            }
            finally
            {
                ExportPaths.SafeDelete(staging);
            }

            output.Views = views.Select(AnnotationSupport.SafeName).Where(n => n != null).ToList();
            return output;
        }

        // ==================== 各格式 ====================

        private static void ExportCad(
            Document document, string folder, string baseName, IList<View> views,
            string format, ExportDocumentsInput input)
        {
            var ids = views.Select(v => v.Id).ToList();

            try
            {
                if (format == "dwg")
                {
                    var options = new DWGExportOptions
                    {
                        LayerMapping = ResolveLayerStandard(input.LayerStandard),
                        MergedViews = input.MergeViews ?? true
                    };

                    if (!document.Export(folder, baseName, ids, options))
                        throw new ToolFailureException(McpDomainError.TransactionFailed,
                            "Revit 报告 DWG 导出失败，没有说明原因。" +
                            "常见情况是某个视图无法打印——用 revit_list_views 看 canBePrinted。");
                }
                else
                {
                    // DXF 没有 MergedViews 这个开关（只有 DWG 有），给了也只能忽略
                    var options = new DXFExportOptions
                    {
                        LayerMapping = ResolveLayerStandard(input.LayerStandard)
                    };

                    if (!document.Export(folder, baseName, ids, options))
                        throw new ToolFailureException(McpDomainError.TransactionFailed,
                            "Revit 报告 DXF 导出失败，没有说明原因。");
                }
            }
            catch (ToolFailureException) { throw; }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "Revit 导出 " + format.ToUpperInvariant() + " 失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 图层映射标准。给的名字不认识时直接失败而不是悄悄用默认值——
        /// 交付方要的是 ISO13567 却拿到 AIA 的图层名，是要返工的。
        /// </summary>
        private static string ResolveLayerStandard(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "AIA";

            var known = new[] { "AIA", "ISO13567", "CP83", "BS1192" };
            var match = known.FirstOrDefault(k => string.Equals(k, value.Trim(), StringComparison.OrdinalIgnoreCase));

            if (match == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "无法识别的 layerStandard \"" + value + "\"。可用值：" + string.Join("、", known) + "。");

            return match;
        }

        private static void ExportIfc(
            Document document, string folder, string baseName, View scope, ExportDocumentsInput input)
        {
            var options = new IFCExportOptions();

            if (!string.IsNullOrWhiteSpace(input.IfcVersion))
            {
                IFCVersion version;
                if (!Enum.TryParse(input.IfcVersion.Trim(), ignoreCase: true, out version) ||
                    !Enum.IsDefined(typeof(IFCVersion), version))
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "无法识别的 ifcVersion \"" + input.IfcVersion + "\"。本版本可用：" +
                        string.Join("、", Enum.GetNames(typeof(IFCVersion)).Take(12)) + " 等。");

                options.FileVersion = version;
            }

            if (input.ExportBaseQuantities == true) options.ExportBaseQuantities = true;

            // 给了视图就把导出范围限制在它里面——想只导某一层时靠这个
            if (scope != null) options.FilterViewId = scope.Id;

            try
            {
                document.Export(folder, baseName, options);
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "Revit 导出 IFC 失败：" + ex.Message +
                    "。IFC 导出依赖 Revit 自带的 IFC 导出器，它是一个独立安装的组件——" +
                    "报错提到找不到导出器时，需要用户先装上它。");
            }
        }

        private static void ExportNavisworks(Document document, string folder, string baseName, View scope)
        {
            NavisworksExportOptions options;
            try
            {
                options = new NavisworksExportOptions
                {
                    ExportScope = scope != null ? NavisworksExportScope.View : NavisworksExportScope.Model,
                    ExportElementIds = true,
                    ConvertElementProperties = true
                };

                if (scope != null) options.ViewId = scope.Id;
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "无法准备 NWC 导出选项：" + ex.Message +
                    "。NWC 导出需要本机装有 Navisworks 的 Revit 导出器插件。");
            }

            try
            {
                document.Export(folder, baseName, options);
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "Revit 导出 NWC 失败：" + ex.Message +
                    "。最常见的原因是本机没装 Navisworks 的 Revit 导出器——" +
                    "它随 Navisworks 一起安装，Revit 本身不带。");
            }
        }

        // ==================== 共用零件 ====================

        private static string ExtensionOf(string format)
        {
            switch (format)
            {
                case "dwg": return ".dwg";
                case "dxf": return ".dxf";
                case "pdf": return ".pdf";
                case "ifc": return ".ifc";
                case "nwc": return ".nwc";

                default:
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "无法识别的 format \"" + format + "\"。可用值：dwg、dxf、pdf、ifc、nwc。");
            }
        }

        private List<View> RequireViews(
            Document document, IList<string> rawIds, bool? confirm,
            ToolExecutionContext<UIApplication> context, string format)
        {
            if (rawIds == null || rawIds.Count == 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "format 为 " + format + " 时必须给 viewIds。用 revit_list_views 挑视图或图纸。");

            GuardScale(rawIds.Count, confirm, context, "导出",
                "否则请分批导出，每批不超过 " + context.MaxElementsPerWrite + " 个视图。");

            var views = new List<View>();
            var seen = new HashSet<long>();

            foreach (var rawId in rawIds)
            {
                var element = RequireElement(document, rawId);
                var view = element as View;

                if (view == null)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "ID " + rawId + " 不是视图或图纸。用 revit_list_views 取 ID。");

                if (view.IsTemplate)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "「" + AnnotationSupport.SafeName(view) + "」是视图样板，导不出东西。");

                if (!seen.Add(view.Id.GetValue())) continue;

                views.Add(view);
            }

            return views;
        }

        /// <summary>ifc/nwc 的可选导出范围视图。给了就用第一个，没给就导整个模型。</summary>
        private static List<View> OptionalScopeView(Document document, IList<string> rawIds)
        {
            if (rawIds == null || rawIds.Count == 0) return new List<View>();

            var element = RequireElement(document, rawIds[0]);
            var view = element as View;

            if (view == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "ID " + rawIds[0] + " 不是视图。ifc 与 nwc 用它限定导出范围，省略则导整个模型。");

            return new List<View> { view };
        }

        /// <summary>
        /// 把暂存目录里的产物挪到导出目录。
        ///
        /// 只产出一个文件时用调用方指定的名字；产出多个时（按视图分文件的 DWG、
        /// 不合并的 PDF）保留 Revit 自己起的名字——那些名字里带着视图名，
        /// 强行改成"名字 (1)(2)(3)"反而让人分不清哪个是哪个。
        /// </summary>
        private static void CollectResults(
            string staging, string target, ExportDocumentsOutput output,
            ToolExecutionContext<UIApplication> context)
        {
            var produced = Directory.GetFiles(staging, "*", SearchOption.AllDirectories);

            if (produced.Length == 0)
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "Revit 没有导出任何文件，也没有报错。" +
                    "选中的视图可能都无法打印（用 revit_list_views 看 canBePrinted）。");

            var directory = Path.GetDirectoryName(target);

            foreach (var file in produced.OrderBy(f => f, StringComparer.Ordinal))
            {
                var destination = produced.Length == 1
                    ? target
                    : Path.Combine(directory, Path.GetFileName(file));

                try
                {
                    if (File.Exists(destination))
                    {
                        context.Warnings.Add("已覆盖同名文件 " + Path.GetFileName(destination) + "。");
                        File.Delete(destination);
                    }

                    File.Move(file, destination);
                }
                catch (Exception ex)
                {
                    throw new ToolFailureException(McpDomainError.TransactionFailed,
                        "文件已导出，但移动到目标路径失败：" + ex.Message);
                }

                var info = new FileInfo(destination);
                output.Files.Add(new ExportedFile
                {
                    Path = destination,
                    FileSizeBytes = info.Exists ? info.Length : 0
                });
            }

            output.FileCount = output.Files.Count;

            // 一个 0 字节的产物和一个正常产物在"导出成功"这句话里看不出区别
            var empty = output.Files.Count(f => f.FileSizeBytes == 0);
            if (empty > 0)
                context.Warnings.Add(
                    "有 " + empty + " 个导出文件是 0 字节——Revit 报告成功，但没写进任何内容。" +
                    "多半是那些视图里没有可见构件。");
        }
    }

    // ==================== 明细表导出 ====================

    public sealed class ExportSchedulesInput
    {
        [McpParam("要导出的明细表视图 ID，来自 revit_list_views（viewType 为 Schedule）。ElementId 与 uniqueId 两种写法都接受", Required = true)]
        public List<string> ScheduleIds { get; set; }

        [McpParam("文件名（不带路径）。导多张时会在文件名后拼上明细表名。" +
                  "扩展名可用 .csv（默认）、.txt、.tsv")]
        public string FileName { get; set; }

        [McpParam("字段分隔符。省略时按扩展名定：csv 用逗号、tsv 与 txt 用制表符")]
        public string Delimiter { get; set; }

        [McpParam("是否导出标题行，默认 true")]
        public bool? IncludeHeaders { get; set; }

        [McpParam("是否给文本字段加引号，默认 true。" +
                  "字段里可能含分隔符时必须加，否则下游会把一列切成两列")]
        public bool? QuoteText { get; set; }
    }

    public sealed class ExportedSchedule
    {
        [McpParam("明细表 ID")]
        public string Id { get; set; }

        [McpParam("明细表名")]
        public string Name { get; set; }

        [McpParam("导出文件的完整路径")]
        public string Path { get; set; }

        [McpParam("文件大小，字节")]
        public long FileSizeBytes { get; set; }
    }

    public sealed class ExportSchedulesOutput
    {
        [McpParam("导出的明细表数")]
        public int Exported { get; set; }

        [McpParam("逐张明细表的结果")]
        public List<ExportedSchedule> Schedules { get; set; } = new List<ExportedSchedule>();
    }

    /// <summary>
    /// 把明细表导成分隔符文本文件。
    ///
    /// 与 <c>revit_read_schedule</c> 的分工：那个把表读成结构化数据直接回给模型，
    /// 适合"看一眼、做个判断"；这个落盘成文件，适合交给别的软件或存档。
    /// </summary>
    [McpTool("revit_export_schedules",
        Toolsets = new[] { Toolsets.Documentation },
        Title = "导出明细表",
        Description = "把一批明细表导成 CSV/TSV/TXT 文件，落在服务的导出目录下。" +
                      "只是想读数据做判断的话用 revit_read_schedule——" +
                      "那个直接把表格回给你，不用再去读文件。",
        Destructive = false,
        TimeoutSeconds = 300)]
    public sealed class ExportSchedulesTool : RevitTool<ExportSchedulesInput, ExportSchedulesOutput>
    {
        public override ExportSchedulesOutput Execute(
            ExportSchedulesInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);

            if (input.ScheduleIds == null || input.ScheduleIds.Count == 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "scheduleIds 不能为空。用 revit_list_views 查 viewType 为 Schedule 的视图。");

            var root = ExportRoot();
            var fileName = string.IsNullOrWhiteSpace(input.FileName) ? "schedule.csv" : input.FileName;
            var target = ExportPaths.Resolve(root, fileName, ".csv", ExportPaths.TableExtensions);

            var extension = Path.GetExtension(target);
            var options = BuildOptions(input, extension);

            var directory = Path.GetDirectoryName(target);
            var baseName = Path.GetFileNameWithoutExtension(target);
            var single = input.ScheduleIds.Count == 1;

            var output = new ExportSchedulesOutput();
            var total = input.ScheduleIds.Count;

            for (var index = 0; index < total; index++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var rawId = input.ScheduleIds[index];
                var element = RequireElement(document, rawId);
                var schedule = element as ViewSchedule;

                if (schedule == null)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "ID " + rawId + " 不是明细表，而是「" +
                        (element.Category?.Name ?? element.GetType().Name) +
                        "」。用 revit_list_views 查 viewType 为 Schedule 的视图。");

                var name = AnnotationSupport.SafeName(schedule);

                // 导多张时给每个文件拼上明细表名，否则第二张会盖掉第一张
                var outputName = single
                    ? Path.GetFileName(target)
                    : baseName + " - " + Sanitize(name) + extension;

                try
                {
                    schedule.Export(directory, outputName, options);
                }
                catch (Exception ex)
                {
                    throw new ToolFailureException(McpDomainError.TransactionFailed,
                        "导出明细表「" + (name ?? rawId) + "」失败：" + ex.Message);
                }

                var path = Path.Combine(directory, outputName);
                var info = new FileInfo(path);

                if (!info.Exists)
                    throw new ToolFailureException(McpDomainError.TransactionFailed,
                        "Revit 报告明细表「" + (name ?? rawId) + "」已导出，但目标文件不存在：" + path);

                output.Schedules.Add(new ExportedSchedule
                {
                    Id = rawId,
                    Name = name,
                    Path = path,
                    FileSizeBytes = info.Length
                });

                ProgressTicker.Tick(context.Progress, index + 1, total, "已导出");
            }

            output.Exported = output.Schedules.Count;
            return output;
        }

        private static ViewScheduleExportOptions BuildOptions(ExportSchedulesInput input, string extension)
        {
            var includeHeaders = input.IncludeHeaders ?? true;

            return new ViewScheduleExportOptions
            {
                FieldDelimiter = ResolveDelimiter(input.Delimiter, extension),

                // 明细表的标题行（「门明细表」这种）不是数据，放进 CSV 只会让第一行对不上列数
                Title = false,

                ColumnHeaders = includeHeaders ? ExportColumnHeaders.OneRow : ExportColumnHeaders.None,

                // Revit 的分组标题与空行同样不是数据行，导出去会让下游的行数对不上
                HeadersFootersBlanks = false,

                // 字段里出现分隔符是常态（构件名、说明文字），不加引号下游会把一列切成两列
                TextQualifier = (input.QuoteText ?? true)
                    ? ExportTextQualifier.DoubleQuote
                    : ExportTextQualifier.None
            };
        }

        private static string ResolveDelimiter(string value, string extension)
        {
            if (!string.IsNullOrEmpty(value)) return value;

            return string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase) ? "," : "\t";
        }

        /// <summary>明细表名会被拼进文件名，里面的非法字符要先换掉。</summary>
        private static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "未命名";

            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();

            return new string(chars).Trim();
        }
    }
}
