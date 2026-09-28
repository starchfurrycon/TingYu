using System;

namespace TingYu.Core.Audio
{
    /// <summary>解码后的一段单声道浮点音频。所有采样都在 [-1, 1]。</summary>
    public sealed class AudioClip
    {
        public AudioClip(float[] samples, int sampleRate, string source)
        {
            Samples = samples ?? new float[0];
            SampleRate = sampleRate <= 0 ? 44100 : sampleRate;
            Source = source ?? string.Empty;
        }

        public float[] Samples { get; private set; }

        public int SampleRate { get; private set; }

        public string Source { get; private set; }

        public int Length { get { return Samples.Length; } }

        public double DurationSeconds
        {
            get { return Samples.Length / (double)SampleRate; }
        }

        /// <summary>把多声道降混成单声道。</summary>
        public static AudioClip FromInterleaved(short[] interleaved, int channels, int sampleRate, string source)
        {
            if (interleaved == null) throw new ArgumentNullException("interleaved");
            if (channels < 1) channels = 1;
            var frames = interleaved.Length / channels;
            var samples = new float[frames];
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < channels; channel++)
                    sum += interleaved[frame * channels + channel] / 32768f;
                samples[frame] = sum / channels;
            }
            return new AudioClip(samples, sampleRate, source);
        }

        /// <summary>线性重采样到目标采样率。转录统一在 22050 Hz 上做，够用且快一倍。</summary>
        public AudioClip Resampled(int targetSampleRate)
        {
            if (targetSampleRate <= 0 || targetSampleRate == SampleRate) return this;
            var ratio = SampleRate / (double)targetSampleRate;
            var length = (int)Math.Floor(Samples.Length / ratio);
            if (length <= 1) return new AudioClip(new float[0], targetSampleRate, Source);
            var result = new float[length];
            for (var i = 0; i < length; i++)
            {
                var position = i * ratio;
                var index = (int)position;
                var fraction = (float)(position - index);
                var a = Samples[index];
                var b = index + 1 < Samples.Length ? Samples[index + 1] : a;
                result[i] = a + (b - a) * fraction;
            }
            return new AudioClip(result, targetSampleRate, Source);
        }

        /// <summary>整体缩放到峰值 0.95，避免不同来源音量差异影响阈值。</summary>
        public AudioClip Normalized()
        {
            var peak = 0f;
            for (var i = 0; i < Samples.Length; i++)
            {
                var value = Math.Abs(Samples[i]);
                if (value > peak) peak = value;
            }
            if (peak <= 1e-6f) return this;
            var gain = 0.95f / peak;
            var result = new float[Samples.Length];
            for (var i = 0; i < Samples.Length; i++) result[i] = Samples[i] * gain;
            return new AudioClip(result, SampleRate, Source);
        }
    }
}
