using System;

namespace TingYu.Core.Audio
{
    /// <summary>
    /// 迭代式基-2 FFT，只输出幅度谱。
    ///
    /// 自己写而不是引第三方包：转录只需要「时域帧 → 幅度谱」这一件事，
    /// 为了几十行代码引一个庞大的数值库不划算，也会给发行包加一个必须随身携带的依赖。
    ///
    /// 缓冲全部由调用方传入并复用：一首 5 分钟的歌要算约 26000 帧，
    /// 每帧新分配两个 2048 长度的数组会产生 850 MB 垃圾。
    /// </summary>
    public sealed class Fft
    {
        private readonly int _size;
        private readonly int[] _reversed;
        private readonly float[] _cos;
        private readonly float[] _sin;
        private readonly float[] _window;

        public Fft(int size)
        {
            if (size < 2 || (size & (size - 1)) != 0)
                throw new ArgumentException("FFT 长度必须是 2 的幂：" + size, "size");
            _size = size;
            _reversed = new int[size];
            _cos = new float[size / 2];
            _sin = new float[size / 2];
            _window = new float[size];

            var bits = (int)Math.Round(Math.Log(size, 2d));
            for (var i = 0; i < size; i++)
                _reversed[i] = ReverseBits(i, bits);
            for (var i = 0; i < size / 2; i++)
            {
                _cos[i] = (float)Math.Cos(-2d * Math.PI * i / size);
                _sin[i] = (float)Math.Sin(-2d * Math.PI * i / size);
            }
            // Hann 窗：抑制频谱泄漏，否则相邻半音之间会互相污染。
            for (var i = 0; i < size; i++)
                _window[i] = 0.5f - 0.5f * (float)Math.Cos(2d * Math.PI * i / size);
        }

        public int Size { get { return _size; } }

        /// <summary>Hann 窗表（只读，长度 = Size）。</summary>
        public float[] Window { get { return _window; } }

        /// <summary>
        /// 对 `samples[offset .. offset+Size)` 做加窗 FFT，把幅度写进 `magnitude`（长度 ≥ Size/2）。
        /// 样本不足时补零。`realScratch` / `imaginaryScratch` 长度必须 ≥ Size。
        /// </summary>
        public void Magnitude(float[] samples, int offset, float[] magnitude, float[] realScratch, float[] imaginaryScratch)
        {
            if (magnitude == null || magnitude.Length < _size / 2)
                throw new ArgumentException("幅度缓冲区至少需要 " + (_size / 2) + " 个元素。", "magnitude");
            if (realScratch == null || realScratch.Length < _size)
                throw new ArgumentException("实部缓冲至少需要 " + _size + " 个元素。", "realScratch");
            if (imaginaryScratch == null || imaginaryScratch.Length < _size)
                throw new ArgumentException("虚部缓冲至少需要 " + _size + " 个元素。", "imaginaryScratch");

            var real = realScratch;
            var imaginary = imaginaryScratch;
            for (var i = 0; i < _size; i++)
            {
                var index = offset + i;
                real[i] = index >= 0 && index < samples.Length ? samples[index] * _window[i] : 0f;
                imaginary[i] = 0f;
            }

            for (var i = 0; i < _size; i++)
            {
                var j = _reversed[i];
                if (j <= i) continue;
                var swap = real[i]; real[i] = real[j]; real[j] = swap;
            }

            for (var length = 2; length <= _size; length <<= 1)
            {
                var half = length >> 1;
                var step = _size / length;
                for (var start = 0; start < _size; start += length)
                {
                    for (var k = 0; k < half; k++)
                    {
                        var twiddle = k * step;
                        var cos = _cos[twiddle];
                        var sin = _sin[twiddle];
                        var left = start + k;
                        var right = left + half;
                        var tr = real[right] * cos - imaginary[right] * sin;
                        var ti = real[right] * sin + imaginary[right] * cos;
                        real[right] = real[left] - tr;
                        imaginary[right] = imaginary[left] - ti;
                        real[left] += tr;
                        imaginary[left] += ti;
                    }
                }
            }

            for (var i = 0; i < _size / 2; i++)
                magnitude[i] = (float)Math.Sqrt(real[i] * real[i] + imaginary[i] * imaginary[i]);
        }

        private static int ReverseBits(int value, int bits)
        {
            var result = 0;
            for (var i = 0; i < bits; i++)
            {
                result = (result << 1) | (value & 1);
                value >>= 1;
            }
            return result;
        }
    }
}
