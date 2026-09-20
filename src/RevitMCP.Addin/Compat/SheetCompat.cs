using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Compat
{
    /// <summary>图纸复制要带上多少东西。对外的名字，与 Revit 的枚举解耦。</summary>
    public enum SheetCopyContents
    {
        /// <summary>只复制图纸本身与图签，纸上什么都不放。</summary>
        Empty,

        /// <summary>连同图纸上的视图一起复制——会为每个视图**生成一份副本**再摆上去。</summary>
        Views,

        /// <summary>连同图纸上的详图项目与注释一起复制，但不复制视图。</summary>
        Detailing,

        /// <summary>视图与详图都复制。</summary>
        ViewsAndDetailing
    }

    /// <summary>
    /// 图纸复制的 API 是 2022 才有的（<c>ViewSheet.Duplicate</c>）。差异只允许出现在这里。
    ///
    /// 有一件事必须说清楚：Revit 的"连视图复制"是**为每个视图生成一份新视图**，
    /// 而不是把原视图挪过去。这是它唯一合理的做法——一个视图只能放在一张图纸上——
    /// 但和"复制"这个词的直觉不同，所以工具的回执里要把新视图的 ID 交出来。
    /// </summary>
    public static class SheetCompat
    {
        /// <summary>本版本能不能用 Revit 自己的图纸复制。</summary>
        public static bool CanDuplicateNatively
        {
#if REVIT2022_OR_GREATER
            get { return true; }
#else
            get { return false; }
#endif
        }

        /// <summary>
        /// 用 Revit 自己的 API 复制图纸，返回新图纸 ID。
        /// 本版本不支持、或 Revit 判定这张图纸不能这么复制时返回 <c>null</c>，
        /// 由调用方退回手工复制。
        /// </summary>
        public static ElementId TryDuplicate(ViewSheet sheet, SheetCopyContents contents)
        {
#if REVIT2022_OR_GREATER
            var option = ToOption(contents);

            try
            {
                if (!sheet.CanBeDuplicated(option)) return null;
                return sheet.Duplicate(option);
            }
            catch
            {
                // 拒绝的原因五花八门（图纸被锁、工作集不可编辑、视图不可复制……），
                // 一律退回手工复制，让调用方至少拿到一张图纸
                return null;
            }
#else
            return null;
#endif
        }

#if REVIT2022_OR_GREATER
        private static SheetDuplicateOption ToOption(SheetCopyContents contents)
        {
            switch (contents)
            {
                case SheetCopyContents.Views:
                    return SheetDuplicateOption.DuplicateSheetWithViewsOnly;

                case SheetCopyContents.Detailing:
                    return SheetDuplicateOption.DuplicateSheetWithDetailing;

                case SheetCopyContents.ViewsAndDetailing:
                    return SheetDuplicateOption.DuplicateSheetWithViewsAndDetailing;

                default:
                    return SheetDuplicateOption.DuplicateEmptySheet;
            }
        }
#endif
    }
}
