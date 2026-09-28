using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using TingYu.Core;

namespace TingYu.Plugin
{
    /// <summary>
    /// 注入进 Terraria 的入口。注入器只在 Terraria.exe 里写四个调用点，全部落在这里：
    ///
    /// <list type="bullet">
    /// <item>`BeginFrame()` —— `Main.UpdateWorld_Players` 入口</item>
    /// <item>`EndFrame()` —— 同方法出口前</item>
    /// <item>`InstrumentTick()` —— `Player.ItemCheck_PlayInstruments` 入口</item>
    /// <item>`CaptureCursorInput(int)` —— `Player.Update` 里 `TriggersSet.CopyInto` 之后</item>
    /// </list>
    ///
    /// 静态状态而不是实例：注入点没有宿主对象可挂，而且整个进程只可能有一份。
    ///
    /// 三条稳健性规矩：
    /// 1. 任何钩子里的异常都不许冒泡进游戏——一次冒泡就可能让 Terraria 直接崩；
    ///    出错就释放接管并写日志。
    /// 2. 初始化是懒的：第一次钩子被调到时 Terraria 程序集与 XNA 一定已经加载完毕。
    /// 3. 状态与配置走文件（`TingYu\config.txt` 读、`TingYu\status.txt` 写），
    ///    Manager 与游戏是两个进程，这是最不挑环境的通道。
    /// </summary>
    public static class Runtime
    {
        private const string ConfigFileName = "config.txt";
        private const string StatusFileName = "status.txt";
        private const string LogFileName = "tingyu.log";
        private const int ConfigPollTicks = 30;
        private const int StatusFlushTicks = 20;

        private static bool _initialized;
        private static bool _initFailed;
        private static string _dataDirectory;
        private static GameFacade _game;
        private static ScoreLibrary _library;
        private static Diagnostics _log;
        private static Takeover _takeover;
        private static Hotkey _hotkey;
        private static TingYuConfig _config = new TingYuConfig();
        private static TingYuConfig _applied = new TingYuConfig();

        private static int _tick;
        private static int _lastGameUpdateCount = -1;
        private static DateTime _lastConfigWriteUtc = DateTime.MinValue;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static string _lastHotkeyTrack;
        private static bool _autoStartChecked;
        private static string _issue;

        /// <summary>「正在换手」状态已持续的 tick 数；0 表示没有待办。</summary>
        private static int _pendingStartTicks;

        /// <summary>换手最多等这么多 tick（约 1 秒）。超时通常意味着玩家正按着鼠标用别的东西。</summary>
        private const int PendingStartTimeoutTicks = 60;

        /// <summary>最近一次拒绝接管的理由，界面会显示它，让玩家知道差在哪。</summary>
        public static string LastNotice { get; private set; }

        public static bool Ready { get { return _initialized && !_initFailed; } }

        public static string DataDirectory { get { return _dataDirectory; } }

        // ------------------------------------------------------------ 注入点

        /// <summary>AF：一帧开始。</summary>
        public static void BeginFrame()
        {
            try
            {
                if (!EnsureInitialized()) return;
                _tick++;
                var gameUpdateCount = _game.GameUpdateCount;
                if (gameUpdateCount != _lastGameUpdateCount)
                {
                    _lastGameUpdateCount = gameUpdateCount;
                    PollHotkey();
                }
                if (_tick % ConfigPollTicks == 0) PollConfig();
                ServicePendingStart();
                _takeover.BeginFrame();
                if (_tick % StatusFlushTicks == 0) WriteStatus();
            }
            catch (Exception exception)
            {
                Fault("BeginFrame", exception);
            }
        }

        /// <summary>AB：一帧结束。</summary>
        public static void EndFrame()
        {
            try
            {
                if (!_initialized || _initFailed) return;
                _takeover.EndFrame();
            }
            catch (Exception exception)
            {
                Fault("EndFrame", exception);
            }
        }

        /// <summary>MN：`ItemCheck_PlayInstruments` 入口。返回 true 跳过原版。</summary>
        public static bool InstrumentTick()
        {
            try
            {
                if (!_initialized || _initFailed) return false;
                return _takeover.InstrumentTick();
            }
            catch (Exception exception)
            {
                Fault("InstrumentTick", exception);
                return false;
            }
        }

        /// <summary>CU：`Player.Update` 刚复制完输入。</summary>
        public static void CaptureCursorInput(int tick)
        {
            try
            {
                if (!_initialized || _initFailed) return;
                _takeover.CaptureCursorInput(tick);
            }
            catch (Exception exception)
            {
                Fault("CaptureCursorInput", exception);
            }
        }

