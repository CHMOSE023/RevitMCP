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
    public sealed class ExportImageInput
    {
        [McpParam("要导出的视图或图纸 ID，来自 revit_list_views。省略则导出当前活动视图")]
        public string ViewId { get; set; }

        [McpParam("文件名（不是路径），如 \"一层平面.png\"。省略扩展名则按 imageType 补。" +
                  "文件一律落在服务的导出目录下，返回值里给出完整路径", Required = true)]
        public string FileName { get; set; }

        [McpParam("图片宽度（像素），默认 1920，上限 8000")]
        public int? PixelWidth { get; set; }

        [McpParam("图片格式：png（默认）或 jpg")]
        public string ImageType { get; set; }
    }

    public sealed class ExportImageOutput
    {
        [McpParam("图片的完整路径。直接读这个路径就能拿到文件")]
        public string Path { get; set; }

        [McpParam("导出的视图名")]
        public string ViewName { get; set; }

        [McpParam("视图类型")]
        public string ViewType { get; set; }

        [McpParam("图片宽度（像素）")]
        public int PixelWidth { get; set; }

        [McpParam("文件大小，字节")]
        public long FileSizeBytes { get; set; }

        [McpParam("该视图中可见的构件数。**为 0 说明导出的是一张空图**——" +
                  "常见原因是选错了视图（比如平面视图所在的标高上没有构件）。" +
                  "对图纸而言，这个数包含视口与标题栏")]
        public int? VisibleElementCount { get; set; }
    }

    /// <summary>
    /// 导出视图图片。
    ///
    /// **定性为只读工具**，尽管它会往磁盘写文件。判据是"会不会改模型"：
    /// 导出不动模型，而只读的质检工作流恰恰最需要它——
    /// 把问题截图落盘、进报告、进 PR。要求用户先开修改模式才能截个图是说不通的。
    ///
    /// 写文件的风险另有一道闸：路径不由调用方决定，只接受文件名，
    /// 一律落在配置的导出目录下（见 <see cref="RevitMCP.Tooling.ExportPaths"/>）。
    /// </summary>
    [McpTool("revit_export_image",
        Title = "导出视图图片",
        Description = "把视图或图纸导出成图片文件，返回完整路径。" +
                      "截图能落盘、能进报告、能进 PR——比在对话里看一眼就没了有用得多。" +
                      "不指定 viewId 就导当前活动视图。" +
                      "**只能给文件名，不能给路径**：文件一律落在服务的导出目录下。",
        ReadOnly = true,
        TimeoutSeconds = 120)]
    public sealed class ExportImageTool : RevitTool<ExportImageInput, ExportImageOutput>
    {
        private const int DefaultPixelWidth = 1920;
        private const int MaxPixelWidth = 8000;

        public override ExportImageOutput Execute(
            ExportImageInput input, ToolExecutionContext<UIApplication> context)
        {
            var uiDocument = RequireUiDocument(context);
            var document = uiDocument.Document;

            var view = ResolveView(uiDocument, document, input.ViewId);
            var fileType = ParseImageType(input.ImageType);
            var extension = fileType == ImageFileType.PNG ? ".png" : ".jpg";

            var pixelWidth = Math.Min(Math.Max(input.PixelWidth ?? DefaultPixelWidth, 64), MaxPixelWidth);
            if (input.PixelWidth.HasValue && input.PixelWidth.Value > MaxPixelWidth)
                context.Warnings.Add(
                    "pixelWidth 上限为 " + MaxPixelWidth + "，已按上限导出。" +
                    "再大的图对阅读没有帮助，却可能让 Revit 吃光内存。");

            var root = ExportRoot();
            var target = ExportPaths.Resolve(root, input.FileName, extension);

            if (File.Exists(target))
                context.Warnings.Add("已覆盖同名文件 " + System.IO.Path.GetFileName(target) + "。");

            // Revit 导出时会自己给文件名加后缀（视图名、视图类型都可能被拼进去），
            // 所以先让它落进一个空目录，再把唯一的产物挪到我们承诺的路径上。
            // 不这么做，返回给模型的路径就是错的——它拿去读会扑空
            var staging = ExportPaths.CreateStagingDirectory(root);

            try
            {
                Export(document, view, staging, fileType, pixelWidth);

                var produced = Directory.GetFiles(staging);

                if (produced.Length == 0)
                    throw new ToolFailureException(McpDomainError.TransactionFailed,
                        "Revit 没有导出任何文件，也没有报错。视图「" + SafeName(view) +
                        "」可能无法打印（可用 revit_list_views 看 canBePrinted）。");

                if (produced.Length > 1)
                    context.Warnings.Add(
                        "Revit 一次导出了 " + produced.Length + " 个文件，只保留了第一个。" +
                        "这通常意味着该视图被拆成了多页。");

                Move(produced[0], target);
            }
            finally
            {
                ExportPaths.SafeDelete(staging);
            }

            var info = new FileInfo(target);
            var visible = CountVisible(document, view);

            // 导出成功不等于导出了东西。一张空图和一张有内容的图在返回值里
            // 曾经长得一模一样——"成功"两个字把"你选错视图了"整个盖住了
            if (visible == 0)
                context.Warnings.Add(
                    "视图「" + SafeName(view) + "」里没有任何可见构件，导出的是一张空图。" +
                    "如果这不是预期，多半是选错了视图——平面视图只显示它所在标高附近的构件，" +
                    "可以用 revit_list_views 看 level 字段对不对。");

            return new ExportImageOutput
            {
                Path = target,
                ViewName = SafeName(view),
                ViewType = SafeViewType(view),
                PixelWidth = pixelWidth,
                FileSizeBytes = info.Exists ? info.Length : 0,
                VisibleElementCount = visible
            };
        }

        private static void Export(
            Document document, View view, string staging, ImageFileType fileType, int pixelWidth)
        {
            var options = new ImageExportOptions
            {
                // 基名无所谓，Revit 会在它基础上拼出真正的文件名
                FilePath = System.IO.Path.Combine(staging, "view"),
                ExportRange = ExportRange.SetOfViews,
                HLRandWFViewsFileType = fileType,
                ShadowViewsFileType = fileType,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                FitDirection = FitDirectionType.Horizontal,
                PixelSize = pixelWidth
            };

            options.SetViewsAndSheets(new List<ElementId> { view.Id });

            try
            {
                document.ExportImage(options);
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "Revit 导出图片失败：" + ex.Message);
            }
        }

        private static void Move(string from, string to)
        {
            try
            {
                if (File.Exists(to)) File.Delete(to);
                File.Move(from, to);
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "图片已导出，但移动到目标路径失败：" + ex.Message);
            }
        }

        private static View ResolveView(UIDocument uiDocument, Document document, string rawId)
        {
            if (!string.IsNullOrWhiteSpace(rawId))
            {
                var element = RequireElement(document, rawId);
                var view = element as View;

                if (view == null)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "ID " + rawId + " 不是视图。用 revit_list_views 取视图或图纸 ID。");

                if (view.IsTemplate)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "「" + view.Name + "」是视图样板，没有可导出的内容。");

                return view;
            }

            View active;
            try { active = uiDocument.ActiveView; }
            catch { active = null; }

            if (active == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "没有指定 viewId，当前也没有活动视图。");

            return active;
        }

        private static ImageFileType ParseImageType(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return ImageFileType.PNG;

            switch (value.Trim().ToLowerInvariant())
            {
                case "png": return ImageFileType.PNG;
                case "jpg":
                case "jpeg": return ImageFileType.JPEGLossless;
                default:
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "不支持的 imageType \"" + value + "\"。可用：png、jpg。");
            }
        }

        /// <summary>数视图里可见的构件。数不出来时返回 null——宁可不说，也别乱说。</summary>
        private static int? CountVisible(Document document, View view)
        {
            try
            {
                return new FilteredElementCollector(document, view.Id)
                    .WhereElementIsNotElementType()
                    .GetElementCount();
            }
            catch
            {
                return null;
            }
        }

        private static string SafeName(View view)
        {
            try { return view.Name; }
            catch { return null; }
        }

        private static string SafeViewType(View view)
        {
            try { return view.ViewType.ToString(); }
            catch { return null; }
        }
    }
}
