using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Compat
{
    /// <summary>
    /// PDF 导出的 API（<c>PDFExportOptions</c>）是 Revit 2022 才有的。差异只允许出现在这里。
    ///
    /// 2021 及更早的版本在 API 层面根本没有 PDF 导出——能做的只有走 <c>PrintManager</c>
    /// 打到某个 PDF 打印机驱动上，而那依赖用户机器上装了什么、纸张与边距由驱动说了算、
    /// 失败时也拿不到有意义的报错。刻意不做那条路：
    /// 一个"有时候能出、出来的东西还不一定对"的导出，比明确说"这个版本做不到"更糟。
    /// </summary>
    public static class PdfExportCompat
    {
        /// <summary>本版本能不能导 PDF。</summary>
        public static bool IsSupported
        {
#if REVIT2022_OR_GREATER
            get { return true; }
#else
            get { return false; }
#endif
        }

        /// <summary>
        /// 把一批视图/图纸导成 PDF。
        /// <paramref name="combine"/> 为 true 时合成一个文件，否则每个视图一个。
        /// </summary>
        public static void Export(
            Document document, string folder, string baseName,
            IList<ElementId> views, bool combine)
        {
#if REVIT2022_OR_GREATER
            var options = new PDFExportOptions
            {
                FileName = baseName,
                Combine = combine,

                // 出错就停下，而不是悄悄跳过几页继续。
                // 一个少了两张图的 PDF 和一个完整的 PDF 在返回值里看不出区别
                StopOnError = true,

                // 裁剪边界与参照平面是给建模看的，不该出现在交付的图纸上
                HideCropBoundaries = true,
                HideReferencePlane = true,
                HideScopeBoxes = true,
                HideUnreferencedViewTags = true
            };

            document.Export(folder, views, options);
#else
            throw new RevitMCP.Tooling.ToolFailureException(
                RevitMCP.Tooling.McpDomainError.InvalidParameter,
                "Revit " + RevitVersionInfo.Year + " 的 API 不提供 PDF 导出（这是 2022 才有的）。" +
                "本版本可以导 DWG、DXF、IFC、NWC，或用 revit_export_image 导图片。" +
                "确实需要 PDF 的话，只能请用户在 Revit 里手工打印。");
#endif
        }
    }
}
