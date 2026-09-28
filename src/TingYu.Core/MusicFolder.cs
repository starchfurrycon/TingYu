using System;
using System.Collections.Generic;
using System.IO;

namespace TingYu.Core
{
    /// <summary>音乐文件夹里的一项。</summary>
    public sealed class MusicFileEntry
    {
        public string Path;
        public string FileName;
        public long SizeBytes;
        public DateTime Modified;

        public string DisplayName
        {
            get
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(Path);
                return string.IsNullOrEmpty(name) ? FileName : name;
            }
        }

        public override string ToString()
        {
            return FileName;
        }
    }

    /// <summary>
    /// 玩家的本地音乐文件夹。
    ///
    /// 路径固定在 `%USERPROFILE%\Documents\My Games\Terraria\TingYu`：
    /// 和 Terraria 的存档放在一起，玩家不需要在界面里找路径，也不需要配置。
    /// 界面只列文件，不做递归搜索，避免不小心把整个磁盘扫一遍。
    /// </summary>
    public static class MusicFolder
    {
        public const string FolderName = "TingYu";

        private static readonly string[] DisplayExtensions = { ".wav", ".ogg", ".mp3", ".xnb" };

        /// <summary>默认音乐目录（不一定存在）。</summary>
        public static string DefaultPath
        {
            get
            {
                var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                return Path.Combine(Path.Combine(Path.Combine(documents, "My Games"), "Terraria"), FolderName);
            }
        }

        /// <summary>确保目录存在，返回路径。</summary>
        public static string Ensure(string path)
        {
            var target = string.IsNullOrEmpty(path) ? DefaultPath : path;
            Directory.CreateDirectory(target);
            return target;
        }

        /// <summary>列出目录里所有可解码的音频。</summary>
        public static List<MusicFileEntry> List(string path)
        {
            var target = string.IsNullOrEmpty(path) ? DefaultPath : path;
            var result = new List<MusicFileEntry>();
            if (!Directory.Exists(target)) return result;

            foreach (var file in Directory.GetFiles(target))
            {
                var extension = Path.GetExtension(file);
                var supported = false;
                foreach (var candidate in DisplayExtensions)
                {
                    if (string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase))
                    {
                        supported = true;
                        break;
                    }
                }
                if (!supported) continue;

                var info = new FileInfo(file);
                result.Add(new MusicFileEntry
                {
                    Path = file,
                    FileName = info.Name,
                    SizeBytes = info.Length,
                    Modified = info.LastWriteTime
                });
            }

            result.Sort(delegate (MusicFileEntry left, MusicFileEntry right)
            {
                return string.Compare(left.FileName, right.FileName, StringComparison.CurrentCultureIgnoreCase);
            });
            return result;
        }
    }
}
