using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using TingYu.Core;
using TingYu.Patcher;

namespace TingYu.Manager
{
    /// <summary>
    /// 管理器主窗口。
    ///
    /// 它是和控制台（注入层）配对的另一半，两者只通过文件握手：
    /// 管理器写 `TingYu\config.txt`（含 `request=play|stop|reload`），
    /// 插件每 30 tick 读一次、每 20 tick 写一次 `status.txt`。
    /// 这样界面不需要在游戏进程里，游戏也不依赖界面是否开着。
    ///
    /// 界面全部自绘而非用系统控件：需求要求「纯色边框」，而 WinForms 默认控件
    /// 自带渐变、圆角与系统主题，压不住。自绘的代价是布局要自己算，
    /// 换来的是整套视觉完全一致。
    /// </summary>
    internal sealed class MainForm : Form
    {
        private readonly InstallationService _service = new InstallationService();
        private readonly List<TrackRow> _tracks = new List<TrackRow>();

        private string _terrariaExe;
        private InstallStatus _install;
        private string _selectedTrack = "builtin:jasmine";
        /// <summary>
        /// 界面上选中的乐器下标；-1 表示**未指定**。
        ///
        /// 必须能表示「未指定」：默认值 0 会把「玩家没点过」和「玩家点了第一件」
        /// 混成同一个意思，接管时就会莫名其妙地覆盖玩家手上拿的乐器。
        /// </summary>
        private int _selectedInstrument = -1;
        private TingYuConfigView _settings;
        private PluginStatus _pluginStatus;

        private readonly Dictionary<string, Image> _sprites = new Dictionary<string, Image>(StringComparer.Ordinal);
        private string _spriteSource;

        private readonly Timer _pollTimer;
        private readonly Stopwatch _since = Stopwatch.StartNew();
        private string _toast;
        private DateTime _toastUntil = DateTime.MinValue;

        internal MainForm()
        {
            Text = "听雨的声音";
            ClientSize = new Size(980, 640);
            MinimumSize = new Size(880, 560);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = UiTheme.Canvas;
            ForeColor = UiTheme.TextColor;
            Font = UiTheme.Body;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            KeyPreview = true;

            _settings = TingYuConfigView.Load(null);
            _terrariaExe = TerrariaLocator.FindTerrariaExe();
            RefreshInstall();

            _pollTimer = new Timer { Interval = 1000 };
            _pollTimer.Tick += delegate { Poll(); };
            _pollTimer.Start();

            ReloadTracks();
            MouseDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseLeave += delegate { _hover = HitTest(PointToClient(MousePosition)); Invalidate(); };
        }

        // ---------------------------------------------------------------- 状态

        private void RefreshInstall()
        {
            try
            {
                _install = _service.GetStatus(_terrariaExe);
            }
            catch (Exception exception)
            {
                _install = new InstallStatus
                {
                    State = InstallState.Invalid,
                    TerrariaExe = _terrariaExe,
                    Message = exception.Message
                };
            }
            _settings = TingYuConfigView.Load(DataDirectory());
            SyncSelectionFromSettings();
            EnsureSprites();
        }

        private string DataDirectory()
        {
            if (string.IsNullOrEmpty(_terrariaExe)) return null;
            var directory = Path.GetDirectoryName(_terrariaExe);
            return string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, InstallationService.DataFolderName);
        }

        private void Poll()
        {
            _pluginStatus = PluginStatus.Load(DataDirectory());
            Invalidate();
        }

        private void EnsureSprites()
        {
            var data = DataDirectory();
            if (data == null || _spriteSource == data) return;
            _spriteSource = data;
            _sprites.Clear();
            foreach (var key in InstrumentSprites.Keys())
            {
                var image = SpriteStore.Load(_terrariaExe, key);
                if (image != null) _sprites[key] = image;
            }
        }

        private void ReloadTracks()
        {
            _tracks.Clear();
            // 内置曲目要同时用到 id 与标题：id 写进配置，标题给人看。
            // "builtin:" 前缀是插件认的格式。
            foreach (var song in BuiltInSongs.List())
                _tracks.Add(new TrackRow { Id = "builtin:" + song.Id, Title = song.Title, Kind = "内置" });

            var folder = _settings.MusicFolder;
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                foreach (var entry in MusicFolder.List(folder))
                    _tracks.Add(new TrackRow { Id = "file:" + entry.Path, Title = entry.DisplayName, Kind = "本地" });
            }

