using System;
using System.IO;
using System.Linq;

namespace RevitMCP.Tooling
{
    /// <summary>
    /// 落盘文件的位置。
    ///
    /// **这是整个服务里会在模型之外留下痕迹的那一类操作**，所以路径不能由调用方说了算。
    /// 模型拿到的是一个文件名，不是一个路径：不接受目录分隔符、不接受 <c>..</c>、
    /// 不接受盘符或 UNC 前缀，最终一律落在根目录下。
    ///
    /// 这不是防"模型会使坏"，而是防**模型被喂了坏数据**——
    /// 文件名很可能来自它刚读过的某个构件名、某段用户输入。
    /// 写坏用户的文件是不可逆的，而限制目录几乎不损失可用性：
    /// 位置固定，用户和 Claude Code 都知道去哪儿找。
    ///
    /// **[M10] 扩展名白名单改为按调用点传入。** 起初这里只服务 <c>revit_export_image</c>，
    /// 图片扩展名写死在类里就够了。M10 加入 <c>revit_save_document_as</c> 后落盘的是 .rvt，
    /// 而"能写图片"和"能写模型"该由各自的工具声明，不该让这个类替它们记住。
    /// 白名单本身一条都没放宽：仍然是白名单而非黑名单。
    /// </summary>
    public static class ExportPaths
    {
        /// <summary>图片扩展名。<c>revit_export_image</c> 用的就是这一组。</summary>
        public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };

        /// <summary>Revit 项目文件扩展名。<c>revit_save_document_as</c> 用的就是这一组。</summary>
        public static readonly string[] ProjectExtensions = { ".rvt" };

        /// <summary>
        /// 把调用方给的文件名解析成根目录下的绝对路径，顺便建好目录。
        /// 任何越界企图都当场失败，并说清楚规则——模型据此能一次改对。
        ///
        /// 不带 <c>allowedExtensions</c> 的重载沿用图片白名单，保持既有调用点不变。
        /// </summary>
        public static string Resolve(string root, string fileName, string defaultExtension)
        {
            return Resolve(root, fileName, defaultExtension, ImageExtensions);
        }

        /// <param name="allowedExtensions">允许的扩展名（小写，带点）。**能写 .exe 到磁盘的工具不该存在**，所以这里永远是白名单。</param>
        public static string Resolve(
            string root, string fileName, string defaultExtension, string[] allowedExtensions)
        {
            if (allowedExtensions == null || allowedExtensions.Length == 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "内部错误：未声明允许的扩展名。");

            if (string.IsNullOrWhiteSpace(root))
                throw new ToolFailureException(McpDomainError.InvalidParameter, "导出根目录未配置。");

            if (string.IsNullOrWhiteSpace(fileName))
                throw new ToolFailureException(McpDomainError.InvalidParameter, "fileName 不能为空。");

            var name = fileName.Trim();

            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "fileName 只能是文件名，不能带路径：收到 \"" + fileName + "\"。" +
                    "所有导出都落在服务的导出目录下，返回值里会给出完整路径。");

            // 非法字符必须在任何 Path.* 调用**之前**查。
            // .NET Framework 上 Path.IsPathRooted("a<b>.png") 不是返回 false，而是直接抛
            // ArgumentException("路径中具有非法字符")——那会绕过下面所有精心写的错误信息，
            // 让模型收到一个没头没尾的裸异常。
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "fileName 含有文件名里不允许的字符：收到 \"" + fileName + "\"。");

            if (name.Contains("..") || Path.IsPathRooted(name))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "fileName 不能是绝对路径、也不能包含 \"..\"：收到 \"" + fileName + "\"。");

            var extension = Path.GetExtension(name);
            if (string.IsNullOrEmpty(extension))
            {
                extension = defaultExtension;
                name += extension;
            }

            if (!allowedExtensions.Contains(extension.ToLowerInvariant()))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "不支持的文件扩展名 \"" + extension + "\"。可用：" + string.Join("、", allowedExtensions) + "。");

            string full;
            try
            {
                Directory.CreateDirectory(root);
                full = Path.GetFullPath(Path.Combine(root, name));
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "无法准备导出目录 " + root + "：" + ex.Message);
            }

            // 前面几道检查之后这里本不该失败，但路径规范化有太多平台细节
            // （保留设备名、尾随点、长路径……），最后按结果再确认一次边界
            var rootFull = Path.GetFullPath(root);
            if (!rootFull.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                rootFull += Path.DirectorySeparatorChar;

            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "fileName 解析后落到了导出目录之外，已拒绝。");

            return full;
        }

        /// <summary>导出期间用的空临时目录。Revit 导出时会自己改文件名，先让它落在一个干净的地方。</summary>
        public static string CreateStagingDirectory(string root)
        {
            var staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(staging);
            return staging;
        }

        public static void SafeDelete(string directory)
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch { /* 临时目录清不掉不值得让导出失败，下次启动时也不影响什么 */ }
        }
    }
}
