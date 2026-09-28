using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TingYu.Plugin
{
    /// <summary>
    /// 落盘日志。
    ///
    /// 每 tick 都写会让游戏卡住，所以行先进环形缓冲，每 N 个 tick 冲一次盘；
    /// 出问题时（异常、释放接管）立即冲一次，保证「最后一次为什么退出」一定在文件里。
    ///
    /// 日志是排查注入类问题的唯一手段：进程崩了没有控制台可看，
    /// 所有断言都只能落在文件上。
    /// </summary>
    public sealed class Diagnostics
    {
        /// <summary>
        /// 日志编码用系统 ANSI 代码页（中文 Windows 即 GBK），不用 UTF-8。
        /// 原因很实际：这些日志是给人用记事本 / `type` / `Get-Content` 直接看的，
        /// 而 Windows PowerShell 5.1 的 `Get-Content` 默认按 ANSI 解码，
        /// 写 UTF-8 会让整份日志变成乱码。写入端与最常见的读取端保持一致更省事。
        /// </summary>
        private static readonly Encoding LogEncoding = Encoding.Default;

        private readonly string _path;
        private readonly Queue<string> _pending = new Queue<string>();
        private readonly object _sync = new object();
        private readonly bool _verbose;
        private int _written;

        public Diagnostics(string path, bool verbose)
        {
            _path = path;
            _verbose = verbose;
            try
            {
                var directory = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            }
            catch (Exception)
            {
            }
        }

        public string Path { get { return _path; } }

        public bool Verbose { get { return _verbose; } }

        /// <summary>详细日志（每 tick 一行）只在需要时开；平时只记事件。</summary>
        public void Verbose_(string message)
        {
            if (_verbose) Write(message);
        }

        public void Write(string message)
        {
            var line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + message;
            lock (_sync)
            {
                _pending.Enqueue(line);
                if (_pending.Count > 4000) _pending.Dequeue();
            }
        }

        public void Write(string format, params object[] args)
        {
            Write(string.Format(CultureInfo.InvariantCulture, format, args));
        }

        public void Flush()
        {
            List<string> lines;
            lock (_sync)
            {
                if (_pending.Count == 0) return;
                lines = new List<string>(_pending);
                _pending.Clear();
            }
            try
            {
                using (var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var writer = new StreamWriter(stream, LogEncoding))
                {
                    foreach (var line in lines)
                    {
                        writer.Write(line);
                        writer.Write('\n');
                        _written++;
                    }
                }
            }
            catch (Exception)
            {
                // 日志写不进去也不能让游戏崩：丢弃即可。
            }
        }

        public int WrittenLines { get { return _written; } }
    }
}
