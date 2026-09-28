using System;

namespace TingYu.Core
{
    /// <summary>
    /// 光标位置与音高之间那一层契约。
    ///
    /// 原版 `Terraria.Player.ItemCheck_PlayInstruments`（1.4.5.8，反编译逐行核对）对竖琴、
    /// 铃铛、吉他斧按同一套公式定音：
    ///
    /// <code>
    /// float dx = Main.mouseX + Main.screenPosition.X - playerCenter.X;
    /// float dy = Main.mouseY + Main.screenPosition.Y - playerCenter.Y;
    /// float d  = (float)Math.Sqrt(dx * dx + dy * dy);
    /// d /= Main.Camera.SmallerScaledAxis / 2f;   // 归一化到屏幕短边的一半
    /// if (d > 1f) d = 1f;                        // 越界截断
    /// d = d * 2f - 1f;                           // [-1, 1]
    /// d = (float)Math.Round(d * Player.musicNotes);
    /// Main.musicPitch = d / Player.musicNotes;   // 实际发声用的音高
    /// </code>
    ///
    /// `Player.musicNotes` 是常量 6，所以音高刻度是 13 个值（k = -6..+6），
    /// 每级 `1/6`。`Main.Camera.SmallerScaledAxis` 是 `Main.screenHeight - GameViewMatrix.Translation.Y * 2`
    /// （横屏时取短边），因此**每级音高对应的像素数会随窗口大小/缩放变化**，
    /// 必须每帧现场读取而不能写死。
    ///
    /// 竖琴 13 级 = 两个八度，故每级 = 2 个半音（全音）；wiki 的
    /// 「最小音程为全音、1/24 屏高 = 全音」与 `semiAxis / 6` 完全一致。
    /// </summary>
    public static class PitchAxis
    {
        /// <summary>`Player.musicNotes` 的取值，1.4.5.8 为常量 6。</summary>
        public const int NoteCount = 6;

        /// <summary>最低一级音高（k = -6，向下两个八度）。</summary>
        public const int MinStep = -NoteCount;

        /// <summary>最高一级音高（k = +6，向上两个八度）。</summary>
        public const int MaxStep = NoteCount;

        /// <summary>可选音高级数总数。</summary>
        public static readonly int StepCount = MaxStep - MinStep + 1;

        /// <summary>
        /// 把归一化距离（0 在玩家身上、1 在屏幕短边的一半处）量化为级数 k。
        /// 与 `Math.Round` 的中点位行为一致：恰好 .5 时向偶数取整在 .NET 里是银行家舍入，
        /// 而原版用的就是 `Math.Round(double)`，这里保持同一语义。
        /// </summary>
        public static int StepFromNormalizedDistance(double normalized)
        {
            if (normalized < 0d) normalized = 0d;
            if (normalized > 1d) normalized = 1d;
            var value = Math.Round((normalized * 2d - 1d) * NoteCount);
            var step = (int)value;
            if (step < MinStep) step = MinStep;
            if (step > MaxStep) step = MaxStep;
            return step;
        }

        /// <summary>级数 k 对应的归一化距离；与原版公式互为逆运算。</summary>
        public static double NormalizedDistanceFromStep(int step)
        {
            if (step < MinStep) step = MinStep;
            if (step > MaxStep) step = MaxStep;
            return (step / (double)NoteCount + 1d) / 2d;
        }

        /// <summary>
        /// 级数 k 需要的光标像素距离。`smallerScaledAxis` 就是
        /// `Main.Camera.SmallerScaledAxis`；级数越界会被夹到端点。
        /// </summary>
        public static float PixelDistanceFromStep(int step, float smallerScaledAxis)
        {
            if (smallerScaledAxis <= 0f) return 0f;
            return (float)(NormalizedDistanceFromStep(step) * (smallerScaledAxis / 2f));
        }

        /// <summary>像素距离反推级数，用于校验注入后原版真的读到我们想要的音高。</summary>
        public static int StepFromPixelDistance(double pixels, float smallerScaledAxis)
        {
            if (smallerScaledAxis <= 0f) return MinStep;
            return StepFromNormalizedDistance(pixels / (smallerScaledAxis / 2d));
        }

        /// <summary>
        /// 原版写进 `Main.musicPitch` 的值。注入层只需让原版读到同一个数字即可发声正确。
        /// </summary>
        public static float MusicPitchFromStep(int step)
        {
            if (step < MinStep) step = MinStep;
            if (step > MaxStep) step = MaxStep;
            return step / (float)NoteCount;
        }

        /// <summary>级数 k 相对基准音的半音数。竖琴这类 13 级乐器每级 2 个半音。</summary>
        public static int WholeToneSemitonesFromStep(int step)
        {
            return step * 2;
        }
    }
}
