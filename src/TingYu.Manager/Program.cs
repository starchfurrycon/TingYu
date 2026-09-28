using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using TingYu.Patcher;

namespace TingYu.Manager
{
    /// <summary>
    /// 管理器入口。
    ///
    /// 除了正常起界面，还提供两个无人值守的模式，它们存在的理由很实际：
    /// 「点一下看看」是最慢的验证方式，而界面代码一旦画错，靠读源码几乎看不出来。
    /// 能自己跑起来、把结果落到文件和图片里，才能在没有显示器的环境下检查它。
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            var smokeDirectory = Argument(args, "--ui-smoke");
            if (smokeDirectory != null)
                return UiSmokeTest.Run(smokeDirectory);

            if (HasFlag(args, "--where"))
            {
                Console.WriteLine(TerrariaLocator.FindTerrariaExe() ?? "(未找到)");
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 未捕获异常绝不能只是让窗口无声消失：那会被误当成「程序不稳定」。
            // 写进日志并把消息摆到界面上，问题才能被定位。
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                ReportCrash(e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                ReportCrash(e.ExceptionObject as Exception);
            };

            Application.Run(new MainForm());
            return 0;
        }

        private static void ReportCrash(Exception exception)
        {
            if (exception == null) return;
            var text = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + exception;
            try
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TingYu");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "manager-error.log"), text + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch (Exception)
            {
            }
            MessageBox.Show(exception.Message, "听雨的声音", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private static bool HasFlag(string[] args, string name)
        {
            foreach (var argument in args)
                if (string.Equals(argument, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string Argument(string[] args, string name)
        {
            foreach (var argument in args)
            {
                if (argument.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                    return argument.Substring(name.Length + 1);
                if (string.Equals(argument, name, StringComparison.OrdinalIgnoreCase)) return string.Empty;
            }
            return null;
        }
    }
}
