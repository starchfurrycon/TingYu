using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace TingYu.Patcher
{
    /// <summary>
    /// 找 Terraria.exe。
    ///
    /// 顺序：环境变量 → Steam 注册表 → Steam 库目录（含 libraryfolders.vdf 里的每个盘）
    /// → 常见安装位置。找不到就返回 null，让界面提示用户自己指定；
    /// **不会**去全盘搜索（那要几分钟，还会把机器卡住）。
    /// </summary>
    public static class TerrariaLocator
    {
        /// <summary>Steam 上 Terraria 的 appid。</summary>
        public const string TerrariaAppId = "105600";

        public static string FindTerrariaExe()
        {
            foreach (var candidate in Candidates())
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static IEnumerable<string> Candidates()
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("TINGYU_TERRARIA");
            if (!string.IsNullOrWhiteSpace(fromEnvironment)) yield return fromEnvironment;

            foreach (var root in SteamRoots())
            {
                var direct = Path.Combine(root, "steamapps", "common", "Terraria", "Terraria.exe");
                yield return direct;

                foreach (var library in LibraryFolders(Path.Combine(root, "steamapps", "libraryfolders.vdf")))
                {
                    yield return Path.Combine(library, "steamapps", "common", "Terraria", "Terraria.exe");
                }
            }

            yield return @"C:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe";
            yield return @"C:\Program Files\Steam\steamapps\common\Terraria\Terraria.exe";
            yield return @"D:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe";
            yield return @"D:\SteamLibrary\steamapps\common\Terraria\Terraria.exe";
            yield return @"E:\SteamLibrary\steamapps\common\Terraria\Terraria.exe";
        }

        private static IEnumerable<string> SteamRoots()
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                string value = null;
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
                    using (var key = baseKey.OpenSubKey(@"Software\Valve\Steam"))
                    {
                        if (key != null) value = key.GetValue("SteamPath") as string;
                    }
                }
                catch (Exception)
                {
                }

                if (!string.IsNullOrEmpty(value))
                    yield return value.Replace('/', Path.DirectorySeparatorChar);
            }
        }

        /// <summary>
        /// 读 Steam 的 `libraryfolders.vdf`。只做很浅的解析：找 `"path" "X:\\..."` 这一行，
        /// 没必要为这个引一个 VDF 解析器。
        /// </summary>
        public static IEnumerable<string> LibraryFolders(string vdfPath)
        {
            if (!File.Exists(vdfPath)) yield break;
            foreach (var line in File.ReadAllLines(vdfPath))
            {
                var text = line.Trim();
                if (!text.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;
                var first = text.IndexOf('"', 6);
                if (first < 0) continue;
                var second = text.IndexOf('"', first + 1);
                if (second < 0) continue;
                var value = text.Substring(first + 1, second - first - 1);
                yield return value.Replace("\\\\", "\\").Replace('/', Path.DirectorySeparatorChar);
            }
        }
    }
}