        // ------------------------------------------------------------ 初始化

        private static bool EnsureInitialized()
        {
            if (_initialized) return !_initFailed;
            _initialized = true;

            // 这一行在所有初始化逻辑之前落盘。它的唯一职责是「证明钩子真的被调到了」：
            // 之前那次失败里一个字节的日志都没有，连插件有没有被加载都无法判断，
            // 只能靠反汇编去猜。有了这行就没有这种盲区了。
            Bootstrap("钩子被调用，开始初始化");

            try
            {
                Bootstrap("步骤 1/6 解析数据目录");
                _dataDirectory = ResolveDataDirectory();
                Directory.CreateDirectory(_dataDirectory);
                Bootstrap("步骤 2/6 读配置（" + _dataDirectory + "）");
                LoadConfig();
                Bootstrap("步骤 3/6 开日志，verbose=" + (_config.Verbose ? "1" : "0"));

                _log = new Diagnostics(Path.Combine(_dataDirectory, LogFileName), _config.Verbose);
                _log.Write("=== 听雨的声音 v" + typeof(Runtime).Assembly.GetName().Version +
                           " 注入成功，数据目录 " + _dataDirectory + " ===");

                Bootstrap("步骤 4/6 找 Terraria 程序集");
                var gameAssembly = FindTerrariaAssembly();
                if (gameAssembly == null)
                {
                    _initFailed = true;
                    _issue = "找不到 Terraria 程序集，注入层无法读取游戏状态。";
                    _log.Write(_issue);
                    return false;
                }

                Bootstrap("步骤 5/6 绑定反射（" + gameAssembly.GetName().Name + "）");
                _game = new GameFacade(gameAssembly);
                if (!_game.Available)
                {
                    _initFailed = true;
                    _issue = "Terraria 版本与预期不符（缺少关键字段），已停用接管。";
                    _log.Write(_issue);
                    return false;
                }

                // 反射绑定错一个成员，表现就只是「按了键没反应」，很难查。
                // 所以在初始化时一次性点全，把问题暴露在第一份日志里。
                var missing = _game.Validate();
                Bootstrap(missing == null ? "反射自检通过" : "反射自检失败：" + missing);
                if (missing != null)
                {
                    _initFailed = true;
                    _issue = missing;
                    _log.Write(_issue);
                    return false;
                }

                Bootstrap("步骤 6/6 载入曲库");
                _library = new ScoreLibrary();
                _library.Load(_config.MusicFolder);
                _takeover = new Takeover(_game, _library, _log);
                _hotkey = new Hotkey(_config.HotkeyVirtualKey);
                _applied = Clone(_config);
                _lastHotkeyTrack = _config.Track;
                _log.Write("曲库载入：" + _library.Tracks.Count + " 首 · 接管键 " + _hotkey.DisplayName +
                           " · 窗口缩放轴长 " + _game.SmallerScaledAxis.ToString("0.0", CultureInfo.InvariantCulture));
                WriteStatus();
                return true;
            }
            catch (Exception exception)
            {
                _initFailed = true;
                _issue = "初始化失败：" + exception.Message;
                // 这条**必须**走 Bootstrap：上一轮就是「初始化中断但只剩两行日志」，
                // 因为异常只写进了还没建起来的 Diagnostics。异常要写到不依赖任何状态的地方。
                Bootstrap("初始化失败：" + exception);
                if (_log != null) _log.Write(_issue + "\n" + exception);
                return false;
            }
        }

        private static string ResolveDataDirectory()
        {
            // 插件 DLL 必须与 Terraria.exe 同级：Terraria 启动时会用
            // `RuntimeHelpers.PrepareMethod` 预热自己程序集里的每个方法，注入点引用的
            // TingYu.Plugin 因此会在 CLR 的程序集探测阶段被解析，而探测只看 exe 同级目录
            // 与 GAC（子目录不在其列）。放在子目录里的那次尝试换来了一个
            // FileNotFoundException 与 `0xE0434352` 退出码。
            //
            // 数据目录则放在同级的 TingYu 子目录里：日志、配置、状态、曲库都在那里，
            // 一个目录就能看全，删掉也不影响游戏。
            var location = typeof(Runtime).Assembly.Location;
            var baseDirectory = string.IsNullOrEmpty(location)
                ? AppDomain.CurrentDomain.BaseDirectory
                : Path.GetDirectoryName(location);
            if (string.IsNullOrEmpty(baseDirectory)) baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(baseDirectory, "TingYu");
        }

