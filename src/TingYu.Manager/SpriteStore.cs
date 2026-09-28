using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using Color = Microsoft.Xna.Framework.Color;

namespace TingYu.Manager
{
    /// <summary>
    /// 从**玩家自己的** Terraria 安装里读出乐器贴图。
    ///
    /// 为什么不把贴图打进仓库：那是 Re-Logic 的美术资源。
    /// 为什么不自己画：需求明确要求用游戏原生素材，自创素材就是违约。
    /// 所以走第三条路——在玩家的机器上、按玩家的版本、从玩家的 Content 目录解码。
    ///
    /// Terraria 的 XNB 是 LZX 压缩的（头标志 0x80），自己实现解压既费事又容易出错。
    /// 而 XNA 自带的 `ContentManager` 就是干这个的，直接用它。
    ///
    /// 代价是真实的，也要说清楚：XNA 只有 32 位版本，所以管理器必须是 x86 进程；
    /// 解码需要一个图形设备，因此无显示的机器上无法首次提取。
    /// </summary>
    internal static class SpriteStore
    {
        /// <summary>缓存目录名，放在游戏目录下的 TingYu\ 数据目录里。</summary>
        internal const string FolderName = "sprites";
        private const string ManifestName = "manifest.txt";

        /// <summary>清单版本。贴图清单或转换方式改了就要 +1，这样旧缓存会被重建而不是被信任。</summary>
        private const int FormatVersion = 1;

        internal static string Directory(string terrariaExePath)
        {
            var data = DataDirectory(terrariaExePath);
            return data == null ? null : Path.Combine(data, FolderName);
        }

        private static string DataDirectory(string terrariaExePath)
        {
            if (string.IsNullOrEmpty(terrariaExePath)) return null;
            var gameDirectory = Path.GetDirectoryName(terrariaExePath);
            return string.IsNullOrEmpty(gameDirectory) ? null : Path.Combine(gameDirectory, "TingYu");
        }

        private static string ContentDirectory(string terrariaExePath)
        {
            var gameDirectory = Path.GetDirectoryName(terrariaExePath);
            return string.IsNullOrEmpty(gameDirectory) ? null : Path.Combine(gameDirectory, "Content");
        }

        /// <summary>缓存是否完整且是当前版本。</summary>
        internal static bool IsReady(string terrariaExePath)
        {
            var directory = Directory(terrariaExePath);
            if (directory == null) return false;
            string reason;
            return IsComplete(directory, out reason);
        }

        /// <summary>缓存缺失或不完整时的中文原因；完整则返回 null。</summary>
        internal static string Describe(string terrariaExePath)
        {
            var directory = Directory(terrariaExePath);
            if (directory == null) return "尚未选择 Terraria.exe";
            string reason;
            return IsComplete(directory, out reason) ? null : reason;
        }

        /// <summary>
        /// 提取缺失的贴图。可重复调用；游戏没装时返回失败而不是抛异常——
        /// 一个画不出图标的界面仍然应该能完成安装。
        /// </summary>
        internal static bool TryEnsure(string terrariaExePath, out string error)
        {
            error = null;
            var directory = Directory(terrariaExePath);
            var content = ContentDirectory(terrariaExePath);
            if (directory == null || content == null)
            {
                error = "尚未选择 Terraria.exe";
                return false;
            }
            if (!System.IO.Directory.Exists(content))
            {
                error = "没有找到游戏的 Content 目录";
                return false;
            }
            if (IsReady(terrariaExePath)) return true;
            return TryExtractTo(content, directory, out error);
        }

        /// <summary>
        /// 从 <paramref name="contentDirectory"/> 提取到 <paramref name="outputDirectory"/>。
        ///
        /// 与 <see cref="TryEnsure"/> 拆开，是为了让自检能对真实游戏内容跑真实的解码路径，
        /// 同时不往玩家的安装目录里写任何东西。
        /// </summary>
        internal static bool TryExtractTo(string contentDirectory, string outputDirectory, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(contentDirectory) || !System.IO.Directory.Exists(contentDirectory))
            {
                error = "没有找到游戏的 Content 目录";
                return false;
            }

            // 图形设备要窗口句柄，XNA 还要求 STA 线程。所以给它一条自己的线程，
            // 而不是借用界面线程——否则提取期间窗口会整个卡住。
            string failure = null;
            var thread = new Thread(delegate()
            {
                try
                {
                    Extract(contentDirectory, outputDirectory);
                }
                catch (Exception exception)
                {
                    failure = Describe(exception);
                }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null)
            {
                error = failure;
                return false;
            }
            return true;
        }

