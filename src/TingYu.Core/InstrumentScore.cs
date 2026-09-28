using System;
using System.Collections.Generic;

namespace TingYu.Core
{
    /// <summary>乐谱里的一个音。时间单位是拍，与 `InstrumentScore.Tempo` 一起换算成 tick。</summary>
    public struct ScoreNote
    {
        public ScoreNote(double startBeat, double lengthBeats, int midi)
        {
            StartBeat = startBeat;
            LengthBeats = lengthBeats;
            Midi = midi;
        }

        public double StartBeat;
        public double LengthBeats;
        public int Midi;

        public override string ToString()
        {
            return NoteNames.FromMidi(Midi) + "@" + StartBeat.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 单乐器乐谱：一串带起止时间的音符加上速度。
    ///
    /// 乐谱只描述「该发哪个音」，不描述「光标该在哪」——后者由 `Performance` 按
    /// 现场读到的 `Main.Camera.SmallerScaledAxis` 反算，因此换窗口大小/缩放后仍然对。
    /// </summary>
    public sealed class InstrumentScore
    {
        /// <summary>原版每 tick 60 帧，速度换算固定用这个常数。</summary>
        public const double TicksPerMinute = 60d * 60d;

        public InstrumentScore(string title, double tempo, IEnumerable<ScoreNote> notes, string source)
        {
            Title = title ?? string.Empty;
            Tempo = tempo <= 0d ? 120d : tempo;
            Source = source ?? string.Empty;
            Notes = new List<ScoreNote>(notes ?? new ScoreNote[0]);
            Notes.Sort(CompareByStart);
        }

        public string Title { get; private set; }

        /// <summary>每分钟拍数。拍的定义由 `BeatsPerSecond` 决定，1 拍 = 1 个四分音符。</summary>
        public double Tempo { get; private set; }

        /// <summary>来源描述：文件名 / 内置曲目。</summary>
        public string Source { get; private set; }

        public List<ScoreNote> Notes { get; private set; }

        public double TicksPerBeat
        {
            get { return TicksPerMinute / Tempo; }
        }

        /// <summary>全曲最后一个音的结束位置（tick）。</summary>
        public int DurationTicks
        {
            get
            {
                var last = 0d;
                for (var i = 0; i < Notes.Count; i++)
                {
                    var end = (Notes[i].StartBeat + Notes[i].LengthBeats) * TicksPerBeat;
                    if (end > last) last = end;
                }
                return (int)Math.Ceiling(last);
            }
        }

        /// <summary>把拍换算成 tick；负数按 0 处理。</summary>
        public int TickFromBeat(double beat)
        {
            var tick = beat * TicksPerBeat;
            if (tick < 0d) return 0;
            return (int)Math.Round(tick);
        }

        /// <summary>压缩掉完全重叠/重复的相邻音，并夹掉荒唐的长度。转录输出会用到。</summary>
        public InstrumentScore Normalized(double minimumLengthBeats)
        {
            var result = new List<ScoreNote>(Notes.Count);
            foreach (var note in Notes)
            {
                var length = note.LengthBeats;
                if (length < minimumLengthBeats) length = minimumLengthBeats;
                if (result.Count > 0)
                {
                    var previous = result[result.Count - 1];
                    var previousEnd = previous.StartBeat + previous.LengthBeats;
                    if (previous.Midi == note.Midi && Math.Abs(note.StartBeat - previousEnd) < 1e-6)
                    {
                        previous.LengthBeats = note.StartBeat + length - previous.StartBeat;
                        result[result.Count - 1] = previous;
                        continue;
                    }
                }
                result.Add(new ScoreNote(note.StartBeat, length, note.Midi));
            }
            return new InstrumentScore(Title, Tempo, result, Source);
        }

        /// <summary>整首移调（半音），用于把旋律挪进乐器音域。</summary>
        public InstrumentScore Transposed(int semitones)
        {
            if (semitones == 0) return this;
            var moved = new List<ScoreNote>(Notes.Count);
            foreach (var note in Notes)
            {
                moved.Add(new ScoreNote(note.StartBeat, note.LengthBeats, note.Midi + semitones));
            }
            return new InstrumentScore(Title, Tempo, moved, Source);
        }

        private static int CompareByStart(ScoreNote left, ScoreNote right)
        {
            var compare = left.StartBeat.CompareTo(right.StartBeat);
            if (compare != 0) return compare;
            return left.Midi.CompareTo(right.Midi);
        }
    }
}
