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
    // ==================== 打开 / 新建 ====================

    public sealed class OpenDocumentInput
    {
        [McpParam("要做什么：open（打开已有文件）、new（从样板新建）、editFamily（打开某个族去编辑）",
                  Required = true,
                  AllowedValues = new[] { "open", "new", "editFamily" })]
        public string Action { get; set; }

        [McpParam("文件的完整路径。action 为 open 时是要打开的 .rvt/.rfa；" +
                  "为 new 时是项目样板 .rte（省略则用 Revit 的默认公制样板）")]
        public string Path { get; set; }

        [McpParam("要编辑的族 ID，来自 revit_list_families。action 为 editFamily 时必填")]
        public string FamilyId { get; set; }

        [McpParam("是否把新打开的文档切成活动文档（用户屏幕会跟着变），默认 true。" +
                  "只是想读一读别的模型的话设 false，不打断用户正在做的事")]
        public bool? Activate { get; set; }

        [McpParam("action 为 open 且目标是工作共享模型时，是否以「分离」方式打开，默认 false。" +
                  "分离后的文档与中心文件断开，改了也同步不回去——只想看一眼时用它最安全")]
        public bool? Detach { get; set; }
    }

    public sealed class OpenDocumentOutput
    {
        [McpParam("新打开文档的 ID。其他工具的 documentId 参数用它")]
        public string DocumentId { get; set; }

        [McpParam("文档标题")]
        public string Title { get; set; }

        [McpParam("文档路径。未保存的新文档为空")]
        public string Path { get; set; }

        [McpParam("是不是族文档。族文档里大部分建模工具用不了")]
        public bool IsFamily { get; set; }

        [McpParam("是不是工作共享模型")]
        public bool IsWorkshared { get; set; }

        [McpParam("是否已切成活动文档")]
        public bool Activated { get; set; }
    }

    /// <summary>
    /// 打开 / 新建 / 编辑族。
    ///
    /// 这是整个服务里第二类会越出"当前模型"边界的操作（第一类是导出）。
    /// 路径由调用方给，没法像导出那样沙箱化——打开文件本来就意味着指定文件。
    /// 所以它受写保护管辖：用户不切到「修改模型」就调不动。
    ///
    /// 不开事务：Revit 不允许在事务打开的状态下开关文档。
    /// </summary>
    [McpTool("revit_open_document",
        Title = "打开或新建文档",
        Description = "在当前 Revit 实例里打开一个已有文件、从样板新建一个项目，或打开某个族来编辑。" +
                      "打开后用返回的 documentId 去查它——多数只读工具都接受 documentId 参数。" +
                      "**写操作只作用于活动文档**，所以要改新打开的模型，activate 必须为 true。" +
                      "工作共享模型建议带 detach: true 打开，避免意外占用中心文件的编辑权。",
        WithoutTransaction = true,
        TimeoutSeconds = 600)]
    public sealed class OpenDocumentTool : RevitTool<OpenDocumentInput, OpenDocumentOutput>
    {
        public override OpenDocumentOutput Execute(
            OpenDocumentInput input, ToolExecutionContext<UIApplication> context)
        {
            var application = context.Host;
            if (application == null)
                throw new ToolFailureException(McpDomainError.NoActiveDocument, "拿不到 Revit 应用实例。");

            var action = (input.Action ?? string.Empty).Trim().ToLowerInvariant();
            var activate = input.Activate ?? true;

            Document document;
            switch (action)
            {
                case "open":
                    return OpenExisting(application, input, context, activate);

                case "new":
                    document = CreateNew(application, input);
                    break;

                case "editfamily":
                    document = EditFamily(application, input);
                    break;

                default:
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "无法识别的 action \"" + input.Action +
                        "\"。可用值：open（打开已有文件）、new（从样板新建）、editFamily（编辑族）。");
            }

            return Describe(document, activated: false, context: context,
                note: activate
                    ? "新建与编辑族得到的文档尚未保存，没有路径，Revit 无法把它切成活动文档。" +
                      "先在 Revit 里保存它，或直接用返回的 documentId 做只读查询。"
                    : null);
        }

        private static OpenDocumentOutput OpenExisting(
            UIApplication application, OpenDocumentInput input,
            ToolExecutionContext<UIApplication> context, bool activate)
        {
            var path = RequirePath(input.Path, ".rvt / .rfa");

            // 已经开着就别再开一遍：Revit 会直接抛"文档已打开"，
            // 而调用方真正想要的多半只是拿到它的 documentId
            var already = DocumentRef.Opened(application)
                .FirstOrDefault(d => string.Equals(SafePath(d), path, StringComparison.OrdinalIgnoreCase));

            if (already != null)
            {
                context.Warnings.Add("该文件已经在这个 Revit 实例里打开了，直接返回它。");

                var activated = activate && TryActivate(application, path, context);
                return Describe(already, activated, context, null);
            }

            Document document;
            try
            {
                if (activate)
                {
                    // OpenAndActivateDocument 不接受 OpenOptions，detach 只能走另一条路
                    if (input.Detach == true)
                    {
                        document = OpenDetached(application, path);
                        context.Warnings.Add(
                            "分离方式打开的文档不能同时切成活动文档，activate 被忽略。" +
                            "它已经打开，用返回的 documentId 做只读查询。");

                        return Describe(document, activated: false, context: context, note: null);
                    }

                    var uiDocument = application.OpenAndActivateDocument(path);
                    document = uiDocument?.Document;

                    return Describe(document, activated: document != null, context, null);
                }

                document = input.Detach == true
                    ? OpenDetached(application, path)
                    : application.Application.OpenDocumentFile(path);
            }
            catch (ToolFailureException) { throw; }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "打开 " + path + " 失败：" + ex.Message);
            }

            return Describe(document, activated: false, context, null);
        }

        private static Document OpenDetached(UIApplication application, string path)
        {
            var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);

            var options = new OpenOptions
            {
                // 丢弃工作集设置：分离本来就是为了"看一眼而不牵扯中心文件"，
                // 保留工作集会让文档仍然试图去联系中心文件
                DetachFromCentralOption = DetachFromCentralOption.DetachAndDiscardWorksets
            };

            return application.Application.OpenDocumentFile(modelPath, options);
        }

        private static Document CreateNew(UIApplication application, OpenDocumentInput input)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(input.Path))
                    return application.Application.NewProjectDocument(UnitSystem.Metric);

                var template = RequirePath(input.Path, ".rte");
                return application.Application.NewProjectDocument(template);
            }
            catch (ToolFailureException) { throw; }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "新建项目失败：" + ex.Message);
            }
        }

        private static Document EditFamily(UIApplication application, OpenDocumentInput input)
        {
            if (string.IsNullOrWhiteSpace(input.FamilyId))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "action 为 editFamily 时必须给 familyId。用 revit_list_families 取 ID。");

            var host = application.ActiveUIDocument?.Document;
            if (host == null)
                throw new ToolFailureException(McpDomainError.NoActiveDocument,
                    "没有活动文档，无从找起这个族。");

            var element = RequireElement(host, input.FamilyId);
            var family = element as Family;

            if (family == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "familyId " + input.FamilyId + " 不是族，而是「" +
                    (element.Category?.Name ?? element.GetType().Name) +
                    "」。用 revit_list_families 取族 ID。");

            bool inPlace;
            try { inPlace = family.IsInPlace; }
            catch { inPlace = false; }

            if (inPlace)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "「" + AnnotationSupport.SafeName(family) +
                    "」是内建族，它没有独立的族文件，只能在项目里就地编辑，打不开成单独的文档。");

            try
            {
                return host.EditFamily(family);
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "打开族「" + AnnotationSupport.SafeName(family) + "」失败：" + ex.Message +
                    "。系统族（基本墙、楼板这类）没有族文件，打不开。");
            }
        }

        private static bool TryActivate(
            UIApplication application, string path, ToolExecutionContext<UIApplication> context)
        {
            try
            {
                application.OpenAndActivateDocument(path);
                return true;
            }
            catch (Exception ex)
            {
                context.Warnings.Add("没能把它切成活动文档：" + ex.Message);
                return false;
            }
        }

        internal static string RequirePath(string raw, string expected)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "path 不能为空，需要一个完整的文件路径（" + expected + "）。");

            var path = raw.Trim();

            if (!File.Exists(path))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "文件不存在：" + path + "。请给出完整路径（含盘符），不是相对路径。");

            return path;
        }

        private static OpenDocumentOutput Describe(
            Document document, bool activated, ToolExecutionContext<UIApplication> context, string note)
        {
            if (document == null)
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "Revit 没有返回文档，也没有报错。");

            if (note != null) context.Warnings.Add(note);

            var output = new OpenDocumentOutput
            {
                DocumentId = DocumentRef.KeyOf(document),
                Title = SafeTitle(document),
                Path = SafePath(document),
                Activated = activated
            };

            try { output.IsFamily = document.IsFamilyDocument; } catch { }
            try { output.IsWorkshared = document.IsWorkshared; } catch { }

            return output;
        }

        internal static string SafePath(Document document)
        {
            try { return document.PathName; }
            catch { return null; }
        }

        internal static string SafeTitle(Document document)
        {
            try { return document.Title; }
            catch { return null; }
        }
    }

    // ==================== 保存 ====================

    public sealed class SaveDocumentInput
    {
        [McpParam("要保存的文档 ID，来自 revit_list_documents。省略则保存当前活动文档")]
        public string DocumentId { get; set; }

        [McpParam("另存为的完整路径。省略则原地保存。" +
                  "**这个路径不受服务的导出目录限制**——它会往用户的磁盘上写一个完整的模型文件，" +
                  "所以只在用户明确要求另存时才给它")]
        public string SaveAsPath { get; set; }

        [McpParam("另存为时，目标文件已存在是否覆盖，默认 false。" +
                  "覆盖一个模型文件是不可逆的，所以默认不允许")]
        public bool? Overwrite { get; set; }

        [McpParam("是否压缩保存（清理累积的增量记录，文件会变小但保存变慢），默认 false")]
        public bool? Compact { get; set; }
    }

    public sealed class SaveDocumentOutput
    {
        [McpParam("被保存的文档 ID。另存为之后它会变成新路径")]
        public string DocumentId { get; set; }

        [McpParam("文档标题")]
        public string Title { get; set; }

        [McpParam("保存后的完整路径")]
        public string Path { get; set; }

        [McpParam("保存前有没有未保存的改动")]
        public bool HadUnsavedChanges { get; set; }

        [McpParam("用的是原地保存（save）还是另存为（saveAs）")]
        public string Mode { get; set; }

        [McpParam("文件大小，字节")]
        public long FileSizeBytes { get; set; }
    }

    /// <summary>
    /// 保存文档。
    ///
    /// 与导出刻意不同：导出的落点由服务决定，保存的落点由用户决定。
    /// 理由是两者的性质不同——导出的文件名很可能来自模型刚读到的某段数据，
    /// 而"另存到哪儿"只能是用户的明确决定，沙箱化它等于让这个功能没用。
    /// 代价是这个工具能往磁盘任意位置写一个模型文件，所以覆盖默认不允许。
    /// </summary>
    [McpTool("revit_save_document",
        Title = "保存文档",
        Description = "保存文档，或另存到指定路径。省略 saveAsPath 就是原地保存。" +
                      "另存为的路径不受服务导出目录的限制，会真的往用户磁盘上写模型文件——" +
                      "只在用户明确要求另存时才用它。覆盖已有文件需要显式 overwrite: true。" +
                      "工作共享模型请用 revit_sync_to_central，原地保存只存本地副本。",
        WithoutTransaction = true,
        TimeoutSeconds = 600)]
    public sealed class SaveDocumentTool : RevitTool<SaveDocumentInput, SaveDocumentOutput>
    {
        public override SaveDocumentOutput Execute(
            SaveDocumentInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = ResolveDocument(context, input.DocumentId);

            if (document.IsReadOnly)
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "文档「" + OpenDocumentTool.SafeTitle(document) + "」是只读的，存不了。");

            var output = new SaveDocumentOutput
            {
                Title = OpenDocumentTool.SafeTitle(document)
            };

            try { output.HadUnsavedChanges = document.IsModified; }
            catch { /* 读不到就按有改动处理，照常保存 */ output.HadUnsavedChanges = true; }

            if (string.IsNullOrWhiteSpace(input.SaveAsPath))
            {
                SaveInPlace(document, input, context);
                output.Mode = "save";
            }
            else
            {
                SaveAs(document, input, context);
                output.Mode = "saveAs";
            }

            output.DocumentId = DocumentRef.KeyOf(document);
            output.Path = OpenDocumentTool.SafePath(document);

            if (!string.IsNullOrEmpty(output.Path))
            {
                var info = new FileInfo(output.Path);
                if (info.Exists) output.FileSizeBytes = info.Length;
            }

            return output;
        }

        private static void SaveInPlace(
            Document document, SaveDocumentInput input, ToolExecutionContext<UIApplication> context)
        {
            if (string.IsNullOrEmpty(OpenDocumentTool.SafePath(document)))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "文档「" + OpenDocumentTool.SafeTitle(document) +
                    "」从未保存过，没有路径可存。请给 saveAsPath 指定要存到哪儿。");

            bool workshared;
            try { workshared = document.IsWorkshared; }
            catch { workshared = false; }

            if (workshared)
                context.Warnings.Add(
                    "这是工作共享模型，原地保存只把改动写进本地副本，不会同步到中心文件。" +
                    "要同步用 revit_sync_to_central。");

            try
            {
                document.Save(new SaveOptions { Compact = input.Compact ?? false });
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "保存失败：" + ex.Message);
            }
        }

        private static void SaveAs(
            Document document, SaveDocumentInput input, ToolExecutionContext<UIApplication> context)
        {
            var path = input.SaveAsPath.Trim();

            if (!Path.IsPathRooted(path))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "saveAsPath 必须是完整路径（含盘符），收到 \"" + path + "\"。");

            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "目标目录不存在：" + directory + "。工具不会替你建目录——" +
                    "路径打错一个字就建出一个没人找得到的文件夹。");

            var exists = File.Exists(path);
            if (exists && input.Overwrite != true)
                throw new ToolFailureException(McpDomainError.ConfirmationRequired,
                    "目标文件已存在：" + path +
                    "。覆盖一个模型文件是不可逆的。确认要覆盖就带上 overwrite: true 重新调用。");

            try
            {
                document.SaveAs(path, new SaveAsOptions
                {
                    OverwriteExistingFile = input.Overwrite ?? false,
                    Compact = input.Compact ?? false
                });
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "另存为 " + path + " 失败：" + ex.Message);
            }

            if (exists) context.Warnings.Add("已覆盖原有文件 " + path + "。");
        }
    }

    // ==================== 关闭 ====================

    public sealed class CloseDocumentInput
    {
        [McpParam("要关闭的文档 ID，来自 revit_list_documents", Required = true)]
        public string DocumentId { get; set; }

        [McpParam("关闭前是否保存，默认 false（丢弃未保存的改动）。" +
                  "true 表示先保存再关——从未保存过的文档没有路径，那种情况会失败")]
        public bool? Save { get; set; }
    }

    public sealed class CloseDocumentOutput
    {
        [McpParam("被关闭文档的 ID")]
        public string DocumentId { get; set; }

        [McpParam("被关闭文档的标题")]
        public string Title { get; set; }

        [McpParam("关闭时有没有未保存的改动")]
        public bool HadUnsavedChanges { get; set; }

        [McpParam("是否在关闭前保存了")]
        public bool Saved { get; set; }

        [McpParam("关闭后剩下的活动文档 ID")]
        public string ActiveDocumentId { get; set; }
    }

    /// <summary>
    /// 关闭文档。
    ///
    /// Revit 不允许直接关闭活动文档，必须先把别的文档切成活动的。
    /// 而"激活一个已打开的文档"在 API 里没有直接入口，只能借 <c>OpenAndActivateDocument</c>
    /// 传它自己的路径绕过去——所以另一个文档必须是保存过、有路径的。
    /// 这些都是 Revit 的限制而不是设计选择，工具能做的是把它们变成说得清的错误。
    /// </summary>
    [McpTool("revit_close_document",
        Title = "关闭文档",
        Description = "关闭一个已打开的文档。默认**丢弃**未保存的改动——" +
                      "要保留就先调 revit_save_document，或带 save: true。" +
                      "关闭活动文档时，工具会先把另一个已打开的文档切成活动的；" +
                      "只开着这一个文档时关不掉，Revit 不允许。",
        WithoutTransaction = true,
        TimeoutSeconds = 300)]
    public sealed class CloseDocumentTool : RevitTool<CloseDocumentInput, CloseDocumentOutput>
    {
        public override CloseDocumentOutput Execute(
            CloseDocumentInput input, ToolExecutionContext<UIApplication> context)
        {
            var application = context.Host;
            var document = ResolveDocument(context, input.DocumentId);

            if (string.IsNullOrWhiteSpace(input.DocumentId))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "documentId 不能为空——关文档这件事不该有默认目标。");

            var output = new CloseDocumentOutput
            {
                DocumentId = DocumentRef.KeyOf(document),
                Title = OpenDocumentTool.SafeTitle(document)
            };

            try { output.HadUnsavedChanges = document.IsModified; }
            catch { }

            var save = input.Save ?? false;

            if (save && string.IsNullOrEmpty(OpenDocumentTool.SafePath(document)))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "文档「" + output.Title + "」从未保存过，没有路径可存。" +
                    "先用 revit_save_document 指定 saveAsPath 存一份，或带 save: false 直接丢弃。");

            if (DocumentRef.SameAs(document, DocumentRef.ActiveOf(application)))
                SwitchAway(application, document, context);

            try
            {
                document.Close(save);
                output.Saved = save;
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "关闭文档「" + output.Title + "」失败：" + ex.Message +
                    "。Revit 不允许关闭正在被编辑的文档，也不允许关掉最后一个文档。");
            }

            output.ActiveDocumentId = DocumentRef.KeyOf(DocumentRef.ActiveOf(application));
            return output;
        }

        /// <summary>
        /// 把活动文档换成另一个已打开的文档。
        /// API 里没有"激活已打开文档"这个动作，只能拿它自己的路径再 Open 一次——
        /// Revit 认出它已经开着，于是只做激活。
        /// </summary>
        private static void SwitchAway(
            UIApplication application, Document closing, ToolExecutionContext<UIApplication> context)
        {
            var candidate = DocumentRef.Opened(application)
                .Where(d => !DocumentRef.SameAs(d, closing))
                .FirstOrDefault(d => !string.IsNullOrEmpty(OpenDocumentTool.SafePath(d)));

            if (candidate == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "「" + OpenDocumentTool.SafeTitle(closing) +
                    "」是当前活动文档，Revit 不允许直接关闭它，" +
                    "而现在没有第二个**保存过的**文档可以切过去。" +
                    "请让用户在 Revit 里手工切换或关闭。");

            try
            {
                application.OpenAndActivateDocument(OpenDocumentTool.SafePath(candidate));

                context.Warnings.Add(
                    "关闭前把活动文档切到了「" + OpenDocumentTool.SafeTitle(candidate) +
                    "」——用户屏幕上看到的模型变了。");
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "关闭前需要先切换活动文档，但切换失败：" + ex.Message);
            }
        }
    }
}
