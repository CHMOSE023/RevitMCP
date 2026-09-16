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
        [McpParam("文件名（不是路径），如 \"办公楼.rvt\"。省略扩展名则补 .rvt。" +
                  "文件一律落在服务的导出目录下，返回值里给出完整路径", Required = true)]
        public string FileName { get; set; }

        [McpParam("同名文件已存在时是否覆盖，默认 false（存在即失败）")]
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
    /// 路径约束照 <see cref="ExportPaths"/> 的先例办：**只收文件名，不收路径**，
    /// 一律落在配置的导出目录下。理由和导出图片时一样——
    /// 文件名很可能来自模型刚读过的某个构件名，而写坏用户的文件不可逆。
    ///
    /// 比导出图片更需要这道闸：这里写的是几十兆的模型，
    /// 覆盖掉用户某个同名项目文件的后果要严重得多。
    /// </summary>
    [McpTool("revit_save_document_as",
        Title = "另存当前文档",
        Description = "把当前活动文档另存为一个新文件，返回完整路径。" +
                      "**只能给文件名，不能给路径**：文件一律落在服务的导出目录下。" +
                      "**另存之后 Revit 里打开的就是新文件了**——后续的 revit_save_document 存的也是它，" +
                      "原文件停在另存前的状态。只是想留个副本时要想清楚这一点。",
        Destructive = true,
        WithoutTransaction = true,
        TimeoutSeconds = 600)]
    public sealed class SaveDocumentAsTool : RevitTool<SaveDocumentAsInput, SaveDocumentAsOutput>
    {
        public override SaveDocumentAsOutput Execute(
            SaveDocumentAsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);

            SaveDocumentTool.RequireSaveable(document);

            var target = ExportPaths.Resolve(
                ExportRoot(), input.FileName, ".rvt", ExportPaths.ProjectExtensions);

            var previous = SaveDocumentTool.SafeString(() => document.PathName);

            if (File.Exists(target))
            {
                if (input.Overwrite != true)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "文件已存在：" + target + "。换个文件名，或带上 overwrite: true 覆盖它。");

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
