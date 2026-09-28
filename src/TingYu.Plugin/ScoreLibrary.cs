using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TingYu.Core;

namespace TingYu.Plugin
{
    /// <summary>界面的配置。插件只读，写方永远是 Manager。</summary>
    public sealed class TingYuConfig
    {
        public bool Enabled = true;
        public string Track = "builtin:jasmine";
        public int Transpose;
        public int JitterTicks = 1;
        public bool ReleaseOnWorldEvent = true;
        public bool ReleaseOnMouseMove = false;
        public int HotkeyVirtualKey = Hotkey.DefaultVirtualKey;
        public bool Verbose;

        /// <summary>界面下发的即时请求：play / stop / reload / idle。</summary>
        public string Request = "idle";

        public string MusicFolder = TingYu.Core.MusicFolder.DefaultPath;

        /// <summary>
        /// 界面指定的乐器物品 ID；0 表示「不指定」。
        ///
        /// 默认是 0，也就是**不覆盖玩家手上拿的那件**。只有玩家在界面上主动点过某件乐器，
        /// 界面才会写入这个值。这样「界面上没选」与「选了但和手上一样」不会混为一谈，
        /// 也不会出现「玩家手动换了一件，程序又给他换回去」。
        /// </summary>
        public int PreferredInstrument;

        public static TingYuConfig Parse(IEnumerable<string> lines, TingYuConfig fallback)
        {
            var config = fallback ?? new TingYuConfig();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                var key = line.Substring(0, separator).Trim().ToLowerInvariant();
                var value = line.Substring(separator + 1).Trim();
                switch (key)
                {
                    case "enabled": config.Enabled = ParseBool(value, config.Enabled); break;
                    case "track": config.Track = value; break;
                    case "transpose": config.Transpose = ParseInt(value, config.Transpose); break;
                    case "jitter": config.JitterTicks = ParseInt(value, config.JitterTicks); break;
                    case "releaseonevent": config.ReleaseOnWorldEvent = ParseBool(value, config.ReleaseOnWorldEvent); break;
                    case "releaseonmousemove": config.ReleaseOnMouseMove = ParseBool(value, config.ReleaseOnMouseMove); break;
                    case "hotkey": config.HotkeyVirtualKey = ParseInt(value, config.HotkeyVirtualKey); break;
                    case "verbose": config.Verbose = ParseBool(value, config.Verbose); break;
                    case "request": config.Request = value; break;
                    case "musicfolder": config.MusicFolder = value; break;
                    case "instrument": config.PreferredInstrument = ParseInt(value, config.PreferredInstrument); break;
                }
            }
            return config;
        }

