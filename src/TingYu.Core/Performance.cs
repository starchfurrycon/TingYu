using System;
using System.Collections.Generic;

namespace TingYu.Core
{
    /// <summary>一次接管演奏的实时状态机。</summary>
    public enum PerformanceState
    {
        Idle,
        Playing,
        Finished
    }

    /// <summary>状态机要求宿主做的一件事。宿主（注入层）负责把它落成光标位置与按键。</summary>
    public enum PerformanceAction
    {
        None,

        /// <summary>发一个音：把光标放到 `PixelDistance` 处，并制造一次「按下」。</summary>
        Strike,

        /// <summary>松手：清掉本 tick 的按下状态，让下一次新的按下能被识别。</summary>
        Release,

        /// <summary>整首弹完，退出接管。</summary>
        Finish
    }

    /// <summary>状态机这一 tick 的决定。</summary>
    public struct PerformanceCommand
    {
        public PerformanceAction Action;
        public int Step;

        /// <summary>该级数所需的光标像素距离（已按现场 `SmallerScaledAxis` 换算）。</summary>
        public float PixelDistance;

        /// <summary>该级数写进 `Main.musicPitch` 的值，供日志/校验用。</summary>
        public float MusicPitch;

        /// <summary>这一音的目标 MIDI，供日志/校验用。</summary>
        public int Midi;
    }

    /// <summary>
    /// 乐谱 → 逐 tick 的光标/按键指令。
    ///
    /// 三件必须写清楚的事：
    /// 1. **音域折叠**：乐器的 13 级只有两个八度，整首旋律先按八度平移落进音域，
    ///    再取最近的可用档位，避免整段跑到音域外被夹成同一个音。
    /// 2. **击弦间隔**：原版 `useTime = 12`，两击之间至少 12 tick（0.2 秒），
    ///    所以乐谱里比这更密的音会被顺延，不会丢音、也不会挤在一起。
    /// 3. **人性化**：轻微的时间抖动让听感不像机器，但抖动上限受击弦间隔约束，
    ///    保证抖动本身不会造成丢音。
    /// </summary>
    public sealed class Performance
    {
        private readonly InstrumentScore _score;
        private readonly InstrumentModel _instrument;
        private readonly int _baseMidi;
        private readonly int _jitterTicks;
        private readonly Random _random;

        private int _nextNoteIndex;
        private int _lastStrikeTick = int.MinValue;
        private bool _needsRelease;

        public Performance(
            InstrumentScore score,
            InstrumentModel instrument,
            int baseMidi,
            int jitterTicks,
            int seed)
        {
            if (score == null) throw new ArgumentNullException("score");
            if (instrument == null) throw new ArgumentNullException("instrument");
            if (!instrument.IsPlayable)
                throw new ArgumentException("乐器没有可用的音高表：" + instrument.NameZh, "instrument");

            _score = score;
            _instrument = instrument;
            _baseMidi = baseMidi;
            _jitterTicks = Math.Max(0, jitterTicks);
            _random = new Random(seed);
            State = PerformanceState.Idle;
            MinimumGapTicks = Math.Max(1, instrument.UseTime);
        }

        public PerformanceState State { get; private set; }

        /// <summary>两击之间必须隔开的 tick 数（= 乐器 `useTime`）。</summary>
        public int MinimumGapTicks { get; private set; }

        /// <summary>已经过去的 tick（从接管开始算）。</summary>
        public int ElapsedTicks { get; private set; }

        public InstrumentScore Score { get { return _score; } }

        public InstrumentModel Instrument { get { return _instrument; } }

        public int BaseMidi { get { return _baseMidi; } }

        /// <summary>总进度 0..1，界面画进度条用。</summary>
        public double Progress
        {
            get
            {
                var total = _score.DurationTicks;
                if (total <= 0) return 1d;
                var value = ElapsedTicks / (double)total;
                return value < 0d ? 0d : (value > 1d ? 1d : value);
            }
        }

        public void Start()
        {
            State = PerformanceState.Playing;
            ElapsedTicks = 0;
            _nextNoteIndex = 0;
            _lastStrikeTick = int.MinValue;
            _needsRelease = false;
        }