        private static string Describe(Exception exception)
        {
            // 有信息量的永远是内层异常：XNA 会把「没有图形设备」「资源不存在」
            // 这些真实原因包在几层外壳里。
            var inner = exception;
            while (inner.InnerException != null) inner = inner.InnerException;
            return inner.Message;
        }

        private static void Extract(string contentDirectory, string directory)
        {
            // 先写临时目录，全部成功再一次性发布。
            // 半个缓存比没有缓存更糟：那会永远显示一部分图标、另一部分永远空白。
            var staging = directory + ".tmp-" + Guid.NewGuid().ToString("N");
            System.IO.Directory.CreateDirectory(staging);
            try
            {
                using (var host = new ExtractionHost())
                using (var content = new ContentManager(host, contentDirectory))
                {
                    foreach (var definition in InstrumentSprites.All)
                    {
                        var texture = content.Load<Texture2D>(definition.AssetPath);
                        try
                        {
                            WritePng(texture, Path.Combine(staging, definition.Key + ".png"));
                        }
                        finally
                        {
                            texture.Dispose();
                        }
                    }
                }

                System.IO.Directory.CreateDirectory(directory);
                foreach (var file in System.IO.Directory.GetFiles(staging))
                    File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), true);
                File.WriteAllText(Path.Combine(directory, ManifestName), BuildManifest(), new UTF8Encoding(false));
            }
            finally
            {
                try { System.IO.Directory.Delete(staging, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static string BuildManifest()
        {
            var text = new StringBuilder();
            text.Append("tingyu-sprites ").Append(FormatVersion).Append('\n');
            foreach (var key in InstrumentSprites.Keys()) text.Append(key).Append('\n');
            return text.ToString();
        }

        private static bool IsComplete(string directory, out string reason)
        {
            reason = null;
            if (!System.IO.Directory.Exists(directory))
            {
                reason = "尚未提取游戏贴图";
                return false;
            }
            var manifest = Path.Combine(directory, ManifestName);
            if (!File.Exists(manifest))
            {
                reason = "贴图缓存缺少清单";
                return false;
            }
            string actual;
            try
            {
                actual = File.ReadAllText(manifest, Encoding.UTF8);
            }
            catch (IOException)
            {
                reason = "贴图清单不可读";
                return false;
            }
            if (!string.Equals(actual, BuildManifest(), StringComparison.Ordinal))
            {
                reason = "贴图缓存来自旧版本";
                return false;
            }
            foreach (var key in InstrumentSprites.Keys())
            {
                if (!File.Exists(Path.Combine(directory, key + ".png")))
                {
                    reason = "贴图缓存缺少 " + key;
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 把一个贴图写成直通 alpha 的 PNG。
        ///
        /// 两个地方是刻意的：XNA 的内容管线存的是**预乘 alpha**，而 PNG 存直通 alpha，
        /// 不做反预乘的话每一条软边都会发暗；物品贴图是 16x16 的小图，
        /// 按目标尺寸放大时用最近邻，免得糊成一团。
        /// </summary>
        private static void WritePng(Texture2D texture, string path)
        {
            var width = texture.Width;
            var height = texture.Height;
            var pixels = new Color[width * height];
            texture.GetData(pixels);

            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new byte[width * 4];
                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var source = pixels[y * width + x];
                            var alpha = source.A;
                            byte red = source.R, green = source.G, blue = source.B;
                            if (alpha != 0 && alpha != 255)
                            {
                                red = Unpremultiply(source.R, alpha);
                                green = Unpremultiply(source.G, alpha);
                                blue = Unpremultiply(source.B, alpha);
                            }
                            // Format32bppArgb 在内存里是 BGRA 顺序。
                            row[x * 4] = blue;
                            row[x * 4 + 1] = green;
                            row[x * 4 + 2] = red;
                            row[x * 4 + 3] = alpha;
                        }
                        Marshal.Copy(row, 0, new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row.Length);
                    }
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }
                bitmap.Save(path, ImageFormat.Png);
            }
        }

        private static byte Unpremultiply(byte value, byte alpha)
        {
            var scaled = (value * 255 + alpha / 2) / alpha;
            return scaled > 255 ? (byte)255 : (byte)scaled;
        }

        /// <summary>
        /// 读一张已缓存的贴图；没提取过则返回 null。
        /// 调用方应画占位而不是失败——首次提取之前界面也必须能用。
        /// </summary>
        internal static Image Load(string terrariaExePath, string key)
        {
            var directory = Directory(terrariaExePath);
            if (directory == null) return null;
            var path = Path.Combine(directory, key + ".png");
            if (!File.Exists(path)) return null;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var loaded = Image.FromStream(stream, true, true);
                    // 从流里拷出来：文件句柄不能活过这次调用，而 Image 是惰性的会一直持有它。
                    var copy = new Bitmap(loaded.Width, loaded.Height, PixelFormat.Format32bppArgb);
                    using (var graphics = Graphics.FromImage(copy))
                        graphics.DrawImageUnscaled(loaded, 0, 0);
                    loaded.Dispose();
                    return copy;
                }
            }
            catch (Exception)
            {
                // 缓存坏了就是少一个图标，不是崩溃。
                return null;
            }
        }

