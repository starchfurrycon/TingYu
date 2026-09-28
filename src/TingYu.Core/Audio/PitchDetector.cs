using System;

namespace TingYu.Core.Audio
{
    /// <summary>一帧的基频估计结果。</summary>
    public struct PitchEstimate
    {
        public bool Voiced;
        public double Frequency;

        /// <summary>小数 MIDI（用于看偏差）。</summary>
        public double Midi;

        /// <summary>谐波乘积谱的峰值强度，做「这一帧有没有音」的门限。</summary>
        public double Strength;
    }

    /// <summary>
    /// 逐帧基频估计。用谐波乘积谱（HPS）：把幅度谱按整数倍下采样后相乘，
    /// 真正的基频会因为各次谐波都对上而脱颖而出。
    ///
    /// 纯单音旋律（长笛、八音盒、人声哼唱这类）能给出很好的结果；
    /// 复音编曲（整支乐队）只能取到最响的那条线，这点在界面上会写明，不假装能拆和弦。
    /// </summary>
    public sealed class PitchDetector
    {
        private readonly Fft _fft;
        private readonly float[] _magnitude;
        private readonly float[] _real;
        private readonly float[] _imaginary;
        private readonly double[] _binFrequency;

        public PitchDetector(int fftSize, int sampleRate)
        {
            SampleRate = sampleRate;
            _fft = new Fft(fftSize);
            _magnitude = new float[fftSize / 2];
            _real = new float[fftSize];
            _imaginary = new float[fftSize];
            _binFrequency = new double[fftSize / 2];
            for (var i = 0; i < _binFrequency.Length; i++)
                _binFrequency[i] = i * (double)sampleRate / fftSize;
        }

        public int SampleRate { get; private set; }

        public int FftSize { get { return _fft.Size; } }

        /// <summary>最近一次 `Analyze` 算出的幅度谱长度。</summary>
        public int BinCount { get { return _magnitude.Length; } }

        /// <summary>最低分析频率（低于它多半是节奏/隆隆声，不当作旋律）。</summary>
        public double MinimumFrequency = 55d;

        /// <summary>最高分析频率。高于 2 kHz 的旋律线在这个场景里没有意义。</summary>
        public double MaximumFrequency = 2000d;

        /// <summary>HPS 的谐波层数。</summary>
        public int Harmonics = 4;

        /// <summary>分析一帧并给出幅度谱（写进内部缓冲，供后续复用）。</summary>
        public float[] Spectrum(float[] samples, int offset)
        {
            _fft.Magnitude(samples, offset, _magnitude, _real, _imaginary);
            return _magnitude;
        }
        public PitchEstimate Analyze(float[] samples, int offset)
        {
            Spectrum(samples, offset);
            return Detect();
        }

        /// <summary>在最近一次 `Spectrum` 的结果上做 HPS 基频估计。</summary>
        public PitchEstimate Detect()
        {
            var estimate = new PitchEstimate();
            var binCount = _magnitude.Length;
            var minBin = Math.Max(2, (int)Math.Floor(MinimumFrequency * _fft.Size / SampleRate));
            var maxBin = Math.Min(binCount - 1, (int)Math.Ceiling(MaximumFrequency * _fft.Size / SampleRate));
            if (maxBin <= minBin) return estimate;

            // 噪声底：用整帧能量的均方根，避免在纯静音里报出一堆假音。
            var energy = 0d;
            for (var i = minBin; i <= maxBin; i++) energy += _magnitude[i] * _magnitude[i];
            var rms = Math.Sqrt(energy / (maxBin - minBin + 1));
            if (rms < 1e-4) return estimate;

            var bestScore = 0d;
            var bestBin = -1;
            for (var bin = minBin; bin <= maxBin; bin++)
            {
                var score = 1d;
                var valid = true;
                for (var harmonic = 1; harmonic <= Harmonics; harmonic++)
                {
                    var index = bin * harmonic;
                    if (index >= binCount) { valid = false; break; }
                    score *= _magnitude[index];
                    if (score <= 0d) { valid = false; break; }
                }
                if (!valid || score <= 0d) continue;
                // 对谐波层数取几何平均，避免高次谐波数量不同造成偏好偏差。
                score = Math.Pow(score, 1d / Harmonics);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestBin = bin;
                }
            }

            if (bestBin < 0) return estimate;

            // 抛物线插值细化峰值位置，把分辨率从「一个 bin」提到「几十分之一 bin」。
            var refined = (double)bestBin;
            if (bestBin > minBin && bestBin < maxBin)
            {
                var left = _magnitude[bestBin - 1];
                var middle = _magnitude[bestBin];
                var right = _magnitude[bestBin + 1];
                var denominator = left - 2d * middle + right;
                if (Math.Abs(denominator) > 1e-9d)
                {
                    var delta = 0.5d * (left - right) / denominator;
                    if (delta > -1d && delta < 1d) refined += delta;
                }
            }

            var frequency = refined * SampleRate / (double)_fft.Size;
            if (frequency < MinimumFrequency || frequency > MaximumFrequency) return estimate;

            estimate.Voiced = true;
            estimate.Frequency = frequency;
            estimate.Midi = NoteNames.MidiFromFrequency(frequency);
            estimate.Strength = bestScore / (rms + 1e-9d);
            return estimate;
        }
    }
}
