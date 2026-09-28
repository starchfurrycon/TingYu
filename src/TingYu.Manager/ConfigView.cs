using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TingYu.Core;

namespace TingYu.Manager
{
    /// <summary>
    /// 管理器侧看到的配置。
    ///
    /// 与插件里的 `TingYuConfig` 是**同一份文件格式**，但刻意不复用那个类：
    /// 插件是注入进游戏进程的，管理器却要独立启动。让两个进程共享一个程序集
    /// 会带来版本耦合——插件更新了，管理器就再也读不了旧配置。所以格式共享、
    /// 类型各写一份，是这里唯一正确的做法。
    ///
    /// 文件编码必须是 UTF-8 无 BOM：插件用 `File.ReadAllLines` 默认编码读，
    /// 带 BOM 会让第一行的键名多出一个不可见字符，`enabled` 就解析不到了。
    /// </summary>
    internal sealed class TingYuConfigView
    {
        internal bool Enabled = true;
        internal string Track = "builtin:jasmine";
        internal int Transpose;
        internal int Jitter = 1;
        internal bool ReleaseOnEvent = true;
        internal bool ReleaseOnMouseMove;
        internal int HotkeyVirtualKey = 0x79; // F10
        internal bool Verbose;
        internal string MusicFolder = TingYu.Core.MusicFolder.DefaultPath;

        /// <summary>
        /// 界面指定的乐器物品 ID；0 表示不指定。
        ///
        /// 默认必须是 0：界面上「没有选中任何乐器」和「选中了第一件」是两种不同的意图，
        /// 用 0 才能区分。玩家没点过乐器时，接管应该用手上那件。
        /// </summary>
        internal int PreferredInstrument;

        internal static string FileName { get { return "config.txt"; } }

        internal static string PathFor(string dataDirectory)
        {
            return string.IsNullOrEmpty(dataDirectory) ? null : Path.Combine(dataDirectory, FileName);
        }

        internal static TingYuConfigView Load(string dataDirectory)
        {
            var view = new TingYuConfigView();
            var path = PathFor(dataDirectory);
            if (path == null || !File.Exists(path)) return view;

            try
            {
                foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    var separator = line.IndexOf('=');
                    if (separator <= 0) continue;
                    var key = line.Substring(0, separator).Trim().ToLowerInvariant();
                    var value = line.Substring(separator + 1).Trim();
                    switch (key)
                    {
                        case "enabled": view.Enabled = Bool(value, view.Enabled); break;
                        case "track": if (value.Length > 0) view.Track = value; break;
                        case "transpose": view.Transpose = Int(value, view.Transpose); break;
                        case "jitter": view.Jitter = Int(value, view.Jitter); break;
                        case "releaseonevent": view.ReleaseOnEvent = Bool(value, view.ReleaseOnEvent); break;
                        case "releaseonmousemove": view.ReleaseOnMouseMove = Bool(value, view.ReleaseOnMouseMove); break;
                        case "hotkey": view.HotkeyVirtualKey = Int(value, view.HotkeyVirtualKey); break;
                        case "verbose": view.Verbose = Bool(value, view.Verbose); break;
                        case "musicfolder": if (value.Length > 0) view.MusicFolder = value; break;
                        case "instrument": view.PreferredInstrument = Int(value, view.PreferredInstrument); break;
                    }
                }
            }
            catch (IOException)
            {
                // 插件可能正在写同一个文件。读失败就用默认值——
                // 配置读不到不该让界面起不来。
            }
            return view;
        }

        internal static void Save(string dataDirectory, TingYuConfigView view)
        {
            Write(dataDirectory, view, null, false);
        }

        /// <summary>
        /// 写入配置并附上一个即时请求。
        ///
        /// 请求是**一次性**的：插件读到后会把它改回 `idle`。
        /// 所以这里永远先写完整配置再写 request，避免出现「请求生效了但参数还是旧的」。
        /// </summary>
        internal static void Request(string dataDirectory, string request, TingYuConfigView view)
        {
            Write(dataDirectory, view, request, true);
        }

