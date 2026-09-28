using System.Drawing;
using System.Drawing.Drawing2D;

namespace TingYu.Manager
{
    /// <summary>
    /// 视觉规范：简约亮色。
    ///
    /// 三条来自需求本身的约束，直接落成代码：
    /// 1. **亮色**——底色接近白，不用深色主题。
    /// 2. **纯色边框**——所有分隔一律 1px 实色描边，不用阴影、渐变、圆角堆叠。
    /// 3. **无说明性文字**——界面上只保留必要的信息（名称、状态、数值），
    ///    不写解释性段落、不留副标题、不放提示语。这条会在各处反复体现，
    ///    评审时如果看到某个控件在「解释自己」，那就是违反约束。
    /// </summary>
    internal static class UiTheme
    {
        // ---------------------------------------------------------------- 颜色

        /// <summary>窗口底色，最亮的一层。</summary>
        internal static readonly Color Canvas = Color.FromArgb(250, 250, 251);

        /// <summary>卡片/面板底色，比画布稍暗一点，靠描边而不是靠明度差来分块。</summary>
        internal static readonly Color Surface = Color.FromArgb(255, 255, 255);

        /// <summary>次级底色，用于输入框、列表条纹。</summary>
        internal static readonly Color Subtle = Color.FromArgb(244, 245, 247);

        /// <summary>边框。整套界面只有这一种边框色。</summary>
        internal static readonly Color Border = Color.FromArgb(214, 218, 224);

        /// <summary>强调边框，用于选中态。</summary>
        internal static readonly Color BorderStrong = Color.FromArgb(64, 120, 200);

        /// <summary>主文字。</summary>
        internal static readonly Color TextColor = Color.FromArgb(32, 36, 44);

        /// <summary>次级文字，只用于数值与状态。</summary>
        internal static readonly Color TextMuted = Color.FromArgb(122, 130, 142);

        /// <summary>不可用。</summary>
        internal static readonly Color TextDisabled = Color.FromArgb(178, 184, 194);

        /// <summary>强调色，用于选中条目与主按钮。</summary>
        internal static readonly Color Accent = Color.FromArgb(64, 120, 200);

        /// <summary>选中条目的底色。</summary>
        internal static readonly Color AccentSoft = Color.FromArgb(232, 240, 252);

        /// <summary>接管中。</summary>
        internal static readonly Color Active = Color.FromArgb(38, 150, 96);

        /// <summary>警告/失败。</summary>
        internal static readonly Color Danger = Color.FromArgb(198, 64, 64);

        // ---------------------------------------------------------------- 字体

        /// <summary>
        /// 界面字体。优先用系统里的思源黑体/微软雅黑，避免自带字体文件
        /// （自带字体要一并处理授权，而系统字体已经覆盖简中）。
        /// </summary>
        private static readonly string Family = PickFamily();

        private static string PickFamily()
        {
            var wanted = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans SC", "Segoe UI" };
            foreach (var name in wanted)
            {
                foreach (var family in FontFamily.Families)
                {
                    if (string.Equals(family.Name, name, System.StringComparison.OrdinalIgnoreCase))
                        return family.Name;
                }
            }
            return FontFamily.GenericSansSerif.Name;
        }

        internal static readonly Font Body = new Font(Family, 9f, FontStyle.Regular, GraphicsUnit.Point);
        internal static readonly Font BodyBold = new Font(Family, 9f, FontStyle.Bold, GraphicsUnit.Point);
        internal static readonly Font Small = new Font(Family, 8f, FontStyle.Regular, GraphicsUnit.Point);
        internal static readonly Font Title = new Font(Family, 11f, FontStyle.Bold, GraphicsUnit.Point);

        // ---------------------------------------------------------------- 尺寸

        internal const int BorderWidth = 1;
        internal const int Padding = 12;
        internal const int Gap = 8;

        /// <summary>内容区与窗口边缘之间的留白。</summary>
        internal const int Margin = 10;
        internal const int RowHeight = 34;
        internal const int InstrumentCardWidth = 104;
        internal const int InstrumentCardHeight = 96;

        // ---------------------------------------------------------------- 绘制