        private static Assembly FindTerrariaAssembly()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.GetType("Terraria.Main", false) != null) return assembly;
                }
                catch (Exception)
                {
                }
            }
            // 兜底：Terraria.exe 就是宿主进程的程序集。
            try
            {
                var entry = Assembly.GetEntryAssembly();
                if (entry != null && entry.GetType("Terraria.Main", false) != null) return entry;
            }
            catch (Exception)
            {
            }
            return null;
        }

        private static void Fault(string hook, Exception exception)
        {
            try
            {
                Bootstrap(hook + " 钩子异常：" + exception.GetType().Name + " " + exception.Message);
                if (_log != null) _log.Write(hook + " 钩子异常，已释放接管：" + exception);
                if (_log != null) _log.Flush();
                if (_takeover != null && _takeover.Active)
                    _takeover.Release(ReleaseReason.Fault, exception.Message);
            }
            catch (Exception)
            {
                // 故障处理本身再出错就只能放弃了。
            }
        }

        /// <summary>
        /// 最后兜底的一条日志。写在 `TingYu\boot.log`。
        ///
        /// 三条设计约束，都是被现实逼出来的：
        /// 1. **不依赖 `Diagnostics`**——初始化早期它还不存在。
        /// 2. **编码要降级**：优先系统 ANSI（中文 Windows 就是 GBK，记事本能直接看），
        ///    万一拿不到再退回 UTF-8，两次都失败才算真写不出去。之前用 `Encoding.Default`
        ///    时如果它抛异常，异常会被下面这个 catch 吃掉，结果就是「什么都看不到」。
        /// 3. **每次失败都要能落盘**，所以目录不存在时现建。
        /// </summary>
        private static void Bootstrap(string message)
        {
            try
            {
                var directory = _dataDirectory;
                if (string.IsNullOrEmpty(directory))
                {
                    var location = typeof(Runtime).Assembly.Location;
                    var baseDirectory = string.IsNullOrEmpty(location)
                        ? AppDomain.CurrentDomain.BaseDirectory
                        : Path.GetDirectoryName(location);
                    directory = Path.Combine(baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory, "TingYu");
                }
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

                var line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  pid=" +
                           Process.GetCurrentProcess().Id + "  " + message + "\n";
                var path = Path.Combine(directory, "boot.log");

                Encoding encoding;
                try { encoding = Encoding.Default; }
                catch (Exception) { encoding = new UTF8Encoding(false); }

                try
                {
                    File.AppendAllText(path, line, encoding);
                }
                catch (EncoderFallbackException)
                {
                    File.AppendAllText(path, line, new UTF8Encoding(false));
                }
            }
            catch (Exception)
            {
                // 连这个都写不出去就没别的办法了。
            }
        }

        // ------------------------------------------------------------ 配置与热键

        private static string ConfigPath { get { return Path.Combine(_dataDirectory, ConfigFileName); } }

        private static string StatusPath { get { return Path.Combine(_dataDirectory, StatusFileName); } }

        private static void LoadConfig()
        {
            var path = Path.Combine(_dataDirectory, ConfigFileName);
            if (File.Exists(path))
            {
                _config = TingYuConfig.Parse(File.ReadAllLines(path), new TingYuConfig());
                _lastConfigWriteUtc = File.GetLastWriteTimeUtc(path);
            }
            else
            {
                _config = new TingYuConfig();
                _config.MusicFolder = MusicFolder.DefaultPath;
                _config.Save(path);
                _lastConfigWriteUtc = File.GetLastWriteTimeUtc(path);
            }

            // 环境变量只用于验证：`TINGYU_VERBOSE=1` 能把每 tick 的发音细节打进日志，
            // 不用去改界面上的开关。正式使用时以 config.txt 为准。
            var verbose = Environment.GetEnvironmentVariable("TINGYU_VERBOSE");
            if (!string.IsNullOrEmpty(verbose) && verbose != "0")
                _config.Verbose = true;
        }

        private static void PollConfig()
        {
            try
            {
                var path = ConfigPath;
                if (!File.Exists(path)) return;
                var writeTime = File.GetLastWriteTimeUtc(path);
                if (writeTime != _lastConfigWriteUtc)
                {
                    _lastConfigWriteUtc = writeTime;
                    var loaded = TingYuConfig.Parse(File.ReadAllLines(path), _config);
                    _config = loaded;

                    if (_hotkey == null || _hotkey.VirtualKey != _config.HotkeyVirtualKey)
                    {
                        _hotkey = new Hotkey(_config.HotkeyVirtualKey);
                        _log.Write("接管键改为 " + _hotkey.DisplayName);
                    }
                    if (!string.Equals(_config.MusicFolder, _applied.MusicFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        _library.Load(_config.MusicFolder);
                        _log.Write("曲库重新载入：" + _library.Tracks.Count + " 首");
                    }
                    if (!string.Equals(_config.Track, _applied.Track, StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Write("曲目切换为「" + (_library.Find(_config.Track) == null ? _config.Track : _library.Find(_config.Track).Title) + "」");
                        if (_takeover.Active) _takeover.ReleaseByRequest();
                    }
                    if (_config.Verbose != _applied.Verbose)
                    {
                        _log.Write("详细日志：" + (_config.Verbose ? "开" : "关"));
                    }
                    HandleRequest(_config.Request);
                    _applied = Clone(_config);
                    WriteStatus();
                }
                else
                {
                    HandleRequest(_config.Request);
                }
            }
            catch (Exception exception)
            {
                _log.Write("读取配置失败：" + exception.Message);
            }
        }

        private static void HandleRequest(string request)
        {
            if (string.IsNullOrEmpty(request)) return;
            switch (request.Trim().ToLowerInvariant())
            {
                case "play":
                    if (!_takeover.Active && _pendingStartTicks == 0)
                        BeginStart(_config, 0);
                    ClearRequest();
                    break;
                case "stop":
                    if (_takeover.Active) _takeover.ReleaseByRequest();
                    else LastNotice = null;
                    _pendingStartTicks = 0;
                    ClearRequest();
                    break;
                case "reload":
                    _library.Load(_config.MusicFolder);
                    _log.Write("界面请求重载曲库：" + _library.Tracks.Count + " 首");
                    ClearRequest();
                    break;
                case "idle":
                    break;
            }
        }

        private static void ClearRequest()
        {
            try
            {
                var lines = new List<string>();
                foreach (var line in File.ReadAllLines(ConfigPath))
                    lines.Add(line.StartsWith("request=", StringComparison.OrdinalIgnoreCase) ? "request=idle" : line);
                File.WriteAllLines(ConfigPath, lines, new UTF8Encoding(false));
                _config.Request = "idle";
                _applied.Request = "idle";
            }
            catch (Exception exception)
            {
                _log.Write("清除界面请求失败：" + exception.Message);
            }
        }

        private static void PollHotkey()
        {
            try
            {
                if (_takeover == null || _hotkey == null) return;

                // 自检模式：`TINGYU_AUTOPLAY=曲目ID[:秒数]` 让接管自动开始，
                // 全程不需要键盘输入，用于无人值守的回归验证。
                var autoPlay = Environment.GetEnvironmentVariable("TINGYU_AUTOPLAY");
                if (!_autoStartChecked && !string.IsNullOrEmpty(autoPlay))
                {
                    _autoStartChecked = true;
                    _config.Track = autoPlay.Split(':')[0];
                    _log.Write("自检模式：TINGYU_AUTOPLAY=" + autoPlay);
                    BeginStart(_config, 0);
                    return;
                }

                if (!_hotkey.PollPressed()) return;

                if (_takeover.Active)
                {
                    _takeover.Release(ReleaseReason.HotkeyPressed, null);
                    LastNotice = null;
                    _pendingStartTicks = 0;
                    return;
                }

                BeginStart(_config, 0);
            }
            catch (Exception exception)
            {
                Fault("PollHotkey", exception);
            }
        }

        /// <summary>
        /// 开始接管（或开始「换手 → 接管」这个两段过程）。
        ///
        /// 之所以要分两段：把手上的物品换成背包里的乐器是**缓冲**动作，
        /// 原版要到下一次 `Player.Update` 的 `selectedItemState.Update()` 才生效。
        /// 当帧就要求「手上已经是乐器」必然失败，所以这里记下待办、逐 tick 重试。
        /// 超过 <see cref="PendingStartTimeoutTicks"/> 还没换成就放弃并说明原因。
        /// </summary>
        private static void BeginStart(TingYuConfig config, int attempt)
        {
            var result = _takeover.TryStart(config);
            if (result == null)
            {
                LastNotice = null;
                _pendingStartTicks = 0;
                return;
            }
            if (result == Takeover.SwitchPending)
            {
                _pendingStartTicks = attempt + 1;
                LastNotice = "正在把手上的物品换成乐器…";
                return;
            }
            _pendingStartTicks = 0;
            LastNotice = result;
            _log.Write("接管请求被拒绝：" + result);
        }

        /// <summary>待办的重试：每 tick 检查一次。</summary>
        private static void ServicePendingStart()
        {
            if (_pendingStartTicks <= 0) return;
            if (_takeover.Active) { _pendingStartTicks = 0; return; }

            if (_pendingStartTicks > PendingStartTimeoutTicks)
            {
                _pendingStartTicks = 0;
                LastNotice = "换乐器超时：手上正在使用别的物品，先松开鼠标再按一次接管键。";
                _log.Write("换手超时，放弃接管。");
                return;
            }
            BeginStart(_config, _pendingStartTicks);
        }

        // ------------------------------------------------------------ 状态上报

        private static void WriteStatus()
        {
            if (_log == null) return;
            try
            {
                var builder = new StringBuilder();
                builder.AppendLine("version=" + typeof(Runtime).Assembly.GetName().Version);
                builder.AppendLine("ready=" + (Ready ? "1" : "0"));
                builder.AppendLine("issue=" + (_issue ?? string.Empty));
                builder.AppendLine("tick=" + _tick.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine("active=" + (_takeover != null && _takeover.Active ? "1" : "0"));
                builder.AppendLine("track=" + (_takeover != null && _takeover.CurrentTrack != null ? _takeover.CurrentTrack.Title : string.Empty));
                builder.AppendLine("instrument=" + (_takeover != null && _takeover.CurrentInstrument != null ? _takeover.CurrentInstrument.NameZh : string.Empty));
                builder.AppendLine("progress=" + (_takeover == null ? "0" : _takeover.Progress.ToString("0.####", CultureInfo.InvariantCulture)));
                builder.AppendLine("strikes=" + (_takeover == null ? 0 : _takeover.StruckTicks));
                builder.AppendLine("confirmed=" + (_takeover == null ? 0 : _takeover.ConfirmedTicks));
                builder.AppendLine("step=" + (_takeover == null ? 0 : _takeover.LastStep));
                builder.AppendLine("pitch=" + (_takeover == null ? "0" : _takeover.LastPitch.ToString("0.####", CultureInfo.InvariantCulture)));
                builder.AppendLine("elapsed=" + (_takeover == null ? 0 : _takeover.ElapsedTicks));
                builder.AppendLine("total=" + (_takeover == null ? 0 : _takeover.TotalTicks));
                builder.AppendLine("release=" + (_takeover == null ? string.Empty : Takeover.Describe(_takeover.LastRelease)));
                builder.AppendLine("releaseDetail=" + (_takeover == null ? string.Empty : (_takeover.LastReleaseDetail ?? string.Empty)));
                builder.AppendLine("notice=" + (LastNotice ?? string.Empty));
                builder.AppendLine("heldItem=" + HeldItemName());
                builder.AppendLine("axis=" + (_game == null ? "0" : _game.SmallerScaledAxis.ToString("0.0", CultureInfo.InvariantCulture)));
                builder.AppendLine("tracks=" + (_library == null ? 0 : _library.Tracks.Count));
                builder.AppendLine("hotkey=" + (_hotkey == null ? "F8" : _hotkey.DisplayName));
                builder.AppendLine("uptime=" + ((int)(Clock.ElapsedMilliseconds / 1000)).ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(StatusPath, builder.ToString(), new UTF8Encoding(false));

                // 状态文件每 tick 都写也扛得住（几十字节），但没必要；这里只在内容变化时冲日志。
                _log.Flush();
            }
            catch (Exception)
            {
                // 状态写不出去不影响游戏运行。
            }
        }

        private static string HeldItemName()
        {
            try
            {
                var player = _game == null ? null : _game.LocalPlayer;
                if (player == null) return string.Empty;
                var type = _game.SelectedItemType(player);
                var instrument = InstrumentModel.Find(type);
                if (instrument != null) return instrument.NameZh;
                return type == 0 ? "空手" : "物品#" + type;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static TingYuConfig Clone(TingYuConfig source)
        {
            return TingYuConfig.Parse(new string[0], new TingYuConfig
            {
                Enabled = source.Enabled,
                Track = source.Track,
                Transpose = source.Transpose,
                JitterTicks = source.JitterTicks,
                ReleaseOnWorldEvent = source.ReleaseOnWorldEvent,
                ReleaseOnMouseMove = source.ReleaseOnMouseMove,
                HotkeyVirtualKey = source.HotkeyVirtualKey,
                Verbose = source.Verbose,
                Request = source.Request,
                MusicFolder = source.MusicFolder
            });
        }
    }
}
