using System;
using System.Collections.Generic;

namespace TingYu.Core
{
    /// <summary>原版按下「使用/攻击」时决定是否发声的时机。</summary>
    public enum TriggerStyle
    {
        /// <summary>按住就连发（`itemAnimation > 0 &amp;&amp; ItemTimeIsZero`），竖琴与铃铛是这一类。</summary>
        Sustained,

        /// <summary>必须是一次新的按下（`Main.mouseLeft &amp;&amp; Main.mouseLeftRelease`），吉他斧与三把吉他是这一类。</summary>
        OnFreshClick
    }

    /// <summary>
    /// 一件可演奏乐器在「光标距离 → 音高」契约里的完整描述。
    ///
    /// 所有数值都来自 1.4.5.8 的反编译源码，不是推测：
    ///   `Item.SetDefaults`（useTime / useAnimation）
    ///   `Player.ItemCheck_PlayInstruments`（触发条件、发声 ID）
    ///   `Player.PlayGuitarChord`（吉他的和弦编号）
    ///   `Player.DefaultToGuitar`（holdStyle = 5）
    /// </summary>
    public sealed class InstrumentModel
    {
        private InstrumentModel(
            int itemId,
            string nameZh,
            string nameEn,
            string textureName,
            SoundKind kind,
            TriggerStyle trigger,
            int soundId,
            int useTime,
            int[] harmonySemitones,
            int[][] chordTones)
        {
            ItemId = itemId;
            NameZh = nameZh;
            NameEn = nameEn;
            TextureName = textureName;
            Kind = kind;
            Trigger = trigger;
            SoundId = soundId;
            UseTime = useTime;
            HarmonySemitones = harmonySemitones;
            ChordTones = chordTones;
        }

        /// <summary>乐器的发声方式。</summary>
        public enum SoundKind
        {
            /// <summary>单音：音高连续可分，按 `musicPitch` 定音（竖琴 / 铃铛 / 吉他斧）。</summary>
            Monophonic,

            /// <summary>和弦：只有固定几档（Ivy / Rain Song / Stellar Tune 的 `PlayGuitarChord`）。</summary>
            Chord
        }

        public int ItemId { get; private set; }
        public string NameZh { get; private set; }
        public string NameEn { get; private set; }

        /// <summary>`Content\Images\Item_&lt;id&gt;` 对应的贴图名，界面用游戏原生素材。</summary>
        public string TextureName { get; private set; }

        public SoundKind Kind { get; private set; }
        public TriggerStyle Trigger { get; private set; }

        /// <summary>`SoundID.Item&lt;n&gt;` 里那个 n。</summary>
        public int SoundId { get; private set; }

        /// <summary>两击之间的最小 tick 数（原版 `useTime`，这些乐器都是 12）。</summary>
        public int UseTime { get; private set; }

        /// <summary>
        /// 每级对应的半音偏移表。索引 0 对应级数 -6（最近的档），索引 12 对应 +6。
        /// 单音乐器按全音阶展开（每级 +2 半音）；和弦乐器给的是这一档和弦的根音偏移。
        /// </summary>
        public int[] HarmonySemitones { get; private set; }

        /// <summary>
        /// 拨弦乐器（常春藤 / 雨歌 / 星星吉他）的**和弦档位表**，6 档、每档一个音高集合。
        ///
        /// 依据 `Player.PlayGuitarChord`（1.4.5.8，逐行核对）：
        /// `num = 6`、每档宽度 `1/6`，`range` 越大档位越高，
        /// 对应 `SoundEngine.PlaySound` 的 50、52、47、51、48、49。
        ///
        /// 为什么必须有这张表：这类乐器**只有 6 个固定和弦**，不存在「13 级半音阶梯」。
        /// 曾经把它当成「13 级、每级不等半音」塞进和单音乐器一样的抽象里，
        /// 结果 `SemitoneOffsetForStep` 不是单调函数，八度折叠、基准音居中、
        /// 最近档位三处全部算错，一首曲子只能发出 3~5 个和弦。
        /// 现在改为「按和弦音集匹配」——目标是让旋律音尽量落在和弦音上。
        /// </summary>
        public int[][] ChordTones { get; private set; }

        /// <summary>拨弦乐器的档位数量（`PlayGuitarChord` 里的 `num`）。</summary>
        public const int ChordBucketCount = 6;

        /// <summary>把任意的吉他档位编号夹到合法范围 `[0, 5]`。</summary>
        public static int ClampBucket(int bucket)
        {
            if (bucket < 0) return 0;
            if (bucket > ChordBucketCount - 1) return ChordBucketCount - 1;
            return bucket;
        }

