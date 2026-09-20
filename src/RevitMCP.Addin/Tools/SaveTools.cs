using System;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    public sealed class SaveDocumentInput
    {
        [McpParam("确认保存。默认 false 时只回报文档状态、不落盘——" +
                  "保存会覆盖用户磁盘上的文件，值得让调用方多打一个字")]
        public bool? Confirm { get; set; }
    }

    public sealed class SaveDocumentOutput
    {
        [McpParam("文档标题")]
        public string Title { get; set; }

        [McpParam("落盘路径")]
        public string Path { get; set; }

        [McpParam("本次是否真的写了磁盘。confirm 未给时为 false")]
        public bool Saved { get; set; }

        [McpParam("保存前文档是否有未保存的改动。为 false 说明这次保存是空转")]
        public bool? HadUnsavedChanges { get; set; }

        [McpParam("文件大小，字节。未保存时为 null")]
        public long? FileSizeBytes { get; set; }
    }

    /// <summary>
    /// 保存当前文档。
    ///
    /// **必须是 <see cref="McpToolAttribute.WithoutTransaction"/> 的。**
    /// Revit 不允许在事务打开的状态下保存文档——`Document.Save()` 会直接抛
    /// InvalidOperationException。这正是 `WithoutTransaction` 存在的理由：
    /// 它受写保护管辖（写关闭时拒绝），但框架不替它开事务。
    ///
    /// 与切换活动视图（另一个 WithoutTransaction 工具）不同的是，保存**动的是磁盘**，
    /// 回滚不了。所以这里额外加一道 confirm：不带 confirm 调用只回报状态，
    /// 让模型先看清"要存的是哪个文件、有没有改动"再决定。
    /// </summary>
    [McpTool("revit_save_document",
        Title = "保存当前文档",
        Description = "把当前活动文档存回它自己的文件。" +
                      "**不带 confirm 调用只回报状态、不落盘**——先看清要覆盖的是哪个文件，再带 confirm: true 存。" +
                      "建完模型记得存：MCP 建的东西在用户保存之前只活在内存里。" +
                      "从没保存过的新文档存不了，要用 revit_save_document_as 指定文件名。",
        Destructive = false,
        WithoutTransaction = true,
        TimeoutSeconds = 300)]
    public sealed class SaveDocumentTool : RevitTool<SaveDocumentInput, SaveDocumentOutput>
    {
        public override SaveDocumentOutput Execute(
            SaveDocumentInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);

            RequireSaveable(document);

            var path = SafeString(() => document.PathName);
            if (string.IsNullOrEmpty(path))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "这个文档还从来没有保存过，没有可写回的文件。" +
                    "用 revit_save_document_as 给它一个文件名。");

            var output = new SaveDocumentOutput
            {
                Title = SafeString(() => document.Title),
                Path = path,
                HadUnsavedChanges = SafeBool(() => document.IsModified)
            };

            if (input.Confirm != true)
            {
                context.Warnings.Add(
                    "未落盘：这是一次只读的状态回报。确认要覆盖 " + path +
                    " 后，带上 confirm: true 重新调用。");
                output.Saved = false;
                output.FileSizeBytes = FileSize(path);
                return output;
            }

            WarnIfWorkshared(document, context);

            if (output.HadUnsavedChanges == false)
                context.Warnings.Add("文档没有未保存的改动，这次保存是空转。");

            try
            {
                document.Save();
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "Revit 拒绝保存：" + ex.Message +
                    "。常见原因是文件被别的程序占用、或磁盘上的文件是只读的。");
            }

            output.Saved = true;
            output.FileSizeBytes = FileSize(path);
            return output;
        }

        internal static void RequireSaveable(Document document)
        {
            if (SafeBool(() => document.IsReadOnly))
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "当前文档是只读打开的，存不回去。");

            if (SafeBool(() => document.IsLinked))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "当前文档是被链接进来的模型，不能由本服务保存。");
        }

        /// <summary>
        /// 工作共享文档存的是本地文件，不是中心文件。
        ///
        /// 这一条必须说出来：在协同项目里，"我存过了"和"同事能看到"是两件事，
        /// 而同步到中心文件（SynchronizeWithCentral）会影响别人，
        /// 不该由一个模型在用户没明确要求时替他做。
        /// </summary>
        internal static void WarnIfWorkshared(Document document, ToolExecutionContext<UIApplication> context)
        {
            if (!SafeBool(() => document.IsWorkshared)) return;

            context.Warnings.Add(
                "这是工作共享文档，本次只保存了本地文件，**没有同步到中心文件**。" +
                "要同步请用户在 Revit 里操作——同步会影响协同的其他人，不该由这里代劳。");
        }

        internal static long? FileSize(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info.Length : (long?)null;
            }
            catch { return null; }
        }

        internal static string SafeString(Func<string> read)
        {
            try { return read(); }
            catch { return null; }
        }

        internal static bool SafeBool(Func<bool> read)
        {
            try { return read(); }
            catch { return false; }
        }
    }

    public sealed class SaveDocumentAsInput
    {
        [McpParam("目标文件的**完整路径**，如 \"D:\\项目\\办公楼-备份.rvt\"。" +
                  "必须含盘符，扩展名必须是 .rvt。" +
                  "目录必须已经存在——工具不会替你建目录，路径打错一个字就会建出" +
                  "一个没人找得到的文件夹", Required = true)]
        public string Path { get; set; }

        [McpParam("同名文件已存在时是否覆盖，默认 false（存在即失败）。" +
                  "覆盖一个模型文件是不可逆的，所以必须显式要求")]
        public bool? Overwrite { get; set; }
    }

    public sealed class SaveDocumentAsOutput
    {
        [McpParam("文档标题（另存后会变成新文件名）")]
        public string Title { get; set; }

        [McpParam("新文件的完整路径")]
        public string Path { get; set; }

        [McpParam("另存前文档关联的路径；从未保存过则为 null")]
        public string PreviousPath { get; set; }

        [McpParam("文件大小，字节")]
        public long? FileSizeBytes { get; set; }
    }

    /// <summary>
    /// 另存为。
    ///
    /// **路径策略与 <see cref="ExportPaths"/> 刻意不同：这里收完整路径。**
    ///
    /// 导出的落点由服务决定，是因为那些文件名很可能来自模型刚读到的某段数据。
    /// 但"模型另存到哪儿"只可能是用户的明确决定——把它沙箱进
    /// %LOCALAPPDATA%\RevitMCP\exports，等于让这个功能没人会用：
    /// 谁另存一个项目都是要存在原项目旁边的。
    ///
    /// 代价是它能往磁盘任意位置写一个模型文件。所以覆盖默认不允许，
    /// 必须显式 overwrite——覆盖掉用户某个同名项目文件是不可逆的。
    /// </summary>
    [McpTool("revit_save_document_as",
        Title = "另存当前文档",
        Description = "把当前活动文档另存为一个新文件，返回完整路径。" +
                      "**收的是完整路径**（与各类导出不同，那些只收文件名）——" +
                      "另存一个模型总是要存在原项目旁边的。目录必须已经存在，" +
                      "覆盖已有文件需要显式 overwrite: true。" +
                      "**另存之后 Revit 里打开的就是新文件了**——后续的 revit_save_document 存的也是它，" +
                      "原文件停在另存前的状态。只是想留个副本时要想清楚这一点。",
        Destructive = true,
        WithoutTransaction = true,
        TimeoutSeconds = 600)]
    public sealed class SaveDocumentAsTool : RevitTool<SaveDocumentAsInput, SaveDocumentAsOutput>
    {
        /// <summary>
        /// 校验另存的目标路径。
        ///
        /// 三件事必须当场查清楚，否则 Revit 给出的报错会含糊到没法照着改：
        /// 是不是完整路径、扩展名对不对、目录在不在。
        /// **刻意不替调用方建目录**——路径打错一个字就会建出一个没人找得到的文件夹，
        /// 而那个文件夹里躺着一份几十兆的模型。
        /// </summary>
        private static string ResolveTarget(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    @"path 不能为空，需要一个完整的目标路径（含盘符），如 D:\项目\办公楼-备份.rvt。");

            var path = raw.Trim();

            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "path 含有路径里不允许的字符：收到 \"" + raw + "\"。");

            if (!Path.IsPathRooted(path))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "path 必须是完整路径（含盘符），收到 \"" + raw + "\"。" +
                    "相对路径的基准目录取决于 Revit 进程的当前目录，那个值不由任何人控制。");

            var extension = Path.GetExtension(path);
            if (!string.Equals(extension, ".rvt", StringComparison.OrdinalIgnoreCase))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "另存的扩展名必须是 .rvt，收到 \"" + extension + "\"。" +
                    "要导出别的格式用 revit_export_documents。");

            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "目标目录不存在：" + directory + "。工具不会替你建目录——" +
                    "路径打错一个字就会建出一个没人找得到的文件夹。");

            return Path.GetFullPath(path);
        }

        public override SaveDocumentAsOutput Execute(
            SaveDocumentAsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);

            SaveDocumentTool.RequireSaveable(document);

            var target = ResolveTarget(input.Path);

            var previous = SaveDocumentTool.SafeString(() => document.PathName);

            if (File.Exists(target))
            {
                if (input.Overwrite != true)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "文件已存在：" + target + "。换个路径，或带上 overwrite: true 覆盖它。");

                if (string.Equals(previous, target, StringComparison.OrdinalIgnoreCase))
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "目标就是当前文档自己（" + target + "）。要存回原处请用 revit_save_document。");

                context.Warnings.Add("已覆盖同名文件 " + Path.GetFileName(target) + "。");
            }

            SaveDocumentTool.WarnIfWorkshared(document, context);

            try
            {
                document.SaveAs(target, new SaveAsOptions { OverwriteExistingFile = input.Overwrite == true });
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "Revit 拒绝另存：" + ex.Message);
            }

            return new SaveDocumentAsOutput
            {
                Title = SaveDocumentTool.SafeString(() => document.Title),
                Path = target,
                PreviousPath = string.IsNullOrEmpty(previous) ? null : previous,
                FileSizeBytes = SaveDocumentTool.FileSize(target)
            };
        }
    }
}