        /// <summary>
        /// 给 `ContentManager` 提供图形设备。它存在的唯一目的就是解码贴图并把像素读出来，
        /// 全程不画任何东西。
        /// </summary>
        private sealed class ExtractionHost : IServiceProvider, IGraphicsDeviceService, IDisposable
        {
            private readonly System.Windows.Forms.Form _window;
            private readonly GraphicsDevice _device;

            internal ExtractionHost()
            {
                _window = new System.Windows.Forms.Form();
                _window.ClientSize = new Size(8, 8);
                // 读一次句柄会强制创建窗口，图形设备需要它，尽管窗口从不显示。
                var handle = _window.Handle;
                var parameters = new PresentationParameters
                {
                    BackBufferWidth = 8,
                    BackBufferHeight = 8,
                    DeviceWindowHandle = handle,
                    IsFullScreen = false,
                    PresentationInterval = PresentInterval.Immediate,
                    RenderTargetUsage = RenderTargetUsage.PreserveContents
                };
                // Terraria 的内容是按 HiDef 档编译的，Reach 档的设备读不了。
                _device = new GraphicsDevice(GraphicsAdapter.DefaultAdapter, GraphicsProfile.HiDef, parameters);
            }

            public object GetService(Type serviceType)
            {
                return serviceType == typeof(IGraphicsDeviceService) ? (object)this : null;
            }

            public GraphicsDevice GraphicsDevice { get { return _device; } }

            public event EventHandler<EventArgs> DeviceCreated;
            public event EventHandler<EventArgs> DeviceDisposing;
            public event EventHandler<EventArgs> DeviceReset;
            public event EventHandler<EventArgs> DeviceResetting;

            public void Dispose()
            {
                if (_device != null) _device.Dispose();
                if (_window != null) _window.Dispose();
            }
        }
    }

    /// <summary>一件乐器对应的一张游戏贴图。</summary>
    internal sealed class SpriteDefinition
    {
        internal string Key;
        internal string AssetPath;
    }

    /// <summary>
    /// 要提取的贴图清单。AssetPath 是 Content 目录下的相对路径（不带 .xnb）。
    /// 这里只列**游戏本体自带的**乐器图标，不包含任何自制素材。
    /// </summary>
    internal static class InstrumentSprites
    {
        internal static readonly SpriteDefinition[] All =
        {
            new SpriteDefinition { Key = "harp", AssetPath = "Images/Item_508" },
            new SpriteDefinition { Key = "bell", AssetPath = "Images/Item_507" },
            new SpriteDefinition { Key = "axe", AssetPath = "Images/Item_1305" },
            new SpriteDefinition { Key = "ivy", AssetPath = "Images/Item_4372" },
            new SpriteDefinition { Key = "rainsong", AssetPath = "Images/Item_4057" },
            new SpriteDefinition { Key = "stellar", AssetPath = "Images/Item_4715" }
        };

        internal static string[] Keys()
        {
            var keys = new string[All.Length];
            for (var i = 0; i < All.Length; i++) keys[i] = All[i].Key;
            return keys;
        }

        /// <summary>乐器物品 ID → 贴图键。</summary>
        internal static string KeyForItem(int itemId)
        {
            switch (itemId)
            {
                case 508: return "harp";
                case 507: return "bell";
                case 1305: return "axe";
                case 4372: return "ivy";
                case 4057: return "rainsong";
                case 4715: return "stellar";
                default: return null;
            }
        }
    }
}
