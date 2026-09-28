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
            int maxSteps)
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
            MaxSteps = maxSteps;
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

        /// <summary>可用级数的一半（= `Player.musicNotes` 语义下的 6）。</summary>
        public int MaxSteps { get; private set; }

        public bool IsPlayable
        {
            get { return HarmonySemitones != null && HarmonySemitones.Length == PitchAxis.StepCount; }
        }

        /// <summary>级数 k 对应的半音偏移（相对乐器基准音）。</summary>
        public int SemitoneOffsetForStep(int step)
        {
            if (step < PitchAxis.MinStep) step = PitchAxis.MinStep;
            if (step > PitchAxis.MaxStep) step = PitchAxis.MaxStep;
            return HarmonySemitones[step - PitchAxis.MinStep];
        }

        /// <summary>
        /// 把目标 MIDI 音映射到这件乐器能发出的最接近的一档，返回级数 k。
        /// <paramref name="baseMidi"/> 是乐器级数 0 所对应的 MIDI 音。
        /// </summary>
        public int NearestStepForMidi(int midi, int baseMidi)
        {
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

        /// <summary>级数 k 实际发出的 MIDI 音。</summary>
        public int MidiForStep(int step, int baseMidi)
        {
            return baseMidi + SemitoneOffsetForStep(step);
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
        /// 吉他的和弦根音表，索引 0 是**最近**的一档。
        ///
        /// 依据是 1.4.5.8 的反编译结果，不是 wiki：`PlayGuitarChord` 里档位越界越先判，
        /// 而 `Main` 的 `GameUI.Guitar*` 显示名把越界最远的那支（`num4 * 5` 以上）标成
        /// `GuitarEm`，往下依次 `GuitarD`、`GuitarC`、`GuitarBm`、`GuitarG`、`GuitarAm`。
        /// 于是由近及远是 **Am – G – Bm – C – D – Em**（与 wiki 的写法不同，
        /// 源码标签优先）。取根音半音偏移、以 C 为 0：A=9、G=7、B=11、C=0、D=2、E=4。
        ///
        /// 13 级表是这 6 档在两端的自然延伸：级数 k 与 `PlayGuitarChord` 的分档编号同域。
        /// </summary>
        private static int[] GuitarChordRoots()
        {
            return new[] { 9, 7, 11, 0, 2, 4, 9, 7, 11, 0, 2, 4, 9 };
        }

        private static readonly InstrumentModel[] Catalog =
        {
            new InstrumentModel(
                508, "竖琴", "Harp", "Item_508",
                InstrumentModel.SoundKind.Monophonic, TriggerStyle.Sustained,
                26, 12, WholeToneSteps(), PitchAxis.NoteCount),

            new InstrumentModel(
                507, "铃铛", "Bell", "Item_507",
                InstrumentModel.SoundKind.Monophonic, TriggerStyle.Sustained,
                35, 12, WholeToneSteps(), PitchAxis.NoteCount),

            new InstrumentModel(
                1305, "吉他斧", "The Axe", "Item_1305",
                InstrumentModel.SoundKind.Monophonic, TriggerStyle.OnFreshClick,
                47, 12, WholeToneSteps(), PitchAxis.NoteCount),

            new InstrumentModel(
                4372, "常春藤", "Ivy", "Item_4372",
                InstrumentModel.SoundKind.Chord, TriggerStyle.OnFreshClick,
                0, 12, GuitarChordRoots(), PitchAxis.NoteCount),

            new InstrumentModel(
                4057, "雨歌", "Rain Song", "Item_4057",
                InstrumentModel.SoundKind.Chord, TriggerStyle.OnFreshClick,
                0, 12, GuitarChordRoots(), PitchAxis.NoteCount),

            new InstrumentModel(
                4715, "星星吉他", "Stellar Tune", "Item_4715",
                InstrumentModel.SoundKind.Chord, TriggerStyle.OnFreshClick,
                0, 12, GuitarChordRoots(), PitchAxis.NoteCount)
        };
    }
}
