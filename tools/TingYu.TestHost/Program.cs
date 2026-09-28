using System;
using System.IO;
using System.Reflection;
using System.Text;
using TingYu.Plugin;

namespace TingYu.TestHost
{
    /// <summary>
    /// 把 `TingYu.Plugin.Runtime` 拉进一个普通控制台进程里跑，用来定位初始化问题。
    ///
    /// 为什么需要它：在游戏里钩子出问题时，异常可能被吞掉，只剩「什么都没发生」这种现象。
    /// 放进控制台宿主里，异常就是普通异常，栈能直接打出来。
    ///
    /// 它**不能**验证注入点与光标覆盖——那两件事必须真的在 Terraria 里跑；
    /// 它能验证的是反射绑定、配置读取、日志写入、接管状态机的准入判定。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var dataDirectory = args.Length > 0
                ? args[0]
                : Path.Combine(AppContext.BaseDirectory, "TingYu");
            Directory.CreateDirectory(dataDirectory);

            // 插件按自己 DLL 的位置推数据目录。这里没有 Terraria.exe 同级可言，
            // 所以先把 DLL 复制到目标目录的同级再加载，模拟真实布局。
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, "TingYu.Plugin.dll");
            Console.WriteLine("数据目录: " + dataDirectory);
            Console.WriteLine("插件位置: " + assemblyPath);
            Console.WriteLine("插件存在: " + File.Exists(assemblyPath));

            try
            {
                var assembly = Assembly.LoadFrom(assemblyPath);
                Console.WriteLine("已加载: " + assembly.FullName);
                var runtime = assembly.GetType("TingYu.Plugin.Runtime", true);
                Console.WriteLine("已找到类型: " + runtime.FullName);

                var fields = runtime.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                Console.WriteLine("静态字段数: " + fields.Length);

                Console.WriteLine("--- 调用 Runtime.BeginFrame() ---");
                var beginFrame = runtime.GetMethod("BeginFrame", BindingFlags.Public | BindingFlags.Static);
                beginFrame.Invoke(null, null);
                Console.WriteLine("BeginFrame 返回了（没有抛出异常）");

                DumpState(runtime);
            }
            catch (TargetInvocationException exception)
            {
                Console.WriteLine("!!! 调用抛出异常 !!!");
                Console.WriteLine(exception.InnerException ?? exception);
            }
            catch (Exception exception)
            {
                Console.WriteLine("!!! 失败 !!!");
                Console.WriteLine(exception);
            }

            Console.WriteLine("=== boot.log ===");
            DumpFile(Path.Combine(dataDirectory, "boot.log"));
            Console.WriteLine("=== tingyu.log ===");
            DumpFile(Path.Combine(dataDirectory, "tingyu.log"));
            Console.WriteLine("=== status.txt ===");
            DumpFile(Path.Combine(dataDirectory, "status.txt"));
            return 0;
        }

        private static void DumpState(Type runtime)
        {
            foreach (var name in new[] { "_initialized", "_initFailed", "_issue", "_dataDirectory" })
            {
                var field = runtime.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
                Console.WriteLine("  " + name + " = " + (field == null ? "(无此字段)" : Convert.ToString(field.GetValue(null))));
            }
        }

        private static void DumpFile(string path)
        {
            if (!File.Exists(path)) { Console.WriteLine("(不存在)"); return; }
            foreach (var line in File.ReadAllLines(path, Encoding.GetEncoding(936))) Console.WriteLine(line);
        }
    }
}
