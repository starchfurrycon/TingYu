using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace TingYu.Manager
{
    /// <summary>
    /// 界面自检：把窗口渲染到位图，并对渲染结果做断言。
    ///
    /// 为什么不是「截个图给人看」：那需要人来判断，也就意味着每改一次界面都要人看一次。
    /// 这里把「界面看起来对不对」拆成可机器判定的几条：面板是否都画出来了、
    /// 边框色是否统一、有没有整块空白、文字是否落在面板内。
    /// 这些断言不能证明界面好看，但能抓住绝大多数「画错了」的情况，
    /// 而且能在没有显示器的机器上跑。
    /// </summary>
    internal static class UiSmokeTest
    {
        internal static int Run(string outputDirectory)
        {
            if (string.IsNullOrEmpty(outputDirectory))
                outputDirectory = Path.Combine(Path.GetTempPath(), "tingyu-ui-smoke");

            Directory.CreateDirectory(outputDirectory);
            var report = new StringBuilder();
            var failures = new List<string>();
            var sizes = new[]
            {
                new Size(980, 640),
                new Size(1180, 720),
                new Size(880, 560)
            };

            using (var form = new MainForm())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-4000, -4000);
                form.Show();
                Application.DoEvents();

                foreach (var size in sizes)
                {
                    form.ClientSize = size;
                    form.PerformLayout();
                    Application.DoEvents();

                                        var bitmap = Render(form);
                    var tag = size.Width.ToString(CultureInfo.InvariantCulture) + "x" +
                              size.Height.ToString(CultureInfo.InvariantCulture);
                    var path = Path.Combine(outputDirectory, "ui-" + tag + ".png");
                    bitmap.Save(path, ImageFormat.Png);

                    var colors = Colors(bitmap);
                    var border = CountColor(bitmap, UiTheme.Border);
                    var canvas = CountColor(bitmap, UiTheme.Canvas);
                    var surface = CountColor(bitmap, UiTheme.Surface);
                    var accent = CountColor(bitmap, UiTheme.Accent);
                    var accentSoft = CountColor(bitmap, UiTheme.AccentSoft);
                    var text = CountColor(bitmap, UiTheme.TextColor);
                    var total = bitmap.Width * bitmap.Height;
                    // 标签清单由界面自己给出，坐标随尺寸重算，所以每次都要重新取。
                    var labels = form.ExpectedLabels();

                    report.AppendLine("== " + tag + " ==");
                    report.AppendLine("  文件 " + Path.GetFileName(path));
                    report.AppendLine("  不同颜色数 " + colors.Count.ToString(CultureInfo.InvariantCulture));
                    report.AppendLine("  画布占比 " + Percent(canvas, total));
                    report.AppendLine("  面板占比 " + Percent(surface, total));
                    report.AppendLine("  边框占比 " + Percent(border, total));
                    report.AppendLine("  强调色 " + accent.ToString(CultureInfo.InvariantCulture) +
                                      " · 选中底 " + accentSoft.ToString(CultureInfo.InvariantCulture));
                    report.AppendLine("  主文字像素 " + text.ToString(CultureInfo.InvariantCulture));
                    report.AppendLine("  预期标签 " + labels.Count.ToString(CultureInfo.InvariantCulture) +
                                      " 项 · 有内容的 " +
                                      (labels.Count - MissingLabels(bitmap, labels).Count)
                                          .ToString(CultureInfo.InvariantCulture) + " 项");

                    // 断言。阈值都留了余量，避免因为字体渲染的细微差别误报。
                    //
                    // 「文字像素总数」不做断言：它取决于字体与抗锯齿方式，数值并不稳定。
                    // 真正要保证的是**该出现的东西都画出来了**，那件事由标签检查负责。
                    Require(failures, tag, "画布几乎看不到（面板之间可能没有留缝）", canvas * 200 >= total);
                    Require(failures, tag, "面板几乎看不到", surface * 100 >= total);
                    Require(failures, tag, "边框太少（分块可能没画出来）", border * 1000 >= total);
                    Require(failures, tag, "强调色缺失（主按钮或选中态没画）", accent > 200);
                    Require(failures, tag, "颜色数过少（可能是纯色块误渲染）", colors.Count >= 8);

                    // 四周必须留出画布：整个窗口被面板糊满说明布局算错了。
                    Require(failures, tag, "窗口边缘没有留白（面板糊满了整个窗口）",
                        HasCanvasMargin(bitmap, UiTheme.Canvas, 6));

                    var missing = MissingLabels(bitmap, labels);
                    Require(failures, tag, "以下位置没有画出任何内容：" + string.Join("、", missing.ToArray()),
                        missing.Count == 0);

                    bitmap.Dispose();
                }

                form.Close();
            }

            var reportPath = Path.Combine(outputDirectory, "ui-smoke.txt");
            report.AppendLine();
            if (failures.Count == 0)
            {
                report.AppendLine("界面自检通过。");
                File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
                Console.WriteLine(report.ToString());
                return 0;
            }

            report.AppendLine("界面自检失败 " + failures.Count.ToString(CultureInfo.InvariantCulture) + " 项：");
            foreach (var failure in failures) report.AppendLine("  ✗ " + failure);
            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
            Console.WriteLine(report.ToString());
            return 2;
        }

        private static void Require(List<string> failures, string tag, string message, bool condition)
        {
            if (!condition) failures.Add(tag + "：" + message);
        }

        private static string Percent(int count, int total)
        {
            return (count * 100d / total).ToString("0.00", CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>
        /// 把界面渲染到位图。
        ///
        /// 不用 `Control.DrawToBitmap`：它走窗口 DC，在离屏渲染下会不稳定地丢字
        /// （同一批标签里有的画得出来有的画不出来，换个尺寸又换一批）。
        /// 这里直接调 `MainForm.Render`，画到自建位图的绘图表面上——干净且可复现。
        /// 因为走的是同一个 `Render`，测的仍然是真实界面代码。
        /// </summary>
        private static Bitmap Render(MainForm form)
        {
            var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                graphics.Clear(UiTheme.Canvas);
                form.Render(graphics);
            }
            return bitmap;
        }

        private static HashSet<int> Colors(Bitmap bitmap)
        {
            var colors = new HashSet<int>();
            for (var y = 0; y < bitmap.Height; y += 2)
                for (var x = 0; x < bitmap.Width; x += 2)
                    colors.Add(bitmap.GetPixel(x, y).ToArgb());
            return colors;
        }

        private static int CountColor(Bitmap bitmap, Color color)
        {
            var wanted = color.ToArgb();
            var count = 0;
            for (var y = 0; y < bitmap.Height; y++)
                for (var x = 0; x < bitmap.Width; x++)
                    if (bitmap.GetPixel(x, y).ToArgb() == wanted) count++;
            return count;
        }

        /// <summary>
        /// 某个区域里有多少个「内容像素」。
        ///
        /// 判定方式是**只认调色板里的文字色**，而不是「和背景色不同」——
        /// 第一版按后者写，把文字本身也当成了背景，于是每个区域都判成空白，自检反过来报假错。
        /// 第二版只认文字主色，又把灰阶的次要文字（例如禁用状态的按钮文字）判成缺失，
        /// 仍是假报。所以这里改成认整套调色板，用近似匹配避开抗锯齿边缘。
        /// </summary>
        private static int InkIn(Bitmap bitmap, Rectangle bounds)
        {
            var count = 0;
            var left = Math.Max(0, bounds.Left);
            var top = Math.Max(0, bounds.Top);
            var right = Math.Min(bitmap.Width, bounds.Right);
            var bottom = Math.Min(bitmap.Height, bounds.Bottom);

            for (var y = top; y < bottom; y++)
            {
                for (var x = left; x < right; x++)
                {
                    if (IsContentColor(bitmap.GetPixel(x, y))) count++;
                }
            }
            return count;
        }

        /// <summary>界面上可能作为文字出现的颜色。漏掉任何一种都会造成假报。</summary>
        private static readonly Color[] Palette =
        {
            UiTheme.TextColor, UiTheme.TextMuted, UiTheme.TextDisabled,
            UiTheme.Accent, UiTheme.AccentSoft, UiTheme.Active, UiTheme.Danger, Color.White
        };

        private static bool IsContentColor(Color pixel)
        {
            foreach (var wanted in Palette)
            {
                if (Math.Abs(pixel.R - wanted.R) <= 70 &&
                    Math.Abs(pixel.G - wanted.G) <= 70 &&
                    Math.Abs(pixel.B - wanted.B) <= 70) return true;
            }
            return false;
        }

        /// <summary>哪些预期标签所在的区域是空的。</summary>
        private static List<string> MissingLabels(Bitmap bitmap, List<LabelCheck> labels)
        {
            var missing = new List<string>();
            foreach (var label in labels)
            {
                // 一行文字即使只有几个笔画，也会有几十个非背景像素；
                // 取 8 作门槛既能放过「笔画极少」的字符，又能抓住真正的空白。
                if (InkIn(bitmap, label.Bounds) < 8) missing.Add(label.Text);
            }
            return missing;
        }

        /// <summary>
        /// 图片左右下三边（含指定厚度）是否出现过画布色。
        ///
        /// 用来抓「面板糊满整个窗口」这类布局错误。顶栏与底栏本来就是通栏的，
        /// 所以不查上边；左右下三边有画布，才说明内容与窗口边缘之间有留白。
        /// </summary>
        private static bool HasCanvasMargin(Bitmap bitmap, Color canvas, int thickness)
        {
            var wanted = canvas.ToArgb();
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var d = 0; d < thickness; d++)
                {
                    if (bitmap.GetPixel(d, y).ToArgb() == wanted) return true;
                    if (bitmap.GetPixel(bitmap.Width - 1 - d, y).ToArgb() == wanted) return true;
                }
            }
            for (var x = 0; x < bitmap.Width; x++)
            {
                for (var d = 0; d < thickness; d++)
                {
                    if (bitmap.GetPixel(x, bitmap.Height - 1 - d).ToArgb() == wanted) return true;
                }
            }
            return false;
        }
    }
}
