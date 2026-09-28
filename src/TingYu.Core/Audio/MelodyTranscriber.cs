using System;
using System.Collections.Generic;

namespace TingYu.Core.Audio
{
    /// <summary>转录参数。默认值是按「单声部旋律」调的。</summary>
    public sealed class TranscriptionSettings
    {
        /// <summary>起音检测灵敏度，0 迟钝 / 1 敏感。</summary>
        public double OnsetSensitivity = 0.55d;

        /// <summary>两帧之间音高变化超过这个半音数就另起一个音。</summary>
        public double PitchChangeSemitones = 0.6d;

        /// <summary>短于这个长度的段会被丢掉（秒）。</summary>
        public double MinimumNoteSeconds = 0.07d;

        /// <summary>两个音之间允许的最大空隙；更大的空隙算休止（秒）。</summary>
        public double MaximumGapSeconds = 0.09d;

        /// <summary>音高平滑窗口（帧数，奇数）。用于压掉倍频跳变。</summary>
        public int SmoothingFrames = 5;

        /// <summary>转录结果的标题。</summary>
        public string Title = string.Empty;

        /// <summary>
        /// 乐谱速度。转录一律用 60 BPM：此时 1 拍 = 1 秒、1 tick = 1/60 秒，
        /// 音频的绝对时长被原样保留，速度估计值只作为显示用的参考值，
        /// 不参与发声时序——时序错了整首曲子就会变快或变慢。
        /// </summary>
        public const double ScoreTempo = 60d;
    }

    /// <summary>转录过程的诊断读数：界面与日志都要能看出「为什么是这样的结果」。</summary>
    public sealed class TranscriptionReport
    {
        public int FrameCount;
        public int VoicedFrames;
        public int OnsetCount;
        public double EstimatedTempo = 120d;
        public double DurationSeconds;
        public double LowestMidi = double.NaN;
        public double HighestMidi = double.NaN;
        public int NoteCount;
        public List<string> Warnings = new List<string>();

        /// <summary>旋律跨了几个半音。超过乐器音域（24 个半音）时会折叠八度。</summary>
        public double RangeSemitones
        {
            get
            {
                if (double.IsNaN(LowestMidi) || double.IsNaN(HighestMidi)) return 0d;
                return HighestMidi - LowestMidi;
            }
        }

