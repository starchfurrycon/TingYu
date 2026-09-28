using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace TingYu.Core
{
    /// <summary>一首内置曲目的标识与显示名。</summary>
    public sealed class BuiltInSongInfo
    {
        public BuiltInSongInfo(string id, string title, string description)
        {
            Id = id;
            Title = title;
            Description = description;
        }

        /// <summary>稳定标识，写进配置用。改它等于让旧配置失效，所以不要改。</summary>
        public string Id { get; private set; }

        public string Title { get; private set; }

        public string Description { get; private set; }
    }

    /// <summary>
    /// 内置曲目。
    ///
    /// 三首都是公有领域（public domain）曲调，用「音名 + 拍长」的紧凑记谱写在源码里。
    /// 这样做的两个理由：
    /// 1. 不夹带任何有版权的录音，发行包干净；
    /// 2. 内置曲目的音高是**准确的**，不经过音频转录那一层估计——
    ///    它同时也是转录结果好不好用的参照物。
    ///
    /// 记谱格式：音名（如 `C4`、`F#5`、`Bb3`），`/n` 表示这个音占 n 拍（默认 1 拍），
    /// `R/n` 是休止 n 拍。速度按每拍 0.5 秒（120 BPM）。
    /// </summary>
    public static class BuiltInSongs
    {
        public const double Tempo = 120d;

        private sealed class SongDefinition
        {
            public string Id;
            public string Title;
            public string Description;
            public string[] Measures;
        }

        private static readonly SongDefinition[] Definitions =
        {
            new SongDefinition
            {
                Id = "jasmine",
                Title = "茉莉花",
                Description = "中国民歌 · 公有领域",
                Measures = new[]
                {
                    // 好 一 朵 美 丽 的 茉 莉 花
                    "E4 E4 G4/2 A4/2", "C5/2 C5/2 A4 G4 A4 G4",
                    // 好 一 朵 美 丽 的 茉 莉 花
                    "E4 E4 G4/2 A4/2", "C5/2 C5/2 A4 G4 A4 G4",
                    // 芬 芳 美 丽 满 枝 桠
                    "G4/2 G4/2 G4 A4 C5", "C5/2 C5/2 A4 G4 A4 G4",
                    // 又 香 又 白 人 人 夸
                    "G4/2 A4/2 C5/2 D5/2 C5/2 A4/2", "G4/2 A4/2 G4/2 E4/2 G4/2 A4/2",
                    // 让 我 来 将 你 摘 下
                    "C5 C5 A4 G4 A4 G4", "E4 E4 G4 A4 G4/2 E4/2",
                    // 送 给 别 人 家
                    "D4 E4 G4/2 A4/2 G4/2 E4/2", "D4 E4 D4 C4/2 R/2"
                }
            },
            new SongDefinition
            {
                Id = "ode-to-joy",
                Title = "欢乐颂",
                Description = "贝多芬《第九交响曲》第四乐章主题 · 公有领域",
                Measures = new[]
                {
                    "E4 E4 F4 G4", "G4 F4 E4 D4", "C4 C4 D4 E4", "E4/2 D4/2 D4/2 R/2",
                    "E4 E4 F4 G4", "G4 F4 E4 D4", "C4 C4 D4 E4", "D4/2 C4/2 C4/2 R/2",
                    "D4 D4 E4 C4", "D4 E4/2 F4/2 E4 C4", "D4 E4/2 F4/2 E4 D4",
                    "C4 D4 G3/2 R/2",
                    "E4 E4 F4 G4", "G4 F4 E4 D4", "C4 C4 D4 E4", "D4/2 C4/2 C4/2 R/2"
                }
            },
            new SongDefinition
            {
                Id = "greensleeves",
                Title = "绿袖子",
                Description = "英格兰传统曲调 · 公有领域",
                Measures = new[]
                {
                    "A4/2 C5/2 D5 E5/2 D5/2 B4/2 G4/2", "A4/2 B4/2 C5/2 A4/2 A4/2 G#4/2 A4/2 B4/2",
                    "G4/2 A4/2 B4/2 G4/2 E4/2 G4/2 A4/2 B4/2", "C5/2 B4/2 A4/2 G4/2 A4/2 B4/2 G4/2 E4/2",
                    "A4/2 C5/2 D5 E5/2 D5/2 B4/2 G4/2", "A4/2 B4/2 C5/2 A4/2 A4/2 G#4/2 A4/2 B4/2",
                    "G4/2 A4/2 B4/2 G4/2 E4/2 G4/2 A4/2 B4/2", "C5/2 B4/2 A4/2 G4/2 A4/2 G#4/2 A4/2 R/2"
                }
            }
        };

        /// <summary>全部内置曲目（顺序即界面顺序）。</summary>
        public static List<InstrumentScore> All()
        {
            var result = new List<InstrumentScore>(Definitions.Length);
            foreach (var definition in Definitions) result.Add(Build(definition));
            return result;
        }

        /// <summary>
        /// 全部内置曲目的 id 与标题。
        ///
        /// 界面上必须同时拿到这两个：id 是要写进配置的稳定标识，
        /// 标题是给人看的显示名，而 `InstrumentScore` 只带标题。
        /// 没有这个入口，界面就只能靠「按顺序对号入座」把 id 猜出来——
        /// 那种写法在曲目顺序一变就静默错位。
        /// </summary>
        public static List<BuiltInSongInfo> List()
        {
            var result = new List<BuiltInSongInfo>(Definitions.Length);
            foreach (var definition in Definitions)
                result.Add(new BuiltInSongInfo(definition.Id, definition.Title, definition.Description));
            return result;
        }

        public static InstrumentScore Find(string id)
        {
            foreach (var definition in Definitions)
            {
                if (string.Equals(definition.Id, id, StringComparison.OrdinalIgnoreCase))
                    return Build(definition);
            }
            return null;
        }

        private static InstrumentScore Build(SongDefinition definition)
        {
            var notes = new List<ScoreNote>();
            var beat = 0d;
            foreach (var measure in definition.Measures)
            {
                foreach (var token in measure.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var text = token.Trim();
                    var length = 1d;
                    var slash = text.IndexOf('/');
                    if (slash >= 0)
                    {
                        var lengthText = text.Substring(slash + 1);
                        text = text.Substring(0, slash);
                        double parsed;
                        if (double.TryParse(lengthText, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) && parsed > 0d)
                            length = parsed;
                    }

                    if (text.Length > 0 && (text[0] == 'R' || text[0] == 'r'))
                    {
                        beat += length;
                        continue;
                    }

                    notes.Add(new ScoreNote(beat, length, ParseMidi(text)));
                    beat += length;
                }
            }
            return new InstrumentScore(definition.Title, Tempo, notes, definition.Description);
        }

        /// <summary>`C4` / `F#5` / `Bb3` → MIDI 号。C4 = 60。</summary>
        public static int ParseMidi(string token)
        {
            if (string.IsNullOrEmpty(token)) throw new FormatException("空音名。");
            var letter = char.ToUpperInvariant(token[0]);
            int value;
            switch (letter)
            {
                case 'C': value = 0; break;
                case 'D': value = 2; break;
                case 'E': value = 4; break;
                case 'F': value = 5; break;
                case 'G': value = 7; break;
                case 'A': value = 9; break;
                case 'B': value = 11; break;
                default: throw new FormatException("无法识别的音名：" + token);
            }

            var index = 1;
            while (index < token.Length && (token[index] == '#' || token[index] == 'b' || token[index] == 'B'))
            {
                value += token[index] == '#' ? 1 : -1;
                index++;
            }

            if (index >= token.Length) throw new FormatException("音名缺少八度：" + token);
            var octave = int.Parse(token.Substring(index), CultureInfo.InvariantCulture);
            return value + (octave + 1) * 12;
        }

        /// <summary>
        /// 把一首曲目写成标准 WAV（16 位单声道 22050 Hz），用正弦加一点二次谐波合成。
        /// 给界面做「试听」用，也方便玩家拿去比对。
        /// </summary>
        public static void WritePreviewWave(InstrumentScore score, string path)
        {
            const int rate = 22050;
            var totalSeconds = score.DurationTicks / 60d;
            var totalSamples = Math.Max(rate / 10, (int)Math.Ceiling(totalSeconds * rate) + rate / 4);
            var buffer = new float[totalSamples];

            foreach (var note in score.Notes)
            {
                var start = (int)Math.Round(note.StartBeat * rate);
                var length = Math.Max(rate / 20, (int)Math.Round(note.LengthBeats * rate * 0.92d));
                var frequency = NoteNames.FrequencyFromMidi(note.Midi);
                for (var i = 0; i < length && start + i < totalSamples; i++)
                {
                    var t = i / (double)rate;
                    // 快起慢落：听感接近拨弦，也让连续同音能听出分界。
                    var envelope = Math.Exp(-2.6d * t) * (1d - Math.Exp(-260d * t));
                    var value = Math.Sin(2d * Math.PI * frequency * t) * 0.72d
                                + Math.Sin(4d * Math.PI * frequency * t) * 0.20d
                                + Math.Sin(6d * Math.PI * frequency * t) * 0.08d;
                    buffer[start + i] += (float)(value * envelope * 0.42d);
                }
            }

            var bytes = new byte[44 + totalSamples * 2];
            WriteAscii(bytes, 0, "RIFF");
            WriteInt32(bytes, 4, 36 + totalSamples * 2);
            WriteAscii(bytes, 8, "WAVE");
            WriteAscii(bytes, 12, "fmt ");
            WriteInt32(bytes, 16, 16);
            WriteInt16(bytes, 20, 1);
            WriteInt16(bytes, 22, 1);
            WriteInt32(bytes, 24, rate);
            WriteInt32(bytes, 28, rate * 2);
            WriteInt16(bytes, 32, 2);
            WriteInt16(bytes, 34, 16);
            WriteAscii(bytes, 36, "data");
            WriteInt32(bytes, 40, totalSamples * 2);
            for (var i = 0; i < totalSamples; i++)
            {
                var value = buffer[i];
                if (value > 0.98f) value = 0.98f;
                if (value < -0.98f) value = -0.98f;
                var sample = (short)(value * 32767f);
                bytes[44 + i * 2] = (byte)(sample & 0xFF);
                bytes[44 + i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, bytes);
        }

        private static void WriteAscii(byte[] target, int offset, string text)
        {
            for (var i = 0; i < text.Length; i++) target[offset + i] = (byte)text[i];
        }

        private static void WriteInt32(byte[] target, int offset, int value)
        {
            target[offset] = (byte)(value & 0xFF);
            target[offset + 1] = (byte)((value >> 8) & 0xFF);
            target[offset + 2] = (byte)((value >> 16) & 0xFF);
            target[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static void WriteInt16(byte[] target, int offset, int value)
        {
            target[offset] = (byte)(value & 0xFF);
            target[offset + 1] = (byte)((value >> 8) & 0xFF);
        }
    }
}
