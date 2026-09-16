using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitMCP.Addin.Ribbon
{
    /// <summary>Ribbon 图标的图形。每个状态一个图形，不靠颜色区分。</summary>
    internal enum Glyph
    {
        /// <summary>服务运行中：在线指示灯（圆环套实心点）。</summary>
        Online,

        /// <summary>服务已停止：停止方块。</summary>
        Stopped,

        /// <summary>服务出错：感叹号。和「已停止」不能只差一个颜色。</summary>
        Alert,

        /// <summary>写入关闭：闭合的锁。</summary>
        LockClosed,

        /// <summary>写入开启：打开的锁。</summary>
        LockOpen,

        /// <summary>复制到剪贴板：两张叠放的纸。</summary>
        Copy,

        /// <summary>日志文件：带折角的文档。</summary>
        Document
    }

    /// <summary>
    /// 运行时用 WPF 绘制 Ribbon 图标。
    ///
    /// 不用 .png 资源文件是有意的：图标要随服务状态变，
    /// 用代码生成就不必为每个状态各准备一套位图，也免去二进制资源的版本管理。
    ///
    /// **状态不同就换图形，不只换颜色。** 颜色在小尺寸、在色觉障碍者眼里、
    /// 在深色主题下都可能失效，而"锁是开是合"看轮廓就知道。
    /// 颜色只用来强化已经由图形表达清楚的信息。
    /// </summary>
    internal static class IconFactory
    {
        public static readonly Color Idle = Color.FromRgb(0x8A, 0x8A, 0x8E);
        public static readonly Color Active = Color.FromRgb(0x2E, 0xA0, 0x43);
        public static readonly Color Warning = Color.FromRgb(0xD9, 0x7A, 0x0B);
        public static readonly Color Danger = Color.FromRgb(0xC0, 0x39, 0x2B);
        public static readonly Color Neutral = Color.FromRgb(0x3B, 0x6E, 0xA5);

        /// <summary>Ribbon 大按钮 32px，小按钮 16px。</summary>
        public static ImageSource Create(Glyph glyph, Color color, int size = 32)
        {
            var background = new SolidColorBrush(color);
            var visual = new DrawingVisual();

            using (var dc = visual.RenderOpen())
            {
                var radius = size * 0.22;
                dc.DrawRoundedRectangle(background, null, new Rect(0, 0, size, size), radius, radius);

                switch (glyph)
                {
                    case Glyph.Online: DrawOnline(dc, size); break;
                    case Glyph.Stopped: DrawStopped(dc, size); break;
                    case Glyph.Alert: DrawAlert(dc, size); break;
                    case Glyph.LockClosed: DrawLock(dc, size, open: false); break;
                    case Glyph.LockOpen: DrawLock(dc, size, open: true); break;
                    case Glyph.Copy: DrawCopy(dc, size, background); break;
                    case Glyph.Document: DrawDocument(dc, size, background); break;
                }
            }

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();   // 冻结后可跨线程安全使用
            return bitmap;
        }

        // ---------- 各图形 ----------

        /// <summary>
        /// 在线指示灯：圆环套一个实心点。
        ///
        /// 选圆形是为了和「已停止」的方块形成形状对比——两个状态不靠颜色也分得开。
        /// 不用播放三角或电源符号：那两个在界面里通常是**按钮动作**（点我启动），
        /// 而这个按钮显示的是状态，动作感强的图形会和状态式文案打架。
        /// </summary>
        private static void DrawOnline(DrawingContext dc, int size)
        {
            var center = P(0.5, 0.5, size);

            dc.DrawEllipse(null, Stroke(size, 0.085), center, size * 0.28, size * 0.28);
            dc.DrawEllipse(Brushes.White, null, center, size * 0.13, size * 0.13);
        }

        /// <summary>停止方块。</summary>
        private static void DrawStopped(DrawingContext dc, int size)
        {
            var rect = new Rect(P(0.32, 0.32, size), P(0.68, 0.68, size));
            dc.DrawRoundedRectangle(Brushes.White, null, rect, size * 0.06, size * 0.06);
        }

        /// <summary>感叹号。停止和出错是两回事，不该只靠颜色区分。</summary>
        private static void DrawAlert(DrawingContext dc, int size)
        {
            var bar = new Rect(P(0.43, 0.22, size), P(0.57, 0.60, size));
            dc.DrawRoundedRectangle(Brushes.White, null, bar, size * 0.07, size * 0.07);

            dc.DrawEllipse(Brushes.White, null, P(0.5, 0.73, size), size * 0.08, size * 0.08);
        }

        /// <summary>
        /// 挂锁。开锁时锁梁整体右移、右脚悬空并超出锁体——
        /// 只把右脚缩短一点在 16px 下根本看不出来，轮廓必须差得够远。
        /// </summary>
        private static void DrawLock(DrawingContext dc, int size, bool open)
        {
            var body = new Rect(P(0.26, 0.48, size), P(0.74, 0.81, size));
            dc.DrawRoundedRectangle(Brushes.White, null, body, size * 0.07, size * 0.07);

            var pen = Stroke(size, 0.085);

            // 闭锁：锁梁居中，两脚都落在锁体上
            // 开锁：锁梁右移，左脚落在锁体上，右脚停在半空且探出锁体右缘
            var leftX = open ? 0.50 : 0.365;
            var rightX = open ? 0.80 : 0.635;
            var footY = open ? 0.42 : 0.49;
            var topY = 0.37;
            var radius = (rightX - leftX) / 2 * size;

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(P(leftX, 0.49, size), isFilled: false, isClosed: false);
                ctx.LineTo(P(leftX, topY, size), isStroked: true, isSmoothJoin: true);
                ctx.ArcTo(P(rightX, topY, size), new Size(radius, radius), 0,
                    isLargeArc: false, sweepDirection: SweepDirection.Clockwise,
                    isStroked: true, isSmoothJoin: true);
                ctx.LineTo(P(rightX, footY, size), isStroked: true, isSmoothJoin: false);
            }
            geometry.Freeze();

            dc.DrawGeometry(null, pen, geometry);
        }

        /// <summary>
        /// 两张叠放的纸。前面那张用底色填充，等于在两张纸之间挖出一道缝——
        /// 纯描边在 16px 下会糊成一团。
        /// </summary>
        private static void DrawCopy(DrawingContext dc, int size, Brush background)
        {
            var pen = Stroke(size, 0.08);
            var corner = size * 0.06;

            var back = new Rect(P(0.24, 0.20, size), P(0.60, 0.64, size));
            dc.DrawRoundedRectangle(null, pen, back, corner, corner);

            var front = new Rect(P(0.40, 0.36, size), P(0.78, 0.80, size));
            dc.DrawRoundedRectangle(background, pen, front, corner, corner);
        }

        /// <summary>
        /// 带折角的文档。纸面填白、文字线用底色挖空，比描边在小尺寸下清楚。
        /// </summary>
        private static void DrawDocument(DrawingContext dc, int size, Brush background)
        {
            var fold = 0.56;    // 折角起点的 x
            var foldY = 0.34;   // 折角结束的 y

            var page = new StreamGeometry();
            using (var ctx = page.Open())
            {
                ctx.BeginFigure(P(0.28, 0.18, size), isFilled: true, isClosed: true);
                ctx.LineTo(P(fold, 0.18, size), true, false);
                ctx.LineTo(P(0.74, foldY, size), true, false);
                ctx.LineTo(P(0.74, 0.82, size), true, false);
                ctx.LineTo(P(0.28, 0.82, size), true, false);
            }
            page.Freeze();
            dc.DrawGeometry(Brushes.White, null, page);

            // 折角：用底色盖出一个三角，纸看起来就是翻折过来的
            var corner = new StreamGeometry();
            using (var ctx = corner.Open())
            {
                ctx.BeginFigure(P(fold, 0.18, size), isFilled: true, isClosed: true);
                ctx.LineTo(P(0.74, foldY, size), true, false);
                ctx.LineTo(P(fold, foldY, size), true, false);
            }
            corner.Freeze();
            dc.DrawGeometry(background, null, corner);

            // 16px 下三条线会糊成一块，减到两条
            var lines = size >= 24 ? new[] { 0.50, 0.61, 0.72 } : new[] { 0.52, 0.68 };
            var pen = new Pen(background, size * 0.075);

            foreach (var y in lines)
                dc.DrawLine(pen, P(0.38, y, size), P(0.64, y, size));
        }

        // ---------- 小工具 ----------

        private static Pen Stroke(int size, double weight)
        {
            var pen = new Pen(Brushes.White, size * weight)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();
            return pen;
        }

        /// <summary>归一化坐标 → 像素。图形按 0..1 的方格设计，同一套几何两种尺寸通用。</summary>
        private static Point P(double x, double y, int size)
        {
            return new Point(x * size, y * size);
        }
    }
}
