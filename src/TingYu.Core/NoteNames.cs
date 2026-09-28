using System;

namespace TingYu.Core
{
    /// <summary>音名工具：MIDI 号 ↔ 音名，以及频率换算。</summary>
    public static class NoteNames
    {
        private static readonly string[] Names =
        {
            "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"
        };

        /// <summary>中央 C 的 MIDI 号。</summary>
        public const int MiddleC = 60;

        /// <summary>十二平均律参考：A4 = MIDI 69 = 440 Hz。</summary>
        public const double ConcertA = 440d;

        public static string FromMidi(int midi)
        {
            var index = midi % 12;
            if (index < 0) index += 12;
            var octave = (int)Math.Floor(midi / 12d) - 1;
            return Names[index] + octave.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public static double FrequencyFromMidi(double midi)
        {
            return ConcertA * Math.Pow(2d, (midi - 69d) / 12d);
        }

        public static double MidiFromFrequency(double frequency)
        {
            if (frequency <= 0d) return double.NaN;
            return 69d + 12d * Math.Log(frequency / ConcertA, 2d);
        }

        /// <summary>把任意小数 MIDI 吸附到最近的整数半音。</summary>
        public static int QuantizeToSemitone(double midi)
        {
            return (int)Math.Round(midi, MidpointRounding.AwayFromZero);
        }

        /// <summary>把音高校正到最近的半音，返回偏差（半音为单位）。</summary>
        public static double CentsOffFromSemitone(double midi)
        {
            var nearest = Math.Round(midi);
            return (midi - nearest) * 100d;
        }
    }
}
