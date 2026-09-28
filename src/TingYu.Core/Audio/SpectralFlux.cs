using System;

namespace TingYu.Core.Audio
{
    /// <summary>
    /// 谱通量起音检测：相邻两帧幅度谱的正向增量之和。
    /// 音头出现时通量会突然抬起来，音高变化但连贯（连奏）时它很小——
    /// 这正是「哪里该另起一个音」需要的信息。
    /// </summary>
    public sealed class SpectralFlux
    {
        public SpectralFlux(int binCount)
        {
            _previous = new float[binCount];
        }

        private readonly float[] _previous;

        /// <summary>算一帧的通量，并更新内部状态。</summary>
        public double Push(float[] magnitude, int binCount)
        {
            var flux = 0d;
            var count = Math.Min(binCount, Math.Min(magnitude.Length, _previous.Length));
            for (var i = 0; i < count; i++)
            {
                var delta = magnitude[i] - _previous[i];
                if (delta > 0d) flux += delta;
                _previous[i] = magnitude[i];
            }
            return flux;
        }

        public void Reset()
        {
            Array.Clear(_previous, 0, _previous.Length);
        }

        /// <summary>
        /// 在通量序列上找峰值。阈值同时考虑全局均值与局部滑动均值，
        /// 这样既有整体门限，也不会漏掉安静段落里的音头。
        /// </summary>
        public static int[] FindPeaks(double[] flux, double sensitivity, double minimumSeparationSeconds, double frameSeconds)
        {
            if (flux == null || flux.Length == 0) return new int[0];
            var mean = 0d;
            for (var i = 0; i < flux.Length; i++) mean += flux[i];
            mean /= flux.Length;

            var maximum = 0d;
            for (var i = 0; i < flux.Length; i++) if (flux[i] > maximum) maximum = flux[i];
            if (maximum <= 1e-9d) return new int[0];

            // sensitivity 越小越敏感（接受的峰值越低）。
            var threshold = mean + (maximum - mean) * (0.02d + 0.5d * (1d - Clamp01(sensitivity)));
            var separation = Math.Max(1, (int)Math.Round(minimumSeparationSeconds / Math.Max(frameSeconds, 1e-6)));

            var peaks = new System.Collections.Generic.List<int>();
            var lastPeak = -separation;
            var window = Math.Max(2, separation);
            for (var i = 1; i < flux.Length - 1; i++)
            {
                if (flux[i] < threshold) continue;
                if (flux[i] < flux[i - 1] || flux[i] < flux[i + 1]) continue;

                // 局部均值也要被超过，避免长音尾巴上的小幅波动被判成新音头。
                var from = Math.Max(0, i - window);
                var to = Math.Min(flux.Length - 1, i + window);
                var local = 0d;
                for (var k = from; k <= to; k++) local += flux[k];
                local /= (to - from + 1);
                if (flux[i] < local * (1.15d + 0.85d * (1d - Clamp01(sensitivity)))) continue;

                if (i - lastPeak < separation) continue;
                peaks.Add(i);
                lastPeak = i;
            }
            return peaks.ToArray();
        }

        /// <summary>
        /// 从起音序列估速度（BPM）。做法是：把相邻起音间隔做成直方图，
        /// 在 60–200 BPM 范围内找最集中的那个周期。
        /// </summary>
        public static double EstimateTempo(int[] onsetFrames, double frameSeconds, double fallback)
        {
            if (onsetFrames == null || onsetFrames.Length < 4) return fallback;

            var intervals = new double[onsetFrames.Length - 1];
            for (var i = 1; i < onsetFrames.Length; i++)
                intervals[i - 1] = (onsetFrames[i] - onsetFrames[i - 1]) * frameSeconds;

            var best = fallback;
            var bestScore = 0d;
            for (var bpm = 60d; bpm <= 200d; bpm += 0.5d)
            {
                var beat = 60d / bpm;
                var score = 0d;
                foreach (var interval in intervals)
                {
                    if (interval <= 0d) continue;
                    // 间隔与拍长（或它的整数倍）对齐时加分，半拍也算弱对齐。
                    foreach (var multiple in new[] { 1d, 2d, 3d, 4d, 0.5d })
                    {
                        var ratio = interval / (beat * multiple);
                        var nearest = Math.Round(ratio);
                        if (nearest < 1d) continue;
                        var error = Math.Abs(ratio - nearest);
                        if (error < 0.12d) score += (0.12d - error) / multiple;
                    }
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    best = bpm;
                }
            }
            return best;
        }

        private static double Clamp01(double value)
        {
            if (value < 0d) return 0d;
            if (value > 1d) return 1d;
            return value;
        }
    }
}
