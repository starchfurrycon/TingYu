using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace TingYu.Core.Audio
{
    /// <summary>解码失败时抛出，带可读原因。</summary>
    public sealed class AudioDecodeException : Exception
    {
        public AudioDecodeException(string message) : base(message) { }
        public AudioDecodeException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// 音频文件 → 单声道浮点采样。
    ///
    /// - `.wav`：自己解析 RIFF（PCM 8/16/24/32 位、单/多声道）。
    /// - `.ogg` / `.mp3`：走 Terraria 本体自带的 NVorbis / MP3Sharp（反射加载，
    ///   不复制、不重分发，用玩家机器上已有的那份）。找不到解码器时给出可操作的提示。
    /// - `.xnb`：Terraria 的音频资源格式，未压缩，直接读 WAVE 头 + PCM。
    ///
    /// 明确不支持其它格式：没有可靠的解码器就不假装能解。
    /// </summary>
    public static class AudioDecoder
    {
        /// <summary>转录统一在这个采样率上做。人声/旋律的基频远低于它，够用且省一半时间。</summary>
        public const int AnalysisSampleRate = 22050;

        /// <summary>超过这个长度的文件会被拒绝，避免一次解码吃掉几百 MB。</summary>
        public const double MaximumSeconds = 600d;

        public static readonly string[] SupportedExtensions = { ".wav", ".ogg", ".mp3", ".xnb" };

        public static bool IsSupported(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var extension = Path.GetExtension(path);
            if (string.IsNullOrEmpty(extension)) return false;
            for (var i = 0; i < SupportedExtensions.Length; i++)
            {
                if (string.Equals(SupportedExtensions[i], extension, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public static AudioClip Decode(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new AudioDecodeException("没有给出文件路径。");
            if (!File.Exists(path)) throw new AudioDecodeException("文件不存在：" + path);

            var extension = Path.GetExtension(path).ToLowerInvariant();
            AudioClip clip;
            switch (extension)
            {
                case ".wav":
                    clip = DecodeWave(File.ReadAllBytes(path), path);
                    break;
                case ".xnb":
                    clip = DecodeXnbSound(File.ReadAllBytes(path), path);
                    break;
                case ".ogg":
                    clip = DecodeOgg(path);
                    break;
                case ".mp3":
                    clip = DecodeMp3(path);
                    break;
                default:
                    throw new AudioDecodeException(
                        "不支持的音频格式「" + extension + "」。支持：" + string.Join("、", SupportedExtensions));
            }

            if (clip.Length == 0) throw new AudioDecodeException("解码后没有任何采样：" + Path.GetFileName(path));
            if (clip.DurationSeconds > MaximumSeconds)
                throw new AudioDecodeException(
                    "音频超过 " + MaximumSeconds.ToString("0", CultureInfo.InvariantCulture) + " 秒（实际 " +
                    clip.DurationSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒），请先裁剪。");
            return clip;
        }

        // ---------------------------------------------------------------- WAV

        /// <summary>解析 RIFF/WAVE。允许 fmt 与 data 之间夹别的块（LIST、fact 等）。</summary>
        public static AudioClip DecodeWave(byte[] data, string source)
        {
            if (data == null || data.Length < 44) throw new AudioDecodeException("WAV 文件太短。");
            if (!(data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'))
                throw new AudioDecodeException("不是 RIFF 文件。");
            if (!(data[8] == 'W' && data[9] == 'A' && data[10] == 'V' && data[11] == 'E'))
                throw new AudioDecodeException("不是 WAVE 文件。");

            var channels = 0;
            var sampleRate = 0;
            var bitsPerSample = 0;
            var formatTag = 0;
            var blockAlign = 0;
            var dataOffset = -1;
            var dataLength = 0;

            var position = 12;
            while (position + 8 <= data.Length)
            {
                var id = new string(new[] { (char)data[position], (char)data[position + 1], (char)data[position + 2], (char)data[position + 3] });
                var size = BitConverter.ToInt32(data, position + 4);
                var body = position + 8;
                if (size < 0 || body + size > data.Length)
                {
                    // 有些编码器把 data 块长度写成 0 或 -1（流式），按剩余长度处理。
                    if (id == "data") size = data.Length - body;
                    else break;
                }

                if (id == "fmt ")
                {
                    if (size < 16) throw new AudioDecodeException("fmt 块太短。");
                    formatTag = BitConverter.ToInt16(data, body);
                    channels = BitConverter.ToInt16(data, body + 2);
                    sampleRate = BitConverter.ToInt32(data, body + 4);
                    blockAlign = BitConverter.ToInt16(data, body + 12);
                    bitsPerSample = BitConverter.ToInt16(data, body + 14);
                }
                else if (id == "data")
                {
                    dataOffset = body;
                    dataLength = size;
                }

                position = body + size + (size % 2);
            }

            if (channels <= 0 || sampleRate <= 0 || bitsPerSample <= 0 || dataOffset < 0)
                throw new AudioDecodeException("WAV 缺少 fmt 或 data 块。");
            if (formatTag != 1 && formatTag != 0xFFFE)
                throw new AudioDecodeException("只支持 PCM 编码的 WAV（当前格式标记 " + formatTag + "）。");

            var samples = ReadPcm(data, dataOffset, dataLength, bitsPerSample, blockAlign, channels);
            return AudioClip.FromInterleaved(samples, channels, sampleRate, source).Normalized();
        }

        private static short[] ReadPcm(byte[] data, int offset, int length, int bitsPerSample, int blockAlign, int channels)
        {
            var bytesPerSample = bitsPerSample / 8;
            if (bytesPerSample < 1) throw new AudioDecodeException("位深不受支持：" + bitsPerSample);
            var frameBytes = blockAlign > 0 ? blockAlign : bytesPerSample * channels;
            var frames = length / frameBytes;
            var result = new short[frames * channels];
            for (var frame = 0; frame < frames; frame++)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    var at = offset + frame * frameBytes + channel * bytesPerSample;
                    if (at + bytesPerSample > data.Length) break;
                    int value;
                    switch (bytesPerSample)
                    {
                        case 1:
                            value = (data[at] - 128) << 8;
                            break;
                        case 2:
                            value = (short)(data[at] | (data[at + 1] << 8));
                            break;
                        case 3:
                            value = (data[at] | (data[at + 1] << 8) | (data[at + 2] << 16)) >> 8;
                            if ((data[at + 2] & 0x80) != 0) value |= unchecked((int)0xFF000000);
                            break;
                        default:
                            value = BitConverter.ToInt32(data, at) >> 16;
                            break;
                    }
                    if (value > short.MaxValue) value = short.MaxValue;
                    if (value < short.MinValue) value = short.MinValue;
                    result[frame * channels + channel] = (short)value;
                }
            }
            return result;
        }

        // ---------------------------------------------------------------- XNB

        private static int Read7BitEncodedInt(byte[] data, ref int position)
        {
            var result = 0;
            var shift = 0;
            while (true)
            {
                var value = data[position++];
                result |= (value & 0x7F) << shift;
                if ((value & 0x80) == 0) break;
                shift += 7;
            }
            return result;
        }

        /// <summary>
        /// 读 Terraria 的 `.xnb` 音效。Terraria 的 XNB 是平台 'w'、版本 5、未压缩，
        /// SoundEffectReader 的载荷就是 WAVE 头加裸 PCM。
        ///
        /// 两个坑都在这里解决：
        /// 1. 文件可能带 UTF-8 BOM，必须先跳过；
        /// 2. 声明了共享资源时，索引 0 是本对象自己的资源 id，`Read` 会先吃掉那 4 个字节，
        ///    不跳就会把格式标记读成 0x1201。
        /// </summary>
        public static AudioClip DecodeXnbSound(byte[] raw, string source)
        {
            var start = 0;
            if (raw.Length > 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) start = 3;
            if (raw.Length < start + 10 ||
                raw[start] != 'X' || raw[start + 1] != 'N' || raw[start + 2] != 'B')
                throw new AudioDecodeException("不是 XNB 文件。");

            var position = start + 10; // 'XNB' + platform + version + flags + uint 文件长度
            var readers = Read7BitEncodedInt(raw, ref position);
            for (var i = 0; i < readers; i++)
            {
                var nameLength = Read7BitEncodedInt(raw, ref position);
                position += nameLength + 4; // 类型名 + 读入器版本
            }
            var shared = Read7BitEncodedInt(raw, ref position);
            if (shared == 1) position += 4;
            else if (shared != 0)
                throw new AudioDecodeException("XNB 声明了 " + shared + " 个共享资源，暂不支持。");

            var fmt = BitConverter.ToInt16(raw, position);
            var channels = BitConverter.ToInt16(raw, position + 2);
            var sampleRate = BitConverter.ToInt32(raw, position + 4);
            var bitsPerSample = BitConverter.ToInt16(raw, position + 14);
            position += 16;
            var extra = BitConverter.ToInt16(raw, position);
            position += 2 + extra;
            var size = BitConverter.ToInt32(raw, position);
            position += 4;
            if (position + size > raw.Length) size = raw.Length - position;
            if (fmt != 1) throw new AudioDecodeException("XNB 里的音频不是 PCM（格式标记 " + fmt + "）。");

            var pcm = new byte[size];
            Buffer.BlockCopy(raw, position, pcm, 0, size);
            var samples = ReadPcm(pcm, 0, size, bitsPerSample, channels * (bitsPerSample / 8), channels);
            return AudioClip.FromInterleaved(samples, channels, sampleRate, source).Normalized();
        }

        // ---------------------------------------------------------------- OGG / MP3

        /// <summary>
        /// 用 Terraria 自带的 NVorbis 解 OGG。反射调用是为了不把第三方 DLL 复制进发行包：
        /// 玩家机器上 Terraria 一定带着它。
        /// </summary>
        public static AudioClip DecodeOgg(string path)
        {
            var type = FindType("NVorbis.VorbisReader");
            if (type == null)
                throw new AudioDecodeException("找不到 NVorbis（OGG 解码器）。请把音频转成 WAV，或确认 Terraria 安装目录里有 NVorbis.dll。");

            IDisposable reader = null;
            try
            {
                object instance;
                try
                {
                    instance = Activator.CreateInstance(type, new object[] { path });
                }
                catch (MissingMethodException)
                {
                    instance = Activator.CreateInstance(type, new object[] { path, true });
                }
                reader = instance as IDisposable;

                var channels = Convert.ToInt32(GetProperty(instance, "Channels"), CultureInfo.InvariantCulture);
                var sampleRate = Convert.ToInt32(GetProperty(instance, "SampleRate"), CultureInfo.InvariantCulture);
                var totalSamples = Convert.ToInt64(GetProperty(instance, "TotalSamples"), CultureInfo.InvariantCulture);

                var readMethod = type.GetMethod("ReadSamples", new[] { typeof(float[]), typeof(int), typeof(int) })
                                 ?? type.GetMethod("ReadSamples", new[] { typeof(float[]), typeof(int) });
                if (readMethod == null)
                    throw new AudioDecodeException("NVorbis 的 ReadSamples 签名与预期不符。");

                var buffer = new float[channels * 8192];
                var collected = new List<float>((int)Math.Min(totalSamples, int.MaxValue));
                while (true)
                {
                    int read;
                    if (readMethod.GetParameters().Length == 3)
                    {
                        var args = new object[] { buffer, 0, 8192 };
                        read = Convert.ToInt32(readMethod.Invoke(instance, args), CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        var args = new object[] { buffer, 8192 };
                        read = Convert.ToInt32(readMethod.Invoke(instance, args), CultureInfo.InvariantCulture);
                    }
                    if (read <= 0) break;
                    var count = read * channels;
                    for (var i = 0; i < count; i++) collected.Add(buffer[i]);
                }

                var mono = Downmix(collected, channels);
                return new AudioClip(mono, sampleRate, path).Normalized();
            }
            catch (AudioDecodeException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new AudioDecodeException("OGG 解码失败：" + exception.Message, exception);
            }
            finally
            {
                if (reader != null) reader.Dispose();
            }
        }

        /// <summary>用 Terraria 自带的 MP3Sharp 解 MP3。</summary>
        public static AudioClip DecodeMp3(string path)
        {
            var type = FindType("MP3Sharp.MP3Stream");
            if (type == null)
                throw new AudioDecodeException("找不到 MP3Sharp（MP3 解码器）。请把音频转成 WAV，或确认 Terraria 安装目录里有 MP3Sharp.dll。");

            try
            {
                using (var stream = (Stream)Activator.CreateInstance(type, new object[] { path }))
                {
                    var format = GetProperty(stream, "Format");
                    var channels = format != null
                        ? Convert.ToInt32(GetProperty(format, "Channels"), CultureInfo.InvariantCulture)
                        : 2;
                    var sampleRate = format != null
                        ? Convert.ToInt32(GetProperty(format, "SampleRate"), CultureInfo.InvariantCulture)
                        : 44100;
                    if (channels < 1 || channels > 2) channels = 2;

                    var buffer = new byte[channels * 4096];
                    var samples = new List<short>(1 << 20);
                    while (true)
                    {
                        var read = stream.Read(buffer, 0, buffer.Length);
                        if (read <= 0) break;
                        for (var i = 0; i + 1 < read; i += 2)
                            samples.Add((short)(buffer[i] | (buffer[i + 1] << 8)));
                    }
                    return AudioClip.FromInterleaved(samples.ToArray(), channels, sampleRate, path).Normalized();
                }
            }
            catch (AudioDecodeException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new AudioDecodeException("MP3 解码失败：" + exception.Message, exception);
            }
        }

        /// <summary>交错浮点采样 → 单声道（各声道等权平均）。</summary>
        private static float[] Downmix(List<float> interleaved, int channels)
        {
            if (channels < 1) channels = 1;
            var frames = interleaved.Count / channels;
            var mono = new float[frames];
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < channels; channel++)
                    sum += interleaved[frame * channels + channel];
                mono[frame] = sum / channels;
            }
            return mono;
        }

        private static object GetProperty(object instance, string name)
        {
            if (instance == null) return null;
            var property = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null) return property.GetValue(instance, null);
            var field = instance.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return field != null ? field.GetValue(instance) : null;
        }

        /// <summary>在已加载程序集与 Terraria 安装目录里找类型。</summary>
        public static Type FindType(string fullName)
        {
            var type = Type.GetType(fullName);
            if (type != null) return type;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetType(fullName);
                    if (type != null) return type;
                }
                catch (Exception)
                {
                    // 单个程序集读不到不影响继续找。
                }
            }

            var name = fullName.Substring(fullName.LastIndexOf('.') + 1);
            var simple = fullName.Substring(0, fullName.LastIndexOf('.'));
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (!string.Equals(assembly.GetName().Name, simple, StringComparison.OrdinalIgnoreCase)) continue;
                    type = assembly.GetType(fullName);
                    if (type != null) return type;
                }
                catch (Exception)
                {
                }
            }

            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[] { simple + ".dll", name + ".dll" };
            foreach (var candidate in candidates)
            {
                var path = Path.Combine(baseDirectory, candidate);
                if (!File.Exists(path)) continue;
                try
                {
                    type = Assembly.LoadFrom(path).GetType(fullName);
                    if (type != null) return type;
                }
                catch (Exception)
                {
                }
            }
            return null;
        }
    }
}