        /// <summary>画一个纯色描边的矩形。整套界面所有分块都走这一个方法，保证边框一致。</summary>
        internal static void Frame(Graphics graphics, Rectangle bounds, Color fill, Color border)
        {
            using (var brush = new SolidBrush(fill))
                graphics.FillRectangle(brush, bounds);

            // 描边画在矩形内侧，这样相邻面板的边框不会互相压掉半个像素。
            using (var pen = new Pen(border, BorderWidth))
            {
                var inset = new Rectangle(bounds.X, bounds.Y, bounds.Width - BorderWidth, bounds.Height - BorderWidth);
                graphics.DrawRectangle(pen, inset);
            }
        }

        internal static void FrameSelected(Graphics graphics, Rectangle bounds, bool selected, Color fill)
        {
            Frame(graphics, bounds, fill, selected ? BorderStrong : Border);
        }

        /// <summary>单像素分隔线。</summary>
        internal static void Separator(Graphics graphics, int x, int y, int length, bool horizontal)
        {
            using (var pen = new Pen(Border, BorderWidth))
            {
                if (horizontal) graphics.DrawLine(pen, x, y, x + length, y);
                else graphics.DrawLine(pen, x, y, x, y + length);
            }
        }

        /// <summary>
        /// 文字绘制。用 `Graphics.DrawString`（GDI+ 路径），不用 `TextRenderer`（GDI 路径）。
        ///
        /// 这是被界面自检逼出来的选择，不是风格偏好。`TextRenderer` 在窗口上直接绘制时没问题，
        /// 但一旦把窗口抄进离屏位图（自检用的 `DrawToBitmap`），就会出现难以预测的丢字：
        /// 同一批标签里有的画得出来、有的画不出来，换个窗口尺寸又是另一批丢失。
        /// 排查这个现象花了很多时间，最后定位到绘制 API 而不是布局计算。
        ///
        /// `DrawString` 与离屏渲染完全兼容；中文字形略不及 GDI 锐利，靠 `ClearTypeGridFit` 补偿。
        /// </summary>
        internal static void DrawText(System.Drawing.Graphics graphics, string text, Font font,
            Color color, Rectangle bounds, ContentAlignment alignment)
        {
            var previousHint = graphics.TextRenderingHint;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            try
            {
                using (var brush = new SolidBrush(color))
                using (var format = StringFormatFor(alignment))
                {
                    graphics.DrawString(text, font, brush, bounds, format);
                }
            }
            finally
            {
                graphics.TextRenderingHint = previousHint;
            }
        }

        /// <summary>
        /// 与 `ContentAlignment` 对应的排版设置。
        ///
        /// 这里必须**显式传入**一个 `StringFormat`。对照实验（同一个 Form 里并排画多组
        /// `DrawString`）显示：不带 `StringFormat` 的那个重载在离屏渲染下一律画不出字
        /// （0 像素），而带格式对象的几种写法都正常。所以问题在重载选择，不在排版标志——
        /// 我最初误判为 `GenericTypographic` 的 `NoFontFallback` 标志所致，
        /// 后来的实验否掉了那个结论，这里按实测结果写。
        ///
        /// 用 `GenericDefault` 是因为它的行距比 `GenericTypographic` 更适合界面标题。
        /// </summary>
        private static StringFormat StringFormatFor(ContentAlignment alignment)
        {
            var format = new StringFormat(StringFormat.GenericDefault)
            {
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap,
                LineAlignment = StringAlignment.Center
            };
            switch (alignment)
            {
                case ContentAlignment.MiddleCenter:
                case ContentAlignment.TopCenter:
                case ContentAlignment.BottomCenter:
                    format.Alignment = StringAlignment.Center;
                    break;
                case ContentAlignment.MiddleRight:
                case ContentAlignment.TopRight:
                case ContentAlignment.BottomRight:
                    format.Alignment = StringAlignment.Far;
                    break;
                default:
                    format.Alignment = StringAlignment.Near;
                    break;
            }
            return format;
        }


        /// <summary>把 16x16 的物品贴图放大到目标尺寸，用最近邻保持像素风。</summary>
        internal static void DrawSprite(Graphics graphics, Image sprite, Rectangle bounds)
        {
            if (sprite == null) return;
            var previous = graphics.InterpolationMode;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            try
            {
                graphics.DrawImage(sprite, bounds);
            }
            finally
            {
                graphics.InterpolationMode = previous;
                graphics.PixelOffsetMode = PixelOffsetMode.Default;
            }
        }
    }
}