        private static void Write(string dataDirectory, TingYuConfigView view, string request, bool includeRequest)
        {
            var path = PathFor(dataDirectory);
            if (path == null) return;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var builder = new StringBuilder();
            builder.AppendLine("# 听雨的声音 —— 配置（界面写入，控制台只读）");
            builder.AppendLine("enabled=" + (view.Enabled ? "1" : "0"));
            builder.AppendLine("track=" + view.Track);
            builder.AppendLine("transpose=" + view.Transpose.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("jitter=" + view.Jitter.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("releaseonevent=" + (view.ReleaseOnEvent ? "1" : "0"));
            builder.AppendLine("releaseonmousemove=" + (view.ReleaseOnMouseMove ? "1" : "0"));
            builder.AppendLine("hotkey=" + view.HotkeyVirtualKey.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("verbose=" + (view.Verbose ? "1" : "0"));
            builder.AppendLine("musicfolder=" + view.MusicFolder);
            builder.AppendLine("instrument=" + view.PreferredInstrument.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("request=" + (includeRequest ? request : "idle"));

            // 无 BOM。带 BOM 会让插件把第一行的键读成 "\uFEFFenabled"。
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        }

        private static bool Bool(string value, bool fallback)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (value == "1" || value == "true" || value == "yes" || value == "on") return true;
            if (value == "0" || value == "false" || value == "no" || value == "off") return false;
            return fallback;
        }

        private static int Int(string value, int fallback)
        {
            int parsed;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }
    }

    /// <summary>
    /// 控制台（插件）写出来的运行状态。
    ///
    /// 管理器每秒读一次，所以这里对「文件正被写」这件事必须免疫：
    /// 读失败就保留上一次的结果，而不是把界面清空。
    /// </summary>
    internal sealed class PluginStatus
    {
        internal bool Ready;
        internal int Tick;
        internal bool Active;
        internal string Track;
        internal string Instrument;
        internal double Progress;
        internal int Strikes;
        internal int Total;
        internal int Confirmed;
        internal string Release;
        internal string ReleaseDetail;
        internal string Notice;
        internal string HeldItem;
        internal string Issue;
        internal double Axis;
        internal int Uptime;

        internal static PluginStatus Load(string dataDirectory)
        {
            var path = string.IsNullOrEmpty(dataDirectory) ? null : Path.Combine(dataDirectory, "status.txt");
            if (path == null || !File.Exists(path)) return null;

            var status = new PluginStatus();
            try
            {
                foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    var separator = line.IndexOf('=');
                    if (separator <= 0) continue;
                    var key = line.Substring(0, separator).Trim().ToLowerInvariant();
                    var value = line.Substring(separator + 1).Trim();
                    switch (key)
                    {
                        case "ready": status.Ready = value == "1"; break;
                        case "tick": status.Tick = ParseInt(value); break;
                        case "active": status.Active = value == "1"; break;
                        case "track": status.Track = value; break;
                        case "instrument": status.Instrument = value; break;
                        case "progress": status.Progress = ParseDouble(value); break;
                        case "strikes": status.Strikes = ParseInt(value); break;
                        case "total": status.Total = ParseInt(value); break;
                        case "confirmed": status.Confirmed = ParseInt(value); break;
                        case "release": status.Release = value; break;
                        case "releasedetail": status.ReleaseDetail = value; break;
                        case "notice": status.Notice = value; break;
                        case "helditem": status.HeldItem = value; break;
                        case "issue": status.Issue = value; break;
                        case "axis": status.Axis = ParseDouble(value); break;
                        case "uptime": status.Uptime = ParseInt(value); break;
                    }
                }
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            return status;
        }

        private static int ParseInt(string value)
        {
            int parsed;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }

        private static double ParseDouble(string value)
        {
            double parsed;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : 0d;
        }
    }
}
