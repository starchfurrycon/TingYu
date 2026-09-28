using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TingYu.Patcher
{
    public enum InstallState
    {
        NotFound,
        CleanSupported,
        CleanUnsupported,
        Installed,
        InstalledButChanged,
        Invalid
    }

    public sealed class InstallStatus
    {
        public InstallState State;
        public string TerrariaExe;
        public string GameVersion;
        public string Sha256;
        public string Message;

        public bool CanInstall
        {
            get { return State == InstallState.CleanSupported || State == InstallState.Installed; }
        }

        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.AppendLine(Message);
            builder.AppendLine("路径: " + (TerrariaExe ?? "未找到"));
            if (!string.IsNullOrEmpty(GameVersion)) builder.AppendLine("版本: " + GameVersion);
            if (!string.IsNullOrEmpty(Sha256)) builder.AppendLine("SHA-256: " + Sha256);
            return builder.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// 安装 / 还原 / 查状态。
    ///
    /// 设计上与拆特一致的三条硬规矩：
    /// 1. **只认已核验的版本**。版本号与整份 exe 的 SHA-256 都对上才允许注入——1.4.5.8 的
    ///    偏移量是反编译核过的，换一个构建就未必。
    /// 2. **装之前先备份原版**，并把原版哈希写进清单；还原时先核哈希再换回，
    ///    避免把 Steam 更新过的东西当成原来的东西盖回去。
    /// 3. **装之前确认游戏没在跑**，否则文件被占用，而且半改状态最危险。
    /// </summary>
    public sealed class InstallationService
    {
        public const string SupportedVersion = "1.4.5.8";
        public const string SupportedSha256 = "960A03BFF6050CF7BE16DFC1A7B19E10FC2C4F8F835A6A3B135A50DD9E6BA2F3";

        /// <summary>游戏目录下属于我们的那个子目录名。</summary>
        public const string DataFolderName = "TingYu";

        private const string ManifestFileName = "tingyu-install.txt";
        private const string BackupFileName = "Terraria.exe.orig";

        /// <summary>载荷必须齐全，缺一个都不装。</summary>
        private static readonly string[] RequiredPayload =
        {
            "TingYu.Plugin.dll",
            "TingYu.Core.dll"
        };

        private static string DataDirectory(string terrariaExe)
        {
            return Path.Combine(Path.GetDirectoryName(terrariaExe), DataFolderName);
        }

        public static string ManifestPath(string terrariaExe)
        {
            return Path.Combine(DataDirectory(terrariaExe), ManifestFileName);
        }

        public static string BackupPath(string terrariaExe)
        {
            return Path.Combine(DataDirectory(terrariaExe), BackupFileName);
        }

        public InstallStatus GetStatus(string terrariaExe)
        {
            if (string.IsNullOrWhiteSpace(terrariaExe) || !File.Exists(terrariaExe))
                return NewStatus(InstallState.NotFound, terrariaExe, null, null, "未找到 Terraria.exe。");

            try
            {
                var version = FileVersionInfo.GetVersionInfo(terrariaExe).FileVersion;
                var hash = Sha256(terrariaExe);
                var manifest = ReadManifest(terrariaExe);
                if (manifest != null)
                {
                    var state = hash.Equals(manifest.PatchedSha256, StringComparison.OrdinalIgnoreCase)
                        ? InstallState.Installed
                        : InstallState.InstalledButChanged;
                    return NewStatus(state, terrariaExe, version, hash,
                        state == InstallState.Installed
                            ? "听雨的声音已注入。"
                            : "安装之后 Terraria.exe 被 Steam 或别的工具改动过，请先还原再重装。");
                }

                var supported = version == SupportedVersion &&
                                hash.Equals(SupportedSha256, StringComparison.OrdinalIgnoreCase);
                return NewStatus(supported ? InstallState.CleanSupported : InstallState.CleanUnsupported,
                    terrariaExe, version, hash,
                    supported
                        ? "检测到受支持的 Terraria " + SupportedVersion + "（原版，可以注入）。"
                        : "这个 Terraria.exe 不在已验证清单里（期望版本 " + SupportedVersion +
                          "）。Steam 更新过游戏之后必须重新核验，拒绝盲注入。");
            }
            catch (Exception exception)
            {
                return NewStatus(InstallState.Invalid, terrariaExe, null, null, exception.Message);
            }
        }

        public InstallStatus Install(string terrariaExe, string payloadDirectory)
        {
            EnsureGameClosed();
            var before = GetStatus(terrariaExe);
            if (!before.CanInstall) throw new InvalidOperationException(before.Message);

            foreach (var file in RequiredPayload)
            {
                if (!File.Exists(Path.Combine(payloadDirectory, file)))
                    throw new FileNotFoundException("安装载荷缺少 " + file + "。", Path.Combine(payloadDirectory, file));
            }

            if (before.State == InstallState.Installed)
                return Reinstall(terrariaExe, payloadDirectory, before);

            var data = DataDirectory(terrariaExe);
            Directory.CreateDirectory(data);

            var backup = BackupPath(terrariaExe);
            File.Copy(terrariaExe, backup, true);

            var pluginSource = Path.Combine(payloadDirectory, "TingYu.Plugin.dll");
            var coreSource = Path.Combine(payloadDirectory, "TingYu.Core.dll");
            var pluginTarget = Path.Combine(Path.GetDirectoryName(terrariaExe), "TingYu.Plugin.dll");
            var coreTarget = Path.Combine(Path.GetDirectoryName(terrariaExe), "TingYu.Core.dll");
            File.Copy(pluginSource, pluginTarget, true);
            File.Copy(coreSource, coreTarget, true);

            var patched = Path.Combine(Path.GetDirectoryName(terrariaExe), "Terraria.exe.tingyu-new");
            new AssemblyPatcher().Patch(terrariaExe, patched, pluginTarget);
            File.Replace(patched, terrariaExe, null, true);

            var manifest = new InstallManifest
            {
                OriginalSha256 = Sha256(backup),
                PatchedSha256 = Sha256(terrariaExe),
                GameVersion = before.GameVersion,
                PluginVersion = ReadAssemblyVersion(pluginTarget),
                InstalledUtc = DateTime.UtcNow.ToString("o")
            };
            WriteManifest(terrariaExe, manifest);

            var after = GetStatus(terrariaExe);
            after.Message = "注入完成：" + (after.State == InstallState.Installed ? "校验通过。" : "但校验未通过，请还原后重试。");
            return after;
        }

        /// <summary>已经装过：只需换掉载荷并重新注入。原版备份保持在第一次装的时候。</summary>
        private InstallStatus Reinstall(string terrariaExe, string payloadDirectory, InstallStatus before)
        {
            var data = DataDirectory(terrariaExe);
            var backup = BackupPath(terrariaExe);
            var manifest = ReadManifest(terrariaExe);
            if (!File.Exists(backup))
                throw new InvalidDataException("原版备份不见了，拒绝在已注入的文件上再注入。请用 Steam 校验文件完整性。");

            // 用备份反推出干净的原版，再走一遍完整注入流程。
            var clean = Path.Combine(Path.GetDirectoryName(terrariaExe), "Terraria.exe.tingyu-clean");
            File.Copy(backup, clean, true);
            File.Replace(clean, terrariaExe, null, true);

            var pluginTarget = Path.Combine(Path.GetDirectoryName(terrariaExe), "TingYu.Plugin.dll");
            var coreTarget = Path.Combine(Path.GetDirectoryName(terrariaExe), "TingYu.Core.dll");
            File.Copy(Path.Combine(payloadDirectory, "TingYu.Plugin.dll"), pluginTarget, true);
            File.Copy(Path.Combine(payloadDirectory, "TingYu.Core.dll"), coreTarget, true);

            var patched = Path.Combine(Path.GetDirectoryName(terrariaExe), "Terraria.exe.tingyu-new");
            new AssemblyPatcher().Patch(terrariaExe, patched, pluginTarget);
            File.Replace(patched, terrariaExe, null, true);

            WriteManifest(terrariaExe, new InstallManifest
            {
                OriginalSha256 = Sha256(backup),
                PatchedSha256 = Sha256(terrariaExe),
                GameVersion = before.GameVersion,
                PluginVersion = ReadAssemblyVersion(pluginTarget),
                InstalledUtc = DateTime.UtcNow.ToString("o")
            });

            var after = GetStatus(terrariaExe);
            after.Message = manifest == null ? "重新注入完成。" : "已更新到当前载荷版本。";
            return after;
        }

        public InstallStatus Restore(string terrariaExe)
        {
            EnsureGameClosed();
            if (string.IsNullOrWhiteSpace(terrariaExe) || !File.Exists(terrariaExe))
                return NewStatus(InstallState.NotFound, terrariaExe, null, null, "未找到 Terraria.exe。");

            var backup = BackupPath(terrariaExe);
            var manifest = ReadManifest(terrariaExe);
            if (manifest == null || !File.Exists(backup))
                throw new InvalidOperationException("这台机器上没有装过听雨的声音（缺备份或清单），无需还原。");
            if (!Sha256(backup).Equals(manifest.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("原版备份的哈希与清单不符，拒绝用一个来路不明的备份覆盖游戏。");

            var temporary = Path.Combine(Path.GetDirectoryName(terrariaExe), "Terraria.exe.tingyu-restore");
            File.Copy(backup, temporary, true);
            File.Replace(temporary, terrariaExe, null, true);

            var data = DataDirectory(terrariaExe);
            DeleteIfExists(Path.Combine(Path.GetDirectoryName(terrariaExe), "TingYu.Plugin.dll"));
            DeleteIfExists(Path.Combine(Path.GetDirectoryName(terrariaExe), "TingYu.Core.dll"));
            DeleteIfExists(ManifestPath(terrariaExe));
            DeleteIfExists(BackupPath(terrariaExe));
            try
            {
                if (Directory.Exists(data) && Directory.GetFileSystemEntries(data).Length == 0)
                    Directory.Delete(data);
            }
            catch (IOException)
            {
                // 目录残留无所谓，不影响游戏。
            }

            var status = GetStatus(terrariaExe);
            status.Message = "已还原成原版 Terraria。";
            return status;
        }

        public static void EnsureGameClosed()
        {
            var running = Process.GetProcessesByName("Terraria");
            try
            {
                if (running.Length > 0)
                    throw new InvalidOperationException("Terraria 正在运行，请先退出游戏再安装或还原。");
            }
            finally
            {
                foreach (var process in running) process.Dispose();
            }
        }

        internal sealed class InstallManifest
        {
            public string OriginalSha256;
            public string PatchedSha256;
            public string GameVersion;
            public string PluginVersion;
            public string InstalledUtc;
        }

        private static InstallManifest ReadManifest(string terrariaExe)
        {
            var path = ManifestPath(terrariaExe);
            if (!File.Exists(path)) return null;
            var manifest = new InstallManifest();
            foreach (var line in File.ReadAllLines(path))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                var key = line.Substring(0, separator).Trim();
                var value = line.Substring(separator + 1).Trim();
                switch (key)
                {
                    case "originalSha256": manifest.OriginalSha256 = value; break;
                    case "patchedSha256": manifest.PatchedSha256 = value; break;
                    case "gameVersion": manifest.GameVersion = value; break;
                    case "pluginVersion": manifest.PluginVersion = value; break;
                    case "installedUtc": manifest.InstalledUtc = value; break;
                }
            }
            return string.IsNullOrEmpty(manifest.PatchedSha256) ? null : manifest;
        }

        private static void WriteManifest(string terrariaExe, InstallManifest manifest)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# 听雨的声音 —— 安装清单（删掉这个文件等于放弃还原能力）");
            builder.AppendLine("originalSha256=" + manifest.OriginalSha256);
            builder.AppendLine("patchedSha256=" + manifest.PatchedSha256);
            builder.AppendLine("gameVersion=" + manifest.GameVersion);
            builder.AppendLine("pluginVersion=" + manifest.PluginVersion);
            builder.AppendLine("installedUtc=" + manifest.InstalledUtc);
            File.WriteAllText(ManifestPath(terrariaExe), builder.ToString(), new UTF8Encoding(false));
        }

        private static string ReadAssemblyVersion(string path)
        {
            try
            {
                return FileVersionInfo.GetVersionInfo(path).FileVersion ?? "unknown";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        public static string Sha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(stream);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var value in hash) builder.Append(value.ToString("X2"));
                return builder.ToString();
            }
        }

        private static InstallStatus NewStatus(InstallState state, string exe, string version, string hash, string message)
        {
            return new InstallStatus
            {
                State = state,
                TerrariaExe = exe,
                GameVersion = version,
                Sha256 = hash,
                Message = message
            };
        }
    }
}