        /// <summary>档位 index 对应的归一化距离，供 `PlayGuitarChord` 分档。</summary>
        public static float NormalizedDistanceForChordBucket(int bucket)
        {
            bucket = ClampBucket(bucket);
            // 取每档区间的下沿再略微上抬：`PlayGuitarChord` 判的是 `range > 1/6 * n`，
            // 正好落在边界上会掉进低一档。
            return (bucket + 0.5f) / ChordBucketCount;
        }

        /// <summary>
        /// 目标 MIDI 音与第 <paramref name="bucket"/> 档和弦的贴合度，越小越贴合。
        /// 对三和弦取「离最近和弦音的半音距离」，再给根音一点优先，避免同分时来回跳档。
        /// </summary>
        public int ChordMatchScore(int bucket, int midi, int baseMidi)
        {
            var tones = ChordTones[bucket];
            var best = int.MaxValue;
            for (var i = 0; i < tones.Length; i++)
            {
                var candidate = baseMidi + tones[i];
                // 和弦音可以跨八度贴合，所以按八度折算到最近的同名音。
                var distance = Math.Abs(candidate - midi) % 12;
                if (distance > 6) distance = 12 - distance;
                if (distance < best) best = distance;
            }
            return best;
        }

        public bool IsPlayable
        {
            get
            {
                if (Kind == SoundKind.Chord)
                    return ChordTones != null && ChordTones.Length == ChordBucketCount;
                return HarmonySemitones != null && HarmonySemitones.Length == PitchAxis.StepCount;
            }
        }

        /// <summary>
        /// 级数 k 对应的半音偏移（相对乐器基准音）。
        /// 只对单音乐器成立；拨弦乐器没有半音阶梯，调用会抛异常，
        /// 免得又把它当成和弦表用。
        /// </summary>
        public int SemitoneOffsetForStep(int step)
        {
            if (HarmonySemitones == null)
                throw new InvalidOperationException(
                    NameZh + " 是固定和弦乐器，没有半音阶梯；请改用 ChordMatchScore。");
            if (step < PitchAxis.MinStep) step = PitchAxis.MinStep;
            if (step > PitchAxis.MaxStep) step = PitchAxis.MaxStep;
            return HarmonySemitones[step - PitchAxis.MinStep];
        }

        /// <summary>
        /// 把目标 MIDI 音映射到这件乐器能发出的最接近的一档，返回级数 k。
        /// <paramref name="baseMidi"/> 是乐器级数 0 所对应的 MIDI 音。
        ///
        /// 单音乐器按半音距离找最近档；拨弦乐器改按和弦音集匹配，
        /// 再折回 `[-6, +6]` 的级数域（吉他实际只有 6 档，见 `ChordBucketCount`）。
        /// </summary>
        public int NearestStepForMidi(int midi, int baseMidi)
        {
            if (Kind == SoundKind.Chord)
            {
                var bestBucket = 0;
                var bestScore = int.MaxValue;
                for (var bucket = 0; bucket < ChordBucketCount; bucket++)
                {
                    var score = ChordMatchScore(bucket, midi, baseMidi);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestBucket = bucket;
                    }
                }
                // 档位 0 落在级数 -6，档位 5 落在 -1：与 `1/6` 档宽的区间下沿一致。
                return PitchAxis.MinStep + bestBucket;
            }