        public override string ToString()
        {
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "帧 {0}（有声 {1}）· 起音 {2} · 速度参考 {3:0.0} BPM · 音域 {4}–{5}（{6:0} 半音）· 音符 {7}",
                FrameCount, VoicedFrames, OnsetCount, EstimatedTempo,
                double.IsNaN(LowestMidi) ? "-" : NoteNames.FromMidi((int)Math.Round(LowestMidi)),
                double.IsNaN(HighestMidi) ? "-" : NoteNames.FromMidi((int)Math.Round(HighestMidi)),
                RangeSemitones, NoteCount);
        }
    }

    /// <summary>转录结果：乐谱 + 诊断。</summary>
    public sealed class TranscriptionResult
    {
        public InstrumentScore Score;
        public TranscriptionReport Report;
    }

    /// <summary>
    /// 音频 → 单乐器单声部乐谱。
    ///
    /// 管线：解码 → 归一化 → 逐帧幅度谱 → 谱通量起音 + HPS 基频 → 中值平滑 → 分段
    /// → 量化到半音 → 输出按秒计时的乐谱。
    ///
    /// 有意为之的两条边界：
    /// 1. **不做和弦拆分**。乐器一次只发一个音，硬拆复音只会得到一条更烂的旋律线；
    ///    这里取每一帧最响的那条线，复音编曲会得到「主旋律大致可辨」的结果。
    /// 2. **不猜调号、不自动配和声**。识别到的是绝对音高；乐器音域不够时由
    ///    `Performance` 做八度折叠，而不是改旋律。
    /// </summary>
    public sealed class MelodyTranscriber
    {
        /// <summary>FFT 帧长。44.1 kHz 下 2048 点 ≈ 46 ms，低频分辨率约 10.8 Hz。</summary>
        public const int FftSize = 2048;

        /// <summary>帧移。512 点 ≈ 11.6 ms，约 86 帧/秒，够抓住十六分音符。</summary>
        public const int HopSize = 512;

        private readonly TranscriptionSettings _settings;

        public MelodyTranscriber(TranscriptionSettings settings)
        {
            _settings = settings ?? new TranscriptionSettings();
        }

        public TranscriptionResult Transcribe(AudioClip rawClip)
        {
            if (rawClip == null) throw new ArgumentNullException("rawClip");
            var clip = rawClip.Resampled(AudioDecoder.AnalysisSampleRate).Normalized();
            var report = new TranscriptionReport();
            report.DurationSeconds = clip.DurationSeconds;

            if (clip.Length < FftSize * 2)
            {
                report.Warnings.Add("音频太短（不足 " +
                    (FftSize * 2 / (double)AudioDecoder.AnalysisSampleRate).ToString("0.00") + " 秒）。");
                return Empty(report, clip.Source);
            }

            var detector = new PitchDetector(FftSize, clip.SampleRate) { MinimumFrequency = 50d, MaximumFrequency = 2200d };
            var flux = new SpectralFlux(FftSize / 2);
            var frameCount = 1 + (clip.Length - FftSize) / HopSize;
            var frameSeconds = HopSize / (double)clip.SampleRate;
            var pitches = new double[frameCount];
            var voiced = new bool[frameCount];
            var fluxes = new double[frameCount];

            for (var frame = 0; frame < frameCount; frame++)
            {
                var magnitude = detector.Spectrum(clip.Samples, frame * HopSize);
                fluxes[frame] = flux.Push(magnitude, magnitude.Length);
                var estimate = detector.Detect();
                if (estimate.Voiced && estimate.Frequency > 0d)
                {
                    voiced[frame] = true;
                    pitches[frame] = estimate.Midi;
                    report.VoicedFrames++;
                }
                else
                {
                    pitches[frame] = double.NaN;
                }
            }
            report.FrameCount = frameCount;

            var onsets = SpectralFlux.FindPeaks(fluxes, _settings.OnsetSensitivity, 0.06d, frameSeconds);
            report.OnsetCount = onsets.Length;
            report.EstimatedTempo = SpectralFlux.EstimateTempo(onsets, frameSeconds, 120d);

            var smoothed = Smooth(pitches, voiced, _settings.SmoothingFrames);
            var segments = BuildSegments(smoothed, voiced, onsets, frameSeconds);

            var notes = new List<ScoreNote>(segments.Count);
            foreach (var segment in segments)
            {
                if (double.IsNaN(segment.Midi)) continue;
                var quantized = NoteNames.QuantizeToSemitone(segment.Midi);
                if (quantized < 12 || quantized > 120) continue; // 乐器与人耳都有意义之外的音直接丢
                notes.Add(new ScoreNote(segment.StartSeconds, segment.LengthSeconds, quantized));
            }

            var merged = Merge(notes);
            var score = new InstrumentScore(
                string.IsNullOrEmpty(_settings.Title) ? "转录结果" : _settings.Title,
                TranscriptionSettings.ScoreTempo,
                merged,
                clip.Source);

            if (merged.Count > 0)
            {
                var lowest = merged[0].Midi;
                var highest = merged[0].Midi;
                foreach (var note in merged)
                {
                    if (note.Midi < lowest) lowest = note.Midi;
                    if (note.Midi > highest) highest = note.Midi;
                }
                report.LowestMidi = lowest;
                report.HighestMidi = highest;
            }
            report.NoteCount = merged.Count;

            if (report.NoteCount == 0)
                report.Warnings.Add("没有识别出任何音符：确认文件里确实有单声部旋律，或把起音灵敏度调高。");
            else if (report.RangeSemitones > 24d)
                report.Warnings.Add("旋律跨度超过 24 个半音（两个八度），超出部分会按八度折叠：旋律轮廓保留，绝对音高会变。");
            if (report.VoicedFrames < frameCount / 10)
                report.Warnings.Add("有声帧不到一成，可能是纯打击乐或环境音，转录结果基本没有参考价值。");

            return new TranscriptionResult { Score = score, Report = report };
        }

        private static TranscriptionResult Empty(TranscriptionReport report, string source)
        {
            var score = new InstrumentScore("转录结果", TranscriptionSettings.ScoreTempo, new ScoreNote[0], source);
            report.NoteCount = 0;
            return new TranscriptionResult { Score = score, Report = report };
        }

        /// <summary>中值平滑：压掉单帧的倍频跳变与毛刺。</summary>
        private static double[] Smooth(double[] pitches, bool[] voiced, int window)
        {
            if (window < 3) return (double[])pitches.Clone();
            var half = window / 2;
            var result = new double[pitches.Length];
            var buffer = new List<double>(window);
            for (var i = 0; i < pitches.Length; i++)
            {
                buffer.Clear();
                for (var k = i - half; k <= i + half; k++)
                {
                    if (k < 0 || k >= pitches.Length) continue;
                    if (!voiced[k] || double.IsNaN(pitches[k])) continue;
                    buffer.Add(pitches[k]);
                }
                if (buffer.Count == 0) { result[i] = double.NaN; continue; }
                buffer.Sort();
                result[i] = buffer[buffer.Count / 2];
            }
            return result;
        }

        /// <summary>相邻同音且空隙很小的段合并成一个长音。</summary>
        private List<ScoreNote> Merge(List<ScoreNote> notes)
        {
            var merged = new List<ScoreNote>(notes.Count);
            foreach (var note in notes)
            {
                if (merged.Count > 0)
                {
                    var previous = merged[merged.Count - 1];
                    var gap = note.StartBeat - (previous.StartBeat + previous.LengthBeats);
                    if (previous.Midi == note.Midi && gap <= _settings.MaximumGapSeconds)
                    {
                        previous.LengthBeats = note.StartBeat + note.LengthBeats - previous.StartBeat;
                        merged[merged.Count - 1] = previous;
                        continue;
                    }
                }
                merged.Add(note);
            }
            return merged;
        }

        private sealed class NoteSegment
        {
            public int StartFrame;
            public int Frames;
            public double Midi;
            public double StartSeconds;
            public double LengthSeconds;
        }

        private List<NoteSegment> BuildSegments(double[] pitches, bool[] voiced, int[] onsets, double frameSeconds)
        {
            var onsetSet = new HashSet<int>(onsets);
            var raw = new List<NoteSegment>();
            NoteSegment current = null;

            for (var frame = 0; frame < pitches.Length; frame++)
            {
                var isVoiced = voiced[frame] && !double.IsNaN(pitches[frame]);
                if (!isVoiced)
                {
                    current = null;
                    continue;
                }

                var continuous = current != null && frame == current.StartFrame + current.Frames;
                var startNew = !continuous
                               || onsetSet.Contains(frame)
                               || Math.Abs(pitches[frame] - current.Midi) >= _settings.PitchChangeSemitones;

                if (startNew)
                {
                    current = new NoteSegment { StartFrame = frame, Frames = 1, Midi = pitches[frame] };
                    raw.Add(current);
                }
                else
                {
                    // 帧数加权平均：长音不会被尾部的噪声拉跑。
                    current.Midi = (current.Midi * current.Frames + pitches[frame]) / (current.Frames + 1);
                    current.Frames++;
                }
            }

            var kept = new List<NoteSegment>(raw.Count);
            foreach (var segment in raw)
            {
                segment.StartSeconds = segment.StartFrame * frameSeconds;
                segment.LengthSeconds = segment.Frames * frameSeconds;
                if (segment.LengthSeconds < _settings.MinimumNoteSeconds) continue;
                kept.Add(segment);
            }
            return kept;
        }
    }
}
