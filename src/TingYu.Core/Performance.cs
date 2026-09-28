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

        /// <summary>
        /// 直接给 `PlayGuitarChord` / `PlayDrums` 用的归一化距离 `[0, 1]`。
        ///
        /// 拨弦乐器按 `1/6` 分档，档位边界与 13 级阶梯的边界**不重合**，
        /// 所以不能由 `PixelDistance` 反推，必须由 `Performance` 自己给出。
        /// 单音乐器不用这个值（它们认 `Main.musicPitch`）。
        /// </summary>
        public float NormalizedDistance;

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

        /// <summary>整首曲子的最低音，构造时算一次。</summary>
        private readonly int _songLow;

        /// <summary>整首曲子的最高音。</summary>
        private readonly int _songHigh;

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

            _songLow = score.Notes[0].Midi;
            _songHigh = score.Notes[0].Midi;
            for (var i = 1; i < score.Notes.Count; i++)
            {
                if (score.Notes[i].Midi < _songLow) _songLow = score.Notes[i].Midi;
                if (score.Notes[i].Midi > _songHigh) _songHigh = score.Notes[i].Midi;
            }
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

            if (instrument.Kind == InstrumentModel.SoundKind.Chord)
                return SuggestChordBaseMidi(score, instrument, fallbackBaseMidi);

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

        /// <summary>
        /// 拨弦乐器的基准音：和弦按**音级**贴合，所以基准音取什么八度都不改变匹配结果；
        /// 唯一要保证的是它落在旋律中间，免得 `MidiForStep` 报出的音名偏出好几个八度。
        /// 这里把旋律中点折算到离回退基准音最近的同名音上。
        /// </summary>
        private static int SuggestChordBaseMidi(InstrumentScore score, InstrumentModel instrument, int fallbackBaseMidi)
        {
            var lowest = score.Notes[0].Midi;
            var highest = score.Notes[0].Midi;
            for (var i = 1; i < score.Notes.Count; i++)
            {
                if (score.Notes[i].Midi < lowest) lowest = score.Notes[i].Midi;
                if (score.Notes[i].Midi > highest) highest = score.Notes[i].Midi;
            }

            var center = (lowest + highest) / 2;
            // 只在和弦根音所在的音级上取基准音，这样级数 0 报出来就是一个真实和弦根音。
            var roots = new List<int>();
            for (var bucket = 0; bucket < InstrumentModel.ChordBucketCount; bucket++)
            {
                var root = instrument.ChordTones[bucket][0] % 12;
                if (!roots.Contains(root)) roots.Add(root);
            }
            roots.Sort();

            var best = fallbackBaseMidi;
            var bestDistance = int.MaxValue;
            foreach (var root in roots)
            {
                // 把所有八度上的同名音都试一遍。
                for (var midi = root; midi <= 127; midi += 12)
                {
                    var distance = Math.Abs(midi - center);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = midi;
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// 和弦档位对应的光标像素距离。`PlayGuitarChord` 判的是 `range > 1/6 * n`，
        /// 所以每档取区间中点，正好落在边界上会掉进低一档。
        /// </summary>
        private static float ChordPixelDistance(int step, float smallerScaledAxis)
        {
            if (smallerScaledAxis <= 0f) return 0f;
            var bucket = InstrumentModel.ChordBucketForStep(step);
            return InstrumentModel.NormalizedDistanceForChordBucket(bucket) * (smallerScaledAxis / 2f);
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

                int step;
                if (_instrument.Kind == InstrumentModel.SoundKind.Chord)
                {
                    // 拨弦乐器只有 6 个固定和弦，没有半音阶梯，**不能做八度折叠**：
                    // 折叠会先把音移走，再拿移位后的音去匹配和弦，得到的是另一个和弦。
                    // 档位由「音在整首曲子音域里的相对高度」决定，见 ChordStepForMidi。
                    step = ChordStepForMidi(note.Midi);
                }
                else
                {
                    step = _instrument.NearestStepForMidi(FoldIntoRange(note.Midi), _baseMidi);
                }

                command.Action = PerformanceAction.Strike;
                command.Step = step;
                if (_instrument.Kind == InstrumentModel.SoundKind.Chord)
                {
                    var bucket = InstrumentModel.ChordBucketForStep(step);
                    command.NormalizedDistance = InstrumentModel.NormalizedDistanceForChordBucket(bucket);
                    command.PixelDistance = ChordPixelDistance(step, smallerScaledAxis);
                }
                else
                {
                    command.NormalizedDistance = (float)PitchAxis.NormalizedDistanceFromStep(step);
                    command.PixelDistance = PitchAxis.PixelDistanceFromStep(step, smallerScaledAxis);
                }
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
        /// 拨弦乐器的档位：把整首曲子的音域均匀铺到 6 个和弦上，最低音给最低档、
        /// 最高音给最高档。
        ///
        /// 为什么不按最近的同名音匹配：`Am – G – Bm – C – D – Em` 这六个和弦的音程
        /// **正好铺满 C 大调音阶**，而它们的根音只有 A、G、B、C、D、E 六个音级。
        /// 于是一首 C 大调旋律按「最近音级」匹配时，几乎每个音都能在 Am 或 G 里找到，
        /// 匹配结果必然塌缩到两三个和弦——按定义算对了，听感却是「只有两种音高」。
        /// 6 个和弦不可能忠实表达 7 声音阶，这是乐器的固有上限，不是可以修掉的错误；
        /// 既然无法忠实，就选「音高变化听得出来」这一头。
        ///
        /// 音域铺满后，一首曲子用到的和弦数与旋律宽度成正比，宽音域自然用满 6 档。
        /// </summary>
        private int ChordStepForMidi(int midi)
        {
            var span = _songHigh - _songLow;
            var bucket = 0;
            if (span > 0)
                bucket = (int)Math.Round((midi - _songLow) * (double)(InstrumentModel.ChordBucketCount - 1) / span);
            return PitchAxis.MinStep + InstrumentModel.ClampBucket(bucket);
        }

        /// <summary>
        /// 八度折叠：把超出乐器音域的音按八度平移回来，尽量靠近基准音。
        /// 音域是 [-12, +12] 半音（相对基准），所以先折到 ±12 内，再交给最近档位。
        ///
        /// **只适用于单音乐器。** 拨弦乐器那 6 个固定和弦没有「半音阶梯」，
        /// 折叠会先把音移走再去匹配和弦，等于在匹配另一个音，必须跳过这一步。
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