            var bestStep = 0;
            var bestDistance = int.MaxValue;
            for (var step = PitchAxis.MinStep; step <= PitchAxis.MaxStep; step++)
            {
                var candidate = baseMidi + SemitoneOffsetForStep(step);
                var distance = Math.Abs(candidate - midi);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestStep = step;
                }
            }
            return bestStep;
        }

        /// <summary>级数 k 实际发出的 MIDI 音。拨弦乐器取该档和弦根音。</summary>
        public int MidiForStep(int step, int baseMidi)
        {
            if (Kind == SoundKind.Chord)
                return baseMidi + ChordTones[ChordBucketForStep(step)][0];
            return baseMidi + SemitoneOffsetForStep(step);
        }

        /// <summary>把级数域 `[-6, +6]` 映射到吉他档位 `[0, 5]`。</summary>
        public static int ChordBucketForStep(int step)
        {
            return ClampBucket(step - PitchAxis.MinStep);
        }

        /// <summary>全套可演奏乐器，顺序即界面展示顺序。</summary>
        public static IReadOnlyList<InstrumentModel> All
        {
            get { return Catalog; }
        }

        public static InstrumentModel Find(int itemId)
        {
            for (var i = 0; i < Catalog.Length; i++)
            {
                if (Catalog[i].ItemId == itemId) return Catalog[i];
            }
            return null;
        }

        /// <summary>这件物品 ID 是不是可演奏乐器。</summary>
        public static bool IsInstrument(int itemId)
        {
            return itemId > 0 && Find(itemId) != null;
        }

        /// <summary>
        /// 在背包里找一件乐器，返回它的槽位；没有则返回 -1。
        ///
        /// 热键栏（0–9）优先于主背包，因为那是玩家自己排好的顺位；
        /// 同一区域内按槽位顺序取第一件。这样「按 F8 就弹」不需要玩家先手动选中乐器，
        /// 而选中动作本身是可逆的（接管结束时换回原来那一格）。
        /// </summary>
        public static int FirstInstrumentSlot(IReadOnlyList<int> inventoryTypes, int hotbarSize)
        {
            if (inventoryTypes == null) return -1;
            for (var i = 0; i < inventoryTypes.Count && i < hotbarSize; i++)
            {
                if (IsInstrument(inventoryTypes[i])) return i;
            }
            for (var i = hotbarSize; i < inventoryTypes.Count; i++)
            {
                if (IsInstrument(inventoryTypes[i])) return i;
            }
            return -1;
        }

        private static int[] WholeToneSteps()
        {
            var table = new int[PitchAxis.StepCount];
            for (var step = PitchAxis.MinStep; step <= PitchAxis.MaxStep; step++)
            {
                table[step - PitchAxis.MinStep] = step * 2;
            }
            return table;
        }

        /// <summary>
        /// 拨弦乐器的 6 档和弦音集，索引 0 是**离光标最近**的一档，按根音从低到高排列。
        ///
        /// 音集取自 1.4.5.8 反编译里玩家实际看到的标签
        /// （`Main.DrawInterface_InstrumentMouseText`，`type` 为 4057/4372/4715 时）：
        /// 由近及远是 Am – G – Bm – C – D – Em，与 `Player.PlayGuitarChord` 的
        /// `1/6` 档宽一致。半音偏移以 C 为 0。
        ///
        /// **一处未能核实的地方**：`PlayGuitarChord` 各档实际播放的 `SoundID`
        /// 是 GuitarC(47)、GuitarD(48)、GuitarEm(49)、GuitarG(50)、GuitarBm(51)、GuitarAm(52)，
        /// 与 UI 标签的排列并不完全对应（近档 UI 写 Am，而 `SoundID.GuitarAm` 在最高档）。
        /// 这两者必有其一与听感不符，只有实测才能判定。这里选 UI 标签，
        /// 理由是玩家看到什么就应当听到什么；若听下来发现和弦对不上，
        /// 只需调整本表顺序，其余代码无需改动。
        /// </summary>
        private static int[][] GuitarChordTones()
        {
            return new[]
            {
                new[] { 9, 12, 16 },  // Am : A  C  E
                new[] { 7, 11, 14 },  // G  : G  B  D
                new[] { 11, 14, 18 }, // Bm : B  D  F#
                new[] { 0, 4, 7 },    // C  : C  E  G
                new[] { 2, 6, 9 },    // D  : D  F# A
                new[] { 4, 7, 11 }    // Em : E  G  B
            };
        }

        private static readonly InstrumentModel[] Catalog =
        {
            new InstrumentModel(
                508, "竖琴", "Harp", "Item_508",
                InstrumentModel.SoundKind.Monophonic, TriggerStyle.Sustained,
                26, 12, WholeToneSteps(), null),

            new InstrumentModel(
                507, "铃铛", "Bell", "Item_507",
                InstrumentModel.SoundKind.Monophonic, TriggerStyle.Sustained,
                35, 12, WholeToneSteps(), null),

            new InstrumentModel(
                1305, "吉他斧", "The Axe", "Item_1305",
                InstrumentModel.SoundKind.Monophonic, TriggerStyle.OnFreshClick,
                47, 12, WholeToneSteps(), null),

            new InstrumentModel(
                4372, "常春藤", "Ivy", "Item_4372",
                InstrumentModel.SoundKind.Chord, TriggerStyle.OnFreshClick,
                0, 12, null, GuitarChordTones()),

            new InstrumentModel(
                4057, "雨歌", "Rain Song", "Item_4057",
                InstrumentModel.SoundKind.Chord, TriggerStyle.OnFreshClick,
                0, 12, null, GuitarChordTones()),

            new InstrumentModel(
                4715, "星星吉他", "Stellar Tune", "Item_4715",
                InstrumentModel.SoundKind.Chord, TriggerStyle.OnFreshClick,
                0, 12, null, GuitarChordTones())
        };
    }
}
