using System;
using System.IO;

namespace TingYu.Patcher
{
    /// <summary>
    /// 命令行入口。界面（Manager）也走这一套服务，保证「命令行能做的、界面也能做」。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                var command = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
                var exe = GetArgument(args, "--terraria") ?? TerrariaLocator.FindTerrariaExe();
                var payload = GetArgument(args, "--payload") ?? AppDomain.CurrentDomain.BaseDirectory;
                var service = new InstallationService();
                InstallStatus status;

                switch (command)
                {
                    case "install":
                        status = service.Install(exe, payload);
                        break;
                    case "restore":
                    case "uninstall":
                        status = service.Restore(exe);
                        break;
                    case "status":
                        status = service.GetStatus(exe);
                        break;
                    case "verify":
                        // 离线核对注入层用到的游戏成员。不需要启动游戏。
                        // 成员清单从插件 DLL 里读，保证与运行时用的是同一份。
                        var verifyPlugin = GetArgument(args, "--plugin") ?? Path.Combine(payload, "TingYu.Plugin.dll");
                        return MemberVerifier.Run(exe, verifyPlugin);
                    case "patch-copy":
                        // 只用于离线验证：把注入结果写到另一个文件，不碰游戏本体。
                        var plugin = GetArgument(args, "--plugin") ?? Path.Combine(payload, "TingYu.Plugin.dll");
                        var output = GetArgument(args, "--out") ?? Path.Combine(payload, "Terraria.patched.exe");
                        new AssemblyPatcher().Patch(exe, output, plugin);
                        Console.WriteLine("已写出注入副本：" + output);
                        return 0;
                    default:
                        Console.Error.WriteLine("用法: TingYu.Patcher [status|install|restore|verify|patch-copy] " +
                                                "[--terraria <Terraria.exe>] [--payload <目录>]");
                        return 2;
                }

                Console.WriteLine(status);
                switch (status.State)
                {
                    case InstallState.Installed:
                    case InstallState.CleanSupported:
                        return 0;
                    default:
                        return 1;
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("错误: " + exception.Message);
                return 1;
            }
        }

        private static string GetArgument(string[] args, string name)
        {
            for (var i = 0; i + 1 < args.Length; i++)
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                    return Path.GetFullPath(args[i + 1]);
            return null;
        }
    }
}
