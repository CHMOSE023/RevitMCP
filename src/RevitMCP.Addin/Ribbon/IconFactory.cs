using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitMCP.Addin.Ribbon
{
    /// <summary>
    /// 运行时用 WPF 绘制 Ribbon 图标。
    ///
    /// 不用 .png 资源文件是有意的：图标要随服务状态变色（灰/绿/红），
    /// 用代码生成就不必为每个状态各准备一套位图，也免去二进制资源的版本管理。
    /// </summary>
    internal static class IconFactory
    {
        public static readonly Color Idle = Color.FromRgb(0x8A, 0x8A, 0x8E);
        public static readonly Color Active = Color.FromRgb(0x2E, 0xA0, 0x43);
        public static readonly Color Warning = Color.FromRgb(0xD9, 0x7A, 0x0B);
        public static readonly Color Danger = Color.FromRgb(0xC0, 0x39, 0x2B);
        public static readonly Color Neutral = Color.FromRgb(0x3B, 0x6E, 0xA5);

        /// <summary>Ribbon 大按钮 32px，小按钮 16px。</summary>
        public static ImageSource Create(string glyph, Color color, int size = 32)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var rect = new Rect(0, 0, size, size);
                var radius = size * 0.22;

                dc.DrawRoundedRectangle(new SolidColorBrush(color), null, rect, radius, radius);

                var emSize = size * 0.58;
                var text = new FormattedText(
                    glyph,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                    emSize,
                    Brushes.White,
                    pixelsPerDip: 1.0);

                dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
            }

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();   // 冻结后可跨线程安全使用
            return bitmap;
        }
    }
}