            if (_tracks.Count > 0 && FindTrack(_selectedTrack) == null)
                _selectedTrack = _tracks[0].Id;
        }

        private TrackRow FindTrack(string id)
        {
            foreach (var row in _tracks)
                if (string.Equals(row.Id, id, StringComparison.OrdinalIgnoreCase)) return row;
            return null;
        }

        private void Toast(string message)
        {
            _toast = message;
            _toastUntil = DateTime.UtcNow.AddSeconds(4);
        }

        // ---------------------------------------------------------------- 布局

        internal Rectangle TopBar { get { return new Rectangle(0, 0, ClientSize.Width, 48); } }

        /// <summary>
        /// 内容区。左右下三边各留 `Margin` 的窗口底边，而不是让面板顶到窗口边缘。
        ///
        /// 留白不是为了「好看一点」：面板边框与窗口边框贴在一起时，两条线会连成一条，
        /// 分不出哪是窗口、哪是面板。留出底色之后每块面板才是独立的一块。
        /// </summary>
        internal Rectangle Body
        {
            get
            {
                return new Rectangle(UiTheme.Margin, 48,
                    ClientSize.Width - UiTheme.Margin * 2,
                    ClientSize.Height - 48 - 36 - UiTheme.Margin);
            }
        }

        internal Rectangle LeftPanel
        {
            get { return new Rectangle(Body.X, Body.Top, 248, Body.Height); }
        }

        internal Rectangle RightPanel
        {
            get { return new Rectangle(Body.Right - 260, Body.Top, 260, Body.Height); }
        }

        internal Rectangle CenterPanel
        {
            get
            {
                // 面板之间留出画布色的缝，而不是紧挨着：
                // 两块同色面板直接相邻时，两条 1px 边框会挨在一起，视觉上是一根粗线，
                // 反而看不出分块。留缝之后每个面板都是独立的一块。
                var left = LeftPanel.Right + UiTheme.Gap;
                return new Rectangle(left, Body.Top, RightPanel.Left - UiTheme.Gap - left, Body.Height);
            }
        }

        internal Rectangle BottomBar
        {
            get
            {
                return new Rectangle(UiTheme.Margin, ClientSize.Height - 30,
                    ClientSize.Width - UiTheme.Margin * 2, 22);
            }
        }

        // ---------------------------------------------------------------- 绘制

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Render(g);
        }

        /// <summary>
        /// 把整个界面画到给定的 `Graphics` 上。
        ///
        /// 特意与 `OnPaint` 分开：自检需要把界面画到一张**自己创建的位图**上，
        /// 而 `Control.DrawToBitmap` 会走窗口 DC，在离屏渲染下并不可靠
        /// （实测会丢失部分文字，且丢失的标签随窗口尺寸变化）。
        /// 直接给出这个方法，自检就能用 `Graphics.FromImage` 拿到干净、确定的绘图环境。
        /// </summary>
        internal void Render(System.Drawing.Graphics g)
        {
            DrawTopBar(g);
            DrawInstruments(g);
            DrawTracks(g);
            DrawSettings(g);
            DrawBottomBar(g);
            DrawToast(g);
        }

        private void DrawTopBar(Graphics g)
        {
            var bounds = TopBar;
            UiTheme.Frame(g, bounds, UiTheme.Surface, UiTheme.Border);

            UiTheme.DrawText(g, "听雨的声音", UiTheme.Title, UiTheme.TextColor,
                new Rectangle(UiTheme.Padding, bounds.Top, 160, bounds.Height), ContentAlignment.MiddleLeft);

            // 状态点 + 一句话状态。状态文字属于信息而非说明，是允许保留的。
            //
            // 三种颜色的含义要分得清：已安装是绿、文件被改动是红（这意味着注入过的东西
            // 和当前文件对不上，必须让玩家看见）、其余是灰。
            var stateText = DescribeState();
            var stateColor = UiTheme.TextMuted;
            if (_install != null)
            {
                if (_install.State == InstallState.Installed) stateColor = UiTheme.Active;
                else if (_install.State == InstallState.InstalledButChanged ||
                         _install.State == InstallState.CleanUnsupported ||
                         _install.State == InstallState.Invalid) stateColor = UiTheme.Danger;
            }

            var dot = new Rectangle(176, bounds.Top + bounds.Height / 2 - 4, 8, 8);
            using (var brush = new SolidBrush(stateColor)) g.FillRectangle(brush, dot);
            UiTheme.DrawText(g, stateText, UiTheme.Body, UiTheme.TextMuted,
                new Rectangle(dot.Right + 8, bounds.Top, 420, bounds.Height), ContentAlignment.MiddleLeft);

            // 右侧：提取贴图 / 安装 / 还原，三个平铺按钮。
            foreach (var button in ActionButtons())
            {
                var hover = _hover == button.Key;
                var primary = button.Key == "install";
                var fill = primary ? UiTheme.Accent : UiTheme.Surface;
                var text = primary ? Color.White : UiTheme.TextColor;
                if (!button.Enabled)
                {
                    fill = UiTheme.Subtle;
                    text = UiTheme.TextDisabled;
                }
                else if (hover && !primary)
                {
                    fill = UiTheme.AccentSoft;
                }
                UiTheme.Frame(g, button.Bounds, fill, primary ? UiTheme.Accent : UiTheme.Border);
                UiTheme.DrawText(g, button.Label, UiTheme.Body, text, button.Bounds, ContentAlignment.MiddleCenter);
            }
        }

        private string DescribeState()
        {
            if (_install == null) return "未检测";
            switch (_install.State)
            {
                case InstallState.NotFound: return "未找到游戏";
                case InstallState.CleanSupported: return "未安装";
                case InstallState.CleanUnsupported: return "版本不符";
                case InstallState.Installed: return "已安装 " + _install.GameVersion;
                case InstallState.InstalledButChanged: return "已安装 · 文件被改动";
                default: return string.IsNullOrEmpty(_install.Message) ? "未知" : _install.Message;
            }
        }

        // ------------------------------------------------- 绘制区域（绘制与自检共用）
        //
        // 这些方法存在的唯一理由是**坐标只能有一份**。之前自检里的期望区域是另抄一遍
        // 坐标并加上估算偏移，结果界面一改，自检就报出一堆并不存在的「缺失」，
        // 反而把真正的缺失淹没了。
        // 现在绘制与自检都走这里，坐标不可能漂移。

        internal Rectangle TitleBounds(string caption, Rectangle panel)
        {
            return new Rectangle(panel.X + UiTheme.Padding, panel.Y + 6, panel.Width - UiTheme.Padding * 2, 22);
        }

        /// <summary>乐器卡片里图标下方那行名字的区域。</summary>
        internal static Rectangle InstrumentNameBounds(Rectangle card, Rectangle icon)
        {
            return new Rectangle(card.X, icon.Bottom + 4, card.Width, 18);
        }

        /// <summary>乐器卡片里图标的区域。</summary>
        internal static Rectangle InstrumentIconBounds(Rectangle card)
        {
            return new Rectangle(card.X + card.Width / 2 - 20, card.Y + 10, 40, 40);
        }

        /// <summary>曲目行左侧标题的区域。</summary>
        internal static Rectangle TrackTitleBounds(Rectangle row)
        {
            return new Rectangle(row.X + 10, row.Y, row.Width - 80, row.Height);
        }

        /// <summary>曲目行右侧类型标记的区域。</summary>
        internal static Rectangle TrackKindBounds(Rectangle row)
        {
            return new Rectangle(row.Right - 64, row.Y, 54, row.Height);
        }

        /// <summary>右栏「开始演奏」「停止」两个按钮的区域。</summary>
        internal Rectangle PlayButtonBounds()
        {
            return new Rectangle(RightPanel.X + UiTheme.Padding, RightPanel.Bottom - 92,
                RightPanel.Width - UiTheme.Padding * 2, 32);
        }

        internal Rectangle StopButtonBounds()
        {
            var play = PlayButtonBounds();
            return new Rectangle(play.X, play.Bottom + 8, play.Width, 32);
        }

        private void DrawInstruments(Graphics g)
        {
            var panel = LeftPanel;
            UiTheme.Frame(g, panel, UiTheme.Surface, UiTheme.Border);

            UiTheme.DrawText(g, "乐器", UiTheme.BodyBold, UiTheme.TextColor,
                TitleBounds("乐器", panel), ContentAlignment.MiddleLeft);

            var cards = InstrumentRects();
            var all = InstrumentModel.All;
            for (var i = 0; i < cards.Count && i < all.Count; i++)
            {
                var model = all[i];
                var card = cards[i];
                var selected = i == _selectedInstrument;
                var hover = _hover == "instrument:" + i;
                var fill = selected ? UiTheme.AccentSoft : (hover ? UiTheme.Subtle : UiTheme.Surface);
                UiTheme.FrameSelected(g, card, selected, fill);

                var key = InstrumentSprites.KeyForItem(model.ItemId);
                Image sprite;
                var icon = InstrumentIconBounds(card);
                if (key != null && _sprites.TryGetValue(key, out sprite))
                {
                    UiTheme.DrawSprite(g, sprite, icon);
                }
                else
                {
                    // 没有贴图时画一个纯色空框，而不是用自制图标冒充游戏素材。
                    UiTheme.Frame(g, icon, UiTheme.Subtle, UiTheme.Border);
                }

                UiTheme.DrawText(g, model.NameZh, UiTheme.Body, selected ? UiTheme.Accent : UiTheme.TextColor,
                    InstrumentNameBounds(card, icon), ContentAlignment.MiddleCenter);
            }
        }

        private void DrawTracks(Graphics g)
        {
            var panel = CenterPanel;
            UiTheme.Frame(g, panel, UiTheme.Surface, UiTheme.Border);
            UiTheme.DrawText(g, "曲目", UiTheme.BodyBold, UiTheme.TextColor,
                TitleBounds("曲目", panel), ContentAlignment.MiddleLeft);
            UiTheme.DrawText(g, _tracks.Count.ToString(CultureInfo.InvariantCulture), UiTheme.Small, UiTheme.TextMuted,
                new Rectangle(panel.Right - 60 - UiTheme.Padding, panel.Y + 6, 60, 22), ContentAlignment.MiddleRight);

            var listTop = panel.Y + 34;
            UiTheme.Separator(g, panel.X, listTop, panel.Width, true);

            var rows = TrackRects();
            for (var i = 0; i < rows.Count && i < _tracks.Count; i++)
            {
                var row = _tracks[i];
                var bounds = rows[i];
                var selected = string.Equals(row.Id, _selectedTrack, StringComparison.OrdinalIgnoreCase);
                var hover = _hover == "track:" + i;
                var fill = selected ? UiTheme.AccentSoft : (hover ? UiTheme.Subtle : UiTheme.Surface);
                UiTheme.FrameSelected(g, bounds, selected, fill);

                UiTheme.DrawText(g, row.Title, UiTheme.Body, selected ? UiTheme.Accent : UiTheme.TextColor,
                    TrackTitleBounds(bounds), ContentAlignment.MiddleLeft);
                UiTheme.DrawText(g, row.Kind, UiTheme.Small, UiTheme.TextMuted,
                    TrackKindBounds(bounds), ContentAlignment.MiddleRight);
            }

            if (rows.Count < _tracks.Count)
            {
                UiTheme.DrawText(g, "+" + (_tracks.Count - rows.Count).ToString(CultureInfo.InvariantCulture),
                    UiTheme.Small, UiTheme.TextMuted,
                    new Rectangle(panel.X, panel.Bottom - 22, panel.Width - UiTheme.Padding, 18),
                    ContentAlignment.MiddleRight);
            }
        }

        private void DrawSettings(Graphics g)
        {
            var panel = RightPanel;
            UiTheme.Frame(g, panel, UiTheme.Surface, UiTheme.Border);

            UiTheme.DrawText(g, "参数", UiTheme.BodyBold, UiTheme.TextColor,
                TitleBounds("参数", panel), ContentAlignment.MiddleLeft);

            var y = panel.Y + 34;
            UiTheme.Separator(g, panel.X, y, panel.Width, true);
            y += 8;

            y = DrawStepper(g, panel, y, "移调", _settings.Transpose.ToString(CultureInfo.InvariantCulture), "transpose");
            y = DrawStepper(g, panel, y, "颤音", _settings.Jitter.ToString(CultureInfo.InvariantCulture), "jitter");
            y = DrawToggle(g, panel, y, "事件释放", _settings.ReleaseOnEvent, "releaseonevent");
            y = DrawToggle(g, panel, y, "移鼠释放", _settings.ReleaseOnMouseMove, "releaseonmousemove");
            y = DrawValue(g, panel, y, "接管键", HotkeyName(_settings.HotkeyVirtualKey), "hotkey");
            y = DrawValue(g, panel, y, "曲库目录", ShortPath(_settings.MusicFolder), "musicfolder");

            // 播放与停止。区域取自 PlayButtonBounds/StopButtonBounds，与自检共用同一份坐标。
            var play = PlayButtonBounds();
            var stop = StopButtonBounds();
            UiTheme.Separator(g, panel.X, play.Y - 8, panel.Width, true);

            var playing = _pluginStatus != null && _pluginStatus.Active;
            var playFill = playing ? UiTheme.Active : UiTheme.Accent;
            UiTheme.Frame(g, play, playFill, playFill);
            UiTheme.DrawText(g, playing ? "演奏中" : "开始演奏", UiTheme.BodyBold, Color.White, play, ContentAlignment.MiddleCenter);

            UiTheme.Frame(g, stop, UiTheme.Surface, UiTheme.Border);
            UiTheme.DrawText(g, "停止", UiTheme.Body, UiTheme.TextColor, stop, ContentAlignment.MiddleCenter);
        }

        private int DrawStepper(Graphics g, Rectangle panel, int y, string label, string value, string key)
        {
            var bounds = new Rectangle(panel.X + UiTheme.Padding, y, panel.Width - UiTheme.Padding * 2, 28);
            UiTheme.DrawText(g, label, UiTheme.Body, UiTheme.TextColor, bounds, ContentAlignment.MiddleLeft);

            var minus = new Rectangle(bounds.Right - 64, y + 2, 24, 24);
            var plus = new Rectangle(bounds.Right - 24, y + 2, 24, 24);
            var field = new Rectangle(bounds.Right - 40, y + 2, 16, 24);

            UiTheme.Frame(g, minus, _hover == key + "-" ? UiTheme.AccentSoft : UiTheme.Subtle, UiTheme.Border);
            UiTheme.DrawText(g, "-", UiTheme.Body, UiTheme.TextColor, minus, ContentAlignment.MiddleCenter);
            UiTheme.Frame(g, plus, _hover == key + "+" ? UiTheme.AccentSoft : UiTheme.Subtle, UiTheme.Border);
            UiTheme.DrawText(g, "+", UiTheme.Body, UiTheme.TextColor, plus, ContentAlignment.MiddleCenter);
            UiTheme.DrawText(g, value, UiTheme.Body, UiTheme.TextMuted, field, ContentAlignment.MiddleCenter);
            return y + 34;
        }

        private int DrawToggle(Graphics g, Rectangle panel, int y, string label, bool on, string key)
        {
            var bounds = new Rectangle(panel.X + UiTheme.Padding, y, panel.Width - UiTheme.Padding * 2, 28);
            UiTheme.DrawText(g, label, UiTheme.Body, UiTheme.TextColor, bounds, ContentAlignment.MiddleLeft);

            var box = new Rectangle(bounds.Right - 20, y + 6, 16, 16);
            UiTheme.Frame(g, box, on ? UiTheme.Accent : UiTheme.Surface, on ? UiTheme.Accent : UiTheme.Border);
            if (on)
            {
                using (var brush = new SolidBrush(Color.White))
                    g.FillRectangle(brush, new Rectangle(box.X + 4, box.Y + 4, 8, 8));
            }
            return y + 32;
        }

        private int DrawValue(Graphics g, Rectangle panel, int y, string label, string value, string key)
        {
            var bounds = new Rectangle(panel.X + UiTheme.Padding, y, panel.Width - UiTheme.Padding * 2, 28);
            UiTheme.DrawText(g, label, UiTheme.Body, UiTheme.TextColor, bounds, ContentAlignment.MiddleLeft);
            UiTheme.DrawText(g, value, UiTheme.Body, UiTheme.TextMuted,
                new Rectangle(bounds.Right - 150, y, 150, 28), ContentAlignment.MiddleRight);
            return y + 32;
        }

        private void DrawBottomBar(Graphics g)
        {
            var bounds = BottomBar;
            UiTheme.Frame(g, bounds, UiTheme.Surface, UiTheme.Border);

            var text = new StringBuilder();
            if (_pluginStatus == null)
            {
                text.Append("控制台未运行");
            }
            else
            {
                text.Append("tick ").Append(_pluginStatus.Tick.ToString(CultureInfo.InvariantCulture));
                text.Append("  ·  音 ").Append(_pluginStatus.Strikes.ToString(CultureInfo.InvariantCulture));
                var percent = (int)Math.Round(_pluginStatus.Progress * 100d);
                text.Append("  ·  ").Append(percent.ToString(CultureInfo.InvariantCulture)).Append('%');
                if (!string.IsNullOrEmpty(_pluginStatus.HeldItem))
                    text.Append("  ·  ").Append(_pluginStatus.HeldItem);
                if (!string.IsNullOrEmpty(_pluginStatus.Issue))
                    text.Append("  ·  ").Append(_pluginStatus.Issue);
            }

            UiTheme.DrawText(g, text.ToString(), UiTheme.Small, UiTheme.TextMuted,
                new Rectangle(UiTheme.Padding, bounds.Y, bounds.Width - 240, bounds.Height), ContentAlignment.MiddleLeft);
            UiTheme.DrawText(g, "v" + typeof(MainForm).Assembly.GetName().Version, UiTheme.Small, UiTheme.TextMuted,
                new Rectangle(bounds.Right - 220, bounds.Y, 208, bounds.Height), ContentAlignment.MiddleRight);
        }

        private void DrawToast(Graphics g)
        {
            if (_toast == null || DateTime.UtcNow > _toastUntil) return;
            var bounds = new Rectangle(ClientSize.Width / 2 - 190, BottomBar.Top - 44, 380, 32);
            UiTheme.Frame(g, bounds, UiTheme.TextColor, UiTheme.TextColor);
            UiTheme.DrawText(g, _toast, UiTheme.Body, Color.White, bounds, ContentAlignment.MiddleCenter);
        }

        // ---------------------------------------------------------------- 命中区

        private string _hover;

        internal List<Rectangle> InstrumentRects()
        {
            var result = new List<Rectangle>();
            var panel = LeftPanel;
            const int columns = 2;
            var cardWidth = (panel.Width - UiTheme.Padding * 2 - UiTheme.Gap) / columns;
            var top = panel.Y + 34 + UiTheme.Gap;
            for (var i = 0; i < InstrumentModel.All.Count; i++)
            {
                var column = i % columns;
                var row = i / columns;
                var x = panel.X + UiTheme.Padding + column * (cardWidth + UiTheme.Gap);
                var y = top + row * (UiTheme.InstrumentCardHeight + UiTheme.Gap);
                result.Add(new Rectangle(x, y, cardWidth, UiTheme.InstrumentCardHeight));
            }
            return result;
        }

        internal List<Rectangle> TrackRects()
        {
            var result = new List<Rectangle>();
            var panel = CenterPanel;
            var top = panel.Y + 34 + 4;
            var available = panel.Bottom - top - 28;
            var capacity = Math.Max(1, available / (UiTheme.RowHeight + 4));
            for (var i = 0; i < Math.Min(capacity, _tracks.Count); i++)
            {
                result.Add(new Rectangle(panel.X + UiTheme.Gap, top + i * (UiTheme.RowHeight + 4),
                    panel.Width - UiTheme.Gap * 2, UiTheme.RowHeight));
            }
            return result;
        }

        internal List<ActionButton> ActionButtons()
        {
            var result = new List<ActionButton>();
            var bounds = TopBar;
            var install = new Rectangle(bounds.Right - UiTheme.Padding - 92, bounds.Y + 10, 92, 28);
            var restore = new Rectangle(install.X - UiTheme.Gap - 76, bounds.Y + 10, 76, 28);
            var sprites = new Rectangle(restore.X - UiTheme.Gap - 76, bounds.Y + 10, 76, 28);

            // 提取贴图只在缺少缓存时才有意义，所以按钮状态跟着缓存走。
            var spriteNote = SpriteStore.Describe(_terrariaExe);
            result.Add(new ActionButton("sprites", spriteNote == null ? "贴图就绪" : "提取贴图", sprites, spriteNote != null));
            result.Add(new ActionButton("restore", "还原",
                restore, _install != null && _install.State == InstallState.Installed));
            result.Add(new ActionButton("install", "安装",
                install, _install != null && _install.CanInstall && _install.State != InstallState.Installed));
            return result;
        }

        private string HitTest(Point point)
        {
            foreach (var button in ActionButtons())
                if (button.Bounds.Contains(point)) return button.Key;

            var cards = InstrumentRects();
            for (var i = 0; i < cards.Count; i++)
                if (cards[i].Contains(point)) return "instrument:" + i;

            var rows = TrackRects();
            for (var i = 0; i < rows.Count; i++)
                if (rows[i].Contains(point)) return "track:" + i;

            var panel = RightPanel;
            var y = panel.Y + 42;
            foreach (var key in new[] { "transpose", "jitter" })
            {
                var minus = new Rectangle(panel.Right - UiTheme.Padding - 64, y + 2, 24, 24);
                var plus = new Rectangle(panel.Right - UiTheme.Padding - 24, y + 2, 24, 24);
                if (minus.Contains(point)) return key + "-";
                if (plus.Contains(point)) return key + "+";
                y += 34;
            }
            foreach (var key in new[] { "releaseonevent", "releaseonmousemove" })
            {
                if (new Rectangle(panel.X + UiTheme.Padding, y, panel.Width - UiTheme.Padding * 2, 28).Contains(point))
                    return key;
                y += 32;
            }

            if (PlayButtonBounds().Contains(point)) return "play";
            if (StopButtonBounds().Contains(point)) return "stop";

            return null;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            var previous = _hover;
            _hover = HitTest(e.Location);
            Cursor = _hover == null ? Cursors.Default : Cursors.Hand;
            if (previous != _hover) Invalidate();
        }

        private void OnMouseDown(object sender, MouseEventArgs e)
        {
            var hit = HitTest(e.Location);
            if (hit == null) return;

            if (hit.StartsWith("instrument:", StringComparison.Ordinal))
            {
                _selectedInstrument = int.Parse(hit.Substring("instrument:".Length), CultureInfo.InvariantCulture);
                _settings.PreferredInstrument = InstrumentModel.All[_selectedInstrument].ItemId;
                SaveSettings();
            }
            else if (hit.StartsWith("track:", StringComparison.Ordinal))
            {
                var index = int.Parse(hit.Substring("track:".Length), CultureInfo.InvariantCulture);
                if (index >= 0 && index < _tracks.Count)
                {
                    _selectedTrack = _tracks[index].Id;
                    SaveSettings();
                }
            }
            else
            {
                switch (hit)
                {
                    case "transpose-": _settings.Transpose = Math.Max(-12, _settings.Transpose - 1); SaveSettings(); break;
                    case "transpose+": _settings.Transpose = Math.Min(12, _settings.Transpose + 1); SaveSettings(); break;
                    case "jitter-": _settings.Jitter = Math.Max(0, _settings.Jitter - 1); SaveSettings(); break;
                    case "jitter+": _settings.Jitter = Math.Min(8, _settings.Jitter + 1); SaveSettings(); break;
                    case "releaseonevent": _settings.ReleaseOnEvent = !_settings.ReleaseOnEvent; SaveSettings(); break;
                    case "releaseonmousemove": _settings.ReleaseOnMouseMove = !_settings.ReleaseOnMouseMove; SaveSettings(); break;
                    case "install": DoInstall(); break;
                    case "restore": DoRestore(); break;
                    case "sprites": DoSprites(); break;
                    case "play": DoPlay(); break;
                    case "stop": DoStop(); break;
                }
            }
            Invalidate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveSettings();
            base.OnFormClosing(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && _pluginStatus != null && _pluginStatus.Active) DoStop();
            base.OnKeyDown(e);
        }

        // ---------------------------------------------------------------- 动作

        private void DoInstall()
        {
            var result = _service.Install(_terrariaExe, AppDomain.CurrentDomain.BaseDirectory);
            Toast(result.Message);
            RefreshInstall();
        }

        private void DoRestore()
        {
            var result = _service.Restore(_terrariaExe);
            Toast(result.Message);
            RefreshInstall();
        }

        private void DoSprites()
        {
            string error;
            if (SpriteStore.TryEnsure(_terrariaExe, out error))
            {
                EnsureSprites();
                Toast("贴图已提取 " + _sprites.Count.ToString(CultureInfo.InvariantCulture) + " 张");
            }
            else
            {
                Toast(error);
            }
        }

        private void DoPlay()
        {
            var data = DataDirectory();
            if (data == null) { Toast("尚未定位游戏目录"); return; }
            var track = FindTrack(_selectedTrack);
            if (track == null) { Toast("尚未选择曲目"); return; }

            // 只在玩家确实点过乐器时才下发偏好；否则交给控制台决定
            // （它默认用手上那件，这符合「玩家拿什么就是什么」）。
            _settings.Track = track.Id;
            if (_selectedInstrument >= 0)
                _settings.PreferredInstrument = InstrumentModel.All[_selectedInstrument].ItemId;
            TingYuConfigView.Request(data, "play", _settings);
            Toast("已请求演奏");
        }

        private void DoStop()
        {
            var data = DataDirectory();
            if (data == null) return;
            TingYuConfigView.Request(data, "stop", _settings);
            Toast("已请求停止");
        }

        /// <summary>把配置里的乐器偏好还原成界面选中态。找不到就保持未指定。</summary>
        private void SyncSelectionFromSettings()
        {
            _selectedInstrument = -1;
            if (_settings.PreferredInstrument <= 0) return;
            var all = InstrumentModel.All;
            for (var i = 0; i < all.Count; i++)
            {
                if (all[i].ItemId != _settings.PreferredInstrument) continue;
                _selectedInstrument = i;
                return;
            }
        }

        private void SaveSettings()
        {
            var data = DataDirectory();
            if (data == null) return;
            _settings.Track = _selectedTrack;
            TingYuConfigView.Save(data, _settings);
        }

        private static string HotkeyName(int virtualKey)
        {
            if (virtualKey >= 0x70 && virtualKey <= 0x7B)
                return "F" + (virtualKey - 0x6F).ToString(CultureInfo.InvariantCulture);
            if (virtualKey >= 0x41 && virtualKey <= 0x5A)
                return ((char)virtualKey).ToString();
            return "0x" + virtualKey.ToString("X2", CultureInfo.InvariantCulture);
        }

        private static string ShortPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "—";
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? path : name;
        }

        /// <summary>
        /// 界面上**必须出现**的元素清单（区域 + 一句人话）。
        ///
        /// 存在的理由：UI 代码画错时，靠读源码几乎看不出来，而每改一次就让人看一次
        /// 也不现实。这里把「哪些位置必须有东西」写下来，自检据此逐个区域检查有没有内容，
        /// 于是「某个面板整块空白」这类错误会被自动抓住。
        ///
        /// 清单由界面自己给出，而不是测试里另写一份坐标——那样两边一定会漂移。
        /// </summary>
        internal List<LabelCheck> ExpectedLabels()
        {
            var result = new List<LabelCheck>();

            void Add(string text, Rectangle bounds)
            {
                if (bounds.Width > 0 && bounds.Height > 0) result.Add(new LabelCheck(text, bounds));
            }

            var top = TopBar;
            Add("标题", new Rectangle(UiTheme.Padding, top.Y, 160, top.Height));
            Add("安装状态", new Rectangle(184, top.Y, 300, top.Height));
            foreach (var button in ActionButtons()) Add("按钮 " + button.Label, button.Bounds);

            Add("乐器标题", TitleBounds("乐器", LeftPanel));
            var cards = InstrumentRects();
            var all = InstrumentModel.All;
            for (var i = 0; i < cards.Count && i < all.Count; i++)
            {
                Add("乐器 " + all[i].NameZh,
                    InstrumentNameBounds(cards[i], InstrumentIconBounds(cards[i])));
            }

            Add("曲目标题", TitleBounds("曲目", CenterPanel));
            var rows = TrackRects();
            for (var i = 0; i < rows.Count && i < _tracks.Count; i++)
            {
                Add("曲目 " + _tracks[i].Title, TrackTitleBounds(rows[i]));
                Add("曲目类型 " + _tracks[i].Title, TrackKindBounds(rows[i]));
            }

            Add("参数标题", TitleBounds("参数", RightPanel));
            Add("开始演奏", PlayButtonBounds());
            Add("停止", StopButtonBounds());

            var bottom = BottomBar;
            Add("状态栏", new Rectangle(UiTheme.Padding, bottom.Y, 300, bottom.Height));
            return result;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _pollTimer.Dispose();
                foreach (var image in _sprites.Values) image.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>自检要检查的一个区域：这块地方应该画出东西来。</summary>
    internal sealed class LabelCheck
    {
        internal readonly string Text;
        internal readonly Rectangle Bounds;

        internal LabelCheck(string text, Rectangle bounds)
        {
            Text = text;
            Bounds = bounds;
        }
    }

    internal sealed class TrackRow
    {
        internal string Id;
        internal string Title;
        internal string Kind;
    }

    /// <summary>
    /// 顶部的一个按钮。
    ///
    /// 特意不用 `KeyValuePair`：按钮需要「键、文字、区域、是否可用」四项，
    /// 硬塞进 `KeyValuePair` 的结果就是到处写 `button.Value.Contains(point)`
    /// 这种读不出意图的代码，而且一旦要多带一项就得连着改所有调用点。
    /// </summary>
    internal sealed class ActionButton
    {
        internal readonly string Key;
        internal readonly string Label;
        internal readonly Rectangle Bounds;
        internal readonly bool Enabled;

        internal ActionButton(string key, string label, Rectangle bounds, bool enabled)
        {
            Key = key;
            Label = label;
            Bounds = bounds;
            Enabled = enabled;
        }
    }
}