        public void Save(string path)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# 听雨的声音 —— 配置（界面写入，插件只读）");
            builder.AppendLine("enabled=" + (Enabled ? "1" : "0"));
            builder.AppendLine("track=" + Track);
            builder.AppendLine("transpose=" + Transpose.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("jitter=" + JitterTicks.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("releaseonevent=" + (ReleaseOnWorldEvent ? "1" : "0"));
            builder.AppendLine("releaseonmousemove=" + (ReleaseOnMouseMove ? "1" : "0"));
            builder.AppendLine("hotkey=" + HotkeyVirtualKey.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("verbose=" + (Verbose ? "1" : "0"));
            builder.AppendLine("musicfolder=" + MusicFolder);
            builder.AppendLine("instrument=" + PreferredInstrument.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("request=idle");
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        }

        private static bool ParseBool(string value, bool fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            value = value.Trim().ToLowerInvariant();
            if (value == "1" || value == "true" || value == "yes" || value == "on") return true;
            if (value == "0" || value == "false" || value == "no" || value == "off") return false;
            return fallback;
        }

        private static int ParseInt(string value, int fallback)
        {
            int parsed;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }
    }

    /// <summary>曲库里的一项：内置曲目或玩家放在音乐文件夹里的文件。</summary>
    public sealed class TrackEntry
    {
        /// <summary>稳定标识：内置是 `builtin:&lt;id&gt;`，本地文件是 `file:&lt;绝对路径&gt;`。</summary>
        public string Id;

        /// <summary>界面显示名。</summary>
        public string Title;

        /// <summary>副标题（来源描述）。</summary>
        public string Description;

        public string FilePath;
        public bool BuiltIn;
        public long SizeBytes;

        public override string ToString() { return Title; }
    }

    /// <summary>
    /// 曲库 = 内置曲目 + 音乐文件夹里的音频文件。
    ///
    /// 本地文件**不在这里解码**：解码一首 5 分钟的歌要几百毫秒到几秒，
    /// 放进游戏 tick 里会直接卡帧。解码与转录由 Manager（独立进程）先算好，
    /// 把结果写成 `.tyscore` 放在同目录，插件只读这个文本文件。
    /// 找不到 `.tyscore` 时该文件在界面上标为「未转录」，不会被选中。
    /// </summary>
    public sealed class ScoreLibrary
    {
        private readonly List<TrackEntry> _tracks = new List<TrackEntry>();
        private readonly Dictionary<string, InstrumentScore> _scores = new Dictionary<string, InstrumentScore>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<TrackEntry> Tracks { get { return _tracks; } }

        public void Load(string musicFolder)
        {
            _tracks.Clear();
            _scores.Clear();

            foreach (var score in BuiltInSongs.All())
            {
                var id = "builtin:" + IdFromTitle(score.Title);
                _scores[id] = score;
                _tracks.Add(new TrackEntry
                {
                    Id = id,
                    Title = score.Title,
                    Description = score.Source,
                    BuiltIn = true
                });
            }

            if (string.IsNullOrEmpty(musicFolder) || !Directory.Exists(musicFolder)) return;
            foreach (var entry in MusicFolder.List(musicFolder))
            {
                var id = "file:" + entry.Path;
                var sidecar = Path.ChangeExtension(entry.Path, ".tyscore");
                var track = new TrackEntry
                {
                    Id = id,
                    Title = entry.DisplayName,
                    Description = File.Exists(sidecar) ? "已转录" : "未转录",
                    FilePath = entry.Path,
                    BuiltIn = false,
                    SizeBytes = entry.SizeBytes
                };
                _tracks.Add(track);

                if (!File.Exists(sidecar)) continue;
                try
                {
                    var score = ScoreFile.Read(sidecar);
                    if (score != null && score.Notes.Count > 0) _scores[id] = score;
                }
                catch (Exception)
                {
                    track.Description = "转录文件损坏";
                }
            }
        }

        public TrackEntry Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var track in _tracks)
                if (string.Equals(track.Id, id, StringComparison.OrdinalIgnoreCase)) return track;
            return null;
        }

        public InstrumentScore GetScore(string id)
        {
            InstrumentScore score;
            return !string.IsNullOrEmpty(id) && _scores.TryGetValue(id, out score) ? score : null;
        }

        private static string IdFromTitle(string title)
        {
            switch (title)
            {
                case "茉莉花": return "jasmine";
                case "欢乐颂": return "ode-to-joy";
                case "绿袖子": return "greensleeves";
                default: return title;
            }
        }
    }

    /// <summary>
    /// 转录结果的落盘格式（`.tyscore`）。纯文本、一行一个音，便于人眼核对与手工修正：
    /// <code>
    /// # tingyu-score 1
    /// title=茉莉花
    /// tempo=120
    /// source=...
    /// note &lt;起始秒&gt; &lt;时长秒&gt; &lt;MIDI&gt;
    /// </code>
    /// </summary>
    public static class ScoreFile
    {
        public const string Header = "# tingyu-score 1";

        public static void Write(string path, InstrumentScore score)
        {
            var builder = new StringBuilder();
            builder.AppendLine(Header);
            builder.AppendLine("title=" + score.Title);
            builder.AppendLine("tempo=" + score.Tempo.ToString("0.####", CultureInfo.InvariantCulture));
            builder.AppendLine("source=" + score.Source);
            foreach (var note in score.Notes)
            {
                builder.Append("note ");
                builder.Append(note.StartBeat.ToString("0.#####", CultureInfo.InvariantCulture));
                builder.Append(' ');
                builder.Append(note.LengthBeats.ToString("0.#####", CultureInfo.InvariantCulture));
                builder.Append(' ');
                builder.Append(note.Midi.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine();
            }
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        }

        public static InstrumentScore Read(string path)
        {
            if (!File.Exists(path)) return null;
            var title = Path.GetFileNameWithoutExtension(path);
            var tempo = 60d;
            var source = string.Empty;
            var notes = new List<ScoreNote>();
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var separator = line.IndexOf('=');
                if (separator > 0)
                {
                    var key = line.Substring(0, separator).Trim().ToLowerInvariant();
                    var value = line.Substring(separator + 1).Trim();
                    if (key == "title") title = value;
                    else if (key == "tempo") double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out tempo);
                    else if (key == "source") source = value;
                    continue;
                }
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4 || !string.Equals(parts[0], "note", StringComparison.OrdinalIgnoreCase)) continue;
                double start, length;
                int midi;
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out start)) continue;
                if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out length)) continue;
                if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out midi)) continue;
                notes.Add(new ScoreNote(start, length, midi));
            }
            if (tempo <= 0d) tempo = 60d;
            return new InstrumentScore(title, tempo, notes, source);
        }
    }
}