        /// <summary>把旋律整体移调到乐器音域中心附近，返回建议的基准 MIDI。</summary>
        public static int SuggestBaseMidi(InstrumentScore score, InstrumentModel instrument, int fallbackBaseMidi)
        {
            if (score == null || score.Notes.Count == 0) return fallbackBaseMidi;
            var lowest = 0;
            var highest = 0;
            for (var i = 0; i < score.Notes.Count; i++)
            {
                var offset = instrument.SemitoneOffsetForStep(StepForRawSemitone(instrument, score.Notes[i].Midi - fallbackBaseMidi));
                var midi = fallbackBaseMidi + offset;
                if (i == 0 || midi < lowest) lowest = midi;
                if (i == 0 || midi > highest) highest = midi;
            }
            // 让旋律的中位落回基准音附近：整体平移偶数个半音，音级结构不变。
            var center = (lowest + highest) / 2;
            var shift = center - fallbackBaseMidi;
            return fallbackBaseMidi + shift - (Math.Abs(shift) % 2 == 0 ? 0 : Math.Sign(shift));
        }

        private static int StepForRawSemitone(InstrumentModel instrument, int semitones)
        {
            var bestStep = 0;
            var bestDistance = int.MaxValue;
            for (var step = PitchAxis.MinStep; step <= PitchAxis.MaxStep; step++)
            {
                var distance = Math.Abs(instrument.SemitoneOffsetForStep(step) - semitones);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestStep = step;
                }
            }
            return bestStep;
        }

        /// <summary>
        /// 推进一个 tick。宿主每个游戏 tick 调一次；返回本 tick 要做的事。
        ///
        /// 发一个音需要**两个** tick：第一 tick 制造一次「新的按下」，第二 tick 松手，
        /// 否则原版的 `Main.mouseLeft &amp;&amp; Main.mouseLeftRelease` 条件只在第一 tick 成立，
        /// 而连续按住不会重复触发。
        /// </summary>
        public PerformanceCommand Tick(float smallerScaledAxis)
        {
            var command = new PerformanceCommand();

            // 先处理上一 tick 留下的松手：这一步与 ElapsedTicks 无关，
            // 所以即使音符被顺延，松手也不会被吞掉。
            if (_needsRelease)
            {
                _needsRelease = false;
                command.Action = PerformanceAction.Release;
                ElapsedTicks++;
                return command;
            }

            if (State != PerformanceState.Playing)
            {
                command.Action = PerformanceAction.None;
                return command;
            }

            var dueTick = int.MaxValue;
            if (_nextNoteIndex < _score.Notes.Count)
                dueTick = _score.TickFromBeat(_score.Notes[_nextNoteIndex].StartBeat);

            // 密度超过击弦能力时顺延，不丢音。
            var readyToStrike = _lastStrikeTick == int.MinValue
                                || ElapsedTicks - _lastStrikeTick >= MinimumGapTicks;

            if (dueTick != int.MaxValue && ElapsedTicks >= dueTick && readyToStrike)
            {
                var note = _score.Notes[_nextNoteIndex];
                _nextNoteIndex++;
                var midi = FoldIntoRange(note.Midi);
                var step = _instrument.NearestStepForMidi(midi, _baseMidi);

                command.Action = PerformanceAction.Strike;
                command.Step = step;
                command.PixelDistance = PitchAxis.PixelDistanceFromStep(step, smallerScaledAxis);
                command.MusicPitch = PitchAxis.MusicPitchFromStep(step);
                command.Midi = _instrument.MidiForStep(step, _baseMidi);

                _lastStrikeTick = ElapsedTicks;
                _needsRelease = true;

                // 人性化抖动只吃掉「还没到下一音」的空隙，保证不会把下一音顶到击弦间隔里。
                if (_jitterTicks > 0 && _nextNoteIndex < _score.Notes.Count)
                {
                    var slack = _score.TickFromBeat(_score.Notes[_nextNoteIndex].StartBeat)
                                - ElapsedTicks - MinimumGapTicks - 1;
                    if (slack > 0)
                    {
                        var jitter = _random.Next(0, Math.Min(_jitterTicks, slack) + 1);
                        ElapsedTicks += jitter;
                    }
                }

                ElapsedTicks++;
                return command;
            }

            if (_nextNoteIndex >= _score.Notes.Count && ElapsedTicks >= _score.DurationTicks)
            {
                State = PerformanceState.Finished;
                command.Action = PerformanceAction.Finish;
                return command;
            }

            ElapsedTicks++;
            command.Action = PerformanceAction.None;
            return command;
        }

        /// <summary>
        /// 八度折叠：把超出乐器音域的音按八度平移回来，尽量靠近基准音。
        /// 音域是 [-12, +12] 半音（相对基准），所以先折到 ±12 内，再交给最近档位。
        /// </summary>
        private int FoldIntoRange(int midi)
        {
            var span = 24; // 13 级 × 2 半音 = 24 个半音
            var relative = midi - _baseMidi;
            while (relative > 12) relative -= span;
            while (relative < -12) relative += span;
            return _baseMidi + relative;
        }
    }
}
