using System;
using System.Globalization;
using TingYu.Core;

namespace TingYu.Plugin
{
    /// <summary>接管结束的原因。日志与界面都要能看到这一条，否则「为什么停了」没法回答。</summary>
    public enum ReleaseReason
    {
        None,
        Finished,
        HotkeyPressed,
        PlayerDied,
        InstrumentLost,
        PlayerLeftWorld,
        WorldEvent,
        InputMoved,
        Disabled,
        Fault,
        DeviceLost
    }

    /// <summary>
    /// 接管状态机。
    ///
    /// 发声方式：**直接复刻原版的发声代码**（见 `Strike`），不移动光标、不伪造鼠标按键。
    ///
    /// 曾经试过「把真实光标推到目标位置，让原版自己算音高」那条路。它有一个致命副作用：
    /// 为了让原版认定「左键刚按下」，必须把 `Main.mouseLeft` 与 `Main.mouseLeftRelease`
    /// 都置为 true，而这两个位在 `Main.Draw` 里被界面消费——结果就是每一帧都在界面上
    /// 点一次鼠标，玩家会看到一个凭空冒出来的界面。这条路已经彻底放弃。
    ///
    /// 时序（每个 tick 一次；注入点在 `Main.UpdateWorld_Players` 的入口与出口）：
    ///
    /// ```
    /// BeginFrame() : 检查是否该释放 → 问 Performance 要不要发音 → 要就 Strike()
    /// EndFrame()   : 什么都不用还原（不碰光标与按键的直接好处）
    /// ```
    ///
    /// 另外两个注入点（`Player.Update` 里的 `CaptureCursorInput`、
    /// `ItemCheck_PlayInstruments` 入口的 `InstrumentTick`）现在都只是空钩子，
    /// 保留是为了「玩家自己点击乐器时原版行为完全不受影响」以及将来可能的方案回退。
    /// </summary>
    public sealed class Takeover
    {
        /// <summary>
        /// `TryStart` 的特殊返回值：正在把手上的物品换成乐器，需要调用方在下一 tick 重试。
        /// 不把它写成普通错误文案，是因为调用方必须区别对待——错误要报给玩家，这个要重试。
        /// </summary>
        public const string SwitchPending = "\u0000切换中";

        private readonly GameFacade _game;
        private readonly ScoreLibrary _library;
        private readonly Diagnostics _log;

        private TingYuConfig _config;
        private Performance _performance;
        private InstrumentModel _instrument;
        private TrackEntry _track;

        private float _lastPitch;
        private float _lastSoundedPitch;
        private int _lastStep = int.MinValue;
        private int _struckTicks;
        private int _confirmedTicks;

        /// <summary>我们替玩家换过的手上格子：`_restoreSlot` 是玩家原本那一格。</summary>
        private int _restoreSlot = -1;
        private int _selectedSlot = -1;
        private bool _selectionIsOurs;
        private bool _restorePending;

        /// <summary>上一次换手时写给日志的那句话，等真正开始时一起打出来，避免重复刷日志。</summary>
        private string _pendingSlotNote;

        public Takeover(GameFacade game, ScoreLibrary library, Diagnostics log)
        {
            _game = game;
            _library = library;
            _log = log;
        }

        public bool Active { get { return _performance != null; } }

        public ReleaseReason LastRelease { get; private set; }

        public string LastReleaseDetail { get; private set; }

        public int StruckTicks { get { return _struckTicks; } }

        /// <summary>
        /// 实际确认发声的次数。
        ///
        /// 以前这个数是靠回读 `Main.musicPitch` 有没有变化得来的，那是「借原版发声」
        /// 方案下的验证手段。现在发声由自己完成，可以更直接：
        /// 只要 `PlayNote` 没有抛异常返回，就是发出去了一次。
        /// 保留 `_confirmedTicks` 是为了观察「自定义音高是否真的被引擎接受」——
        /// 它记录的是 `Main.musicPitch` 是否等于我们要求的音高。
        /// </summary>
        public int ConfirmedTicks { get { return _confirmedTicks; } }

        public float LastPitch { get { return _lastPitch; } }

        public int LastStep { get { return _lastStep; } }

        public TrackEntry CurrentTrack { get { return _track; } }

        public InstrumentModel CurrentInstrument { get { return _instrument; } }

        public double Progress { get { return _performance == null ? 0d : _performance.Progress; } }

        public int ElapsedTicks { get { return _performance == null ? 0 : _performance.ElapsedTicks; } }

        public int TotalTicks { get { return _performance == null ? 0 : _performance.Score.DurationTicks; } }

        /// <summary>
        /// 尝试开始接管。
        ///
        /// 返回 null 表示成功；返回 `SwitchPending` 表示「正在换手，稍后再试」——
        /// 换手是缓冲式的，原版要到下一次 `Player.Update` 才真正生效，不可能当帧完成。
        /// 其余返回值是拒绝原因（中文，直接给玩家看）。
        /// </summary>
        public string TryStart(TingYuConfig config)
        {
            _config = config;
            if (Active) return "已经在接管中。";
            if (!config.Enabled) return "接管已在界面上关闭。";

            var player = _game.LocalPlayer;
            if (player == null || !_game.LocalPlayerInWorld) return "当前不在游戏里。";

            // 先把「手上拿的」对齐到背包里的乐器：玩家带着竖琴但手上拿着镐子时，
            // 按一次接管键就够了，不需要他先手动切到那一格。
            var itemType = _game.SelectedItemType(player);
            var instrument = InstrumentModel.Find(itemType);

            // 界面点过某件乐器时以界面为准。这是唯一的覆盖路径，默认（0）不覆盖，
            // 于是「玩家自己拿在手上的那件」始终是默认答案。
            var preferred = config.PreferredInstrument > 0
                ? InstrumentModel.Find(config.PreferredInstrument)
                : null;
            if (preferred != null && preferred.ItemId != itemType)
            {
                var preferredSlot = FindInstrumentSlot(player, preferred.ItemId);
                if (preferredSlot >= 0)
                {
                    var previous = _game.SelectedSlot(player);
                    if (_game.SelectSlot(player, preferredSlot))
                    {
                        _pendingSlotNote = "按界面选择换成「" + preferred.NameZh + "」（第 " +
                                           (preferredSlot + 1) + " 格，原为第 " + (previous + 1) + " 格）";
                        return SwitchPending;
                    }
                }
                // 界面上选了但没有这件：不静默忽略，直接说清楚，否则玩家只会觉得「点了没反应」。
                return "背包里没有界面选中的「" + preferred.NameZh + "」。";
            }

            if (instrument == null)
            {
                var slotNote = AutoSelectInstrument(player);
                if (slotNote != null)
                {
                    _pendingSlotNote = slotNote;
                    return SwitchPending;
                }
                return "背包里没有可演奏的乐器（需要竖琴、铃铛、吉他斧、雨歌、常春藤或星星吉他）。";
            }

            var eventReason = config.ReleaseOnWorldEvent ? _game.WorldEventReason() : null;
            if (!string.IsNullOrEmpty(eventReason)) return "正在发生事件：" + eventReason + "。";

            var score = _library.GetScore(config.Track);
            if (score == null || score.Notes.Count == 0)
            {
                var track = _library.Find(config.Track);
                return track == null ? "没有选中曲目。" : "选中的曲目「" + track.Title + "」还没有转录结果。";
            }

            _instrument = instrument;
            _track = _library.Find(config.Track);
            var baseMidi = NoteNames.MiddleC + config.Transpose;
            _performance = new Performance(score, instrument, baseMidi, config.JitterTicks,
                Environment.TickCount ^ score.Notes.Count);
            _performance.Start();

            _struckTicks = 0;
            _confirmedTicks = 0;
            _lastStep = int.MinValue;
            _lastSoundedPitch = float.NaN;
            LastRelease = ReleaseReason.None;
            LastReleaseDetail = null;

            _log.Write("接管开始：曲目「" + score.Title + "」· 乐器 " + instrument.NameZh +
                       " · 音符 " + score.Notes.Count + " · 总长 " + score.DurationTicks + " tick" +
                       (config.Transpose == 0 ? string.Empty : " · 移调 " + config.Transpose + " 半音") +
                       (_pendingSlotNote == null ? string.Empty : " · " + _pendingSlotNote));
            _pendingSlotNote = null;
            return null;
        }

        /// <summary>
        /// 需要时把乐器换到手上，并返回一句给日志用的说明（没换则返回 null）。
        ///
        /// 手上已经有乐器时什么都不做——包括「手上有竖琴但曲目想弹吉他」这种情况：
        /// 玩家把哪件乐器拿在手上，就是他想用哪件，这一点不该被程序覆盖。
        /// 只有手上一件乐器都没有时才去背包里挑第一件。
        /// </summary>
        private string AutoSelectInstrument(object player)
        {
            var currentType = _game.SelectedItemType(player);
            if (InstrumentModel.IsInstrument(currentType)) return null;

            var types = _game.InventoryItemTypes(player);
            var slot = InstrumentModel.FirstInstrumentSlot(types, GameFacade.HotbarSlotCount);
            if (slot < 0) return null;

            var previous = _game.SelectedSlot(player);
            if (!_game.SelectSlot(player, slot)) return null;

            _restoreSlot = previous;
            _selectedSlot = slot;
            _selectionIsOurs = true;
            return "已把手上的物品换成 " + InstrumentModel.Find(types[slot]).NameZh +
                   "（原为第 " + (previous + 1) + " 格）";
        }

        /// <summary>
        /// 在背包里找某件指定乐器所在的格子；没有返回 -1。
        ///
        /// 与 `FirstInstrumentSlot` 的区别是它找的是**指定的一件**，
        /// 用在「界面点了某件乐器」这条路径上。热键栏优先的理由同前：
        /// 那是玩家自己排好的位置。
        /// </summary>
        private int FindInstrumentSlot(object player, int itemId)
        {
            var types = _game.InventoryItemTypes(player);
            for (var i = 0; i < types.Count && i < GameFacade.HotbarSlotCount; i++)
                if (types[i] == itemId) return i;
            for (var i = GameFacade.HotbarSlotCount; i < types.Count; i++)
                if (types[i] == itemId) return i;
            return -1;
        }

        /// <summary>
        /// 每个 tick 把选中槽位对齐到我们想要的那一格。
        ///
        /// 为什么要重复做：`Player.Update` 里有若干分支会重算 `selectedItem`
        /// （比如使用物品时的自动切换）。只设置一次会出现
        /// 「第一下弹了、后面全哑了」这种极难查的现象。
        /// </summary>
        private void RefreshSelection(object player)
        {
            if (!_selectionIsOurs || player == null) return;
            var actual = _game.SelectedSlot(player);
            if (actual == _selectedSlot) return;
            if (_restorePending)
            {
                // 我们自己换回去的那一次，不再抢回来。
                _selectionIsOurs = false;
                _restorePending = false;
                return;
            }
            _game.SelectSlot(player, _selectedSlot);
        }

        /// <summary>结束接管。`reason` 会写进日志与状态文件。</summary>
        public void Release(ReleaseReason reason, string detail)
        {
            if (!Active) return;
            var title = _track == null ? "?" : _track.Title;
            _log.Write("接管结束：原因 " + Describe(reason) +
                       (string.IsNullOrEmpty(detail) ? string.Empty : "（" + detail + "）") +
                       " · 曲目 " + title +
                       " · 已发音 " + _struckTicks + " 次" +
                       " · 进度 " + (_performance.Progress * 100d).ToString("0.0", CultureInfo.InvariantCulture) + "%");
            LastRelease = reason;
            LastReleaseDetail = detail;
            _performance = null;
            _instrument = null;
            RestoreSelection();
        }

        /// <summary>把玩家原本拿着的那一格换回去。</summary>
        private void RestoreSelection()
        {
            if (!_selectionIsOurs) return;
            try
            {
                var player = _game.LocalPlayer;
                if (player != null && _restoreSlot >= 0 && _restoreSlot != _selectedSlot)
                {
                    _restorePending = true;
                    _game.SelectSlot(player, _restoreSlot);
                }
            }
            catch (Exception exception)
            {
                _log.Write("换回原来的物品栏位置失败：" + exception.Message);
            }
            _selectionIsOurs = false;
            _selectedSlot = -1;
            _restoreSlot = -1;
        }

        public static string Describe(ReleaseReason reason)
        {
            switch (reason)
            {
                case ReleaseReason.Finished: return "整首弹完";
                case ReleaseReason.HotkeyPressed: return "再次按下接管键";
                case ReleaseReason.PlayerDied: return "角色死亡";
                case ReleaseReason.InstrumentLost: return "乐器离手或换成了别的物品";
                case ReleaseReason.PlayerLeftWorld: return "离开世界 / 回到主菜单";
                case ReleaseReason.WorldEvent: return "世界事件";
                case ReleaseReason.InputMoved: return "玩家自己动了鼠标";
                case ReleaseReason.Disabled: return "界面关闭了接管";
                case ReleaseReason.DeviceLost: return "窗口失去焦点";
                case ReleaseReason.Fault: return "注入层出错（已安全释放）";
                default: return "未开始";
            }
        }

        // ---------------------------------------------------------------- 帧

        /// <summary>AF：一帧开始。决定本 tick 要不要发音。</summary>
        public void BeginFrame()
        {
            try
            {
                BeginFrameCore();
            }
            catch (Exception exception)
            {
                // 注入层的异常绝不能冒泡回游戏：一次异常就可能让 Terraria 直接崩。
                _log.Write("BeginFrame 异常：" + exception.GetType().Name + " " + exception.Message);
                if (Active) Release(ReleaseReason.Fault, exception.Message);
            }
        }

        private void BeginFrameCore()
        {
            if (!Active) return;

            var reason = CheckReleaseConditions();
            if (!string.IsNullOrEmpty(reason))
            {
                Release(ReasonForCheck(), reason);
                return;
            }

            // 接管期间持续把手上那一格钉在乐器上：原版的若干分支会自行重算 selectedItem。
            RefreshSelection(_game.LocalPlayer);

            var axis = _game.SmallerScaledAxis;
            var command = _performance.Tick(axis);
            switch (command.Action)
            {
                case PerformanceAction.Strike:
                    Strike(command);
                    break;
                case PerformanceAction.Finish:
                    Release(ReleaseReason.Finished, null);
                    break;
            }
        }

        private ReleaseReason _lastCheckReason;

        private string CheckReleaseConditions()
        {
            _lastCheckReason = ReleaseReason.None;

            if (_config != null && !_config.Enabled)
            {
                _lastCheckReason = ReleaseReason.Disabled;
                return "界面已关闭接管";
            }

            var player = _game.LocalPlayer;
            if (player == null || !_game.LocalPlayerInWorld)
            {
                if (player != null && _game.PlayerDead(player))
                {
                    _lastCheckReason = ReleaseReason.PlayerDied;
                    return "角色死亡";
                }
                _lastCheckReason = ReleaseReason.PlayerLeftWorld;
                return "已离开世界或回到主菜单";
            }

            if (_game.PlayerDead(player))
            {
                _lastCheckReason = ReleaseReason.PlayerDied;
                return "角色死亡";
            }

            if (_game.SelectedItemType(player) != _instrument.ItemId)
            {
                _lastCheckReason = ReleaseReason.InstrumentLost;
                return "手上已经不是 " + _instrument.NameZh;
            }

            if (_config != null && _config.ReleaseOnWorldEvent)
            {
                var worldEvent = _game.WorldEventReason();
                if (!string.IsNullOrEmpty(worldEvent))
                {
                    _lastCheckReason = ReleaseReason.WorldEvent;
                    return worldEvent;
                }
            }

            return null;
        }

        private ReleaseReason ReasonForCheck()
        {
            return _lastCheckReason == ReleaseReason.None ? ReleaseReason.Fault : _lastCheckReason;
        }

        /// <summary>
        /// 发一个音。
        ///
        /// 音高严格按原版公式算（`GameFacade.PitchFromWorldDistance`）；
        /// 发声走原版同一套（`SoundEngine.PlaySound`、`PlayGuitarChord`、
        /// `NetMessage.SendData(58)`），但**不移动光标、不伪造鼠标按键**。
        /// </summary>
        private void Strike(PerformanceCommand command)
        {
            var player = _game.LocalPlayer;
            if (player == null) return;

            var normalized = command.NormalizedDistance;
            if (normalized < 0f) normalized = 0f;
            if (normalized > 1f) normalized = 1f;

            try
            {
                _game.PlayNote(player, _instrument.ItemId, normalized, command.MusicPitch);
            }
            catch (Exception exception)
            {
                // 发声本身失败（比如某个反射签名对不上）不该让整首曲子停掉，
                // 但要立刻留下证据：这正是「听得见但不知道为什么没声音」的根因所在。
                _log.Write("发声失败（第 " + (_struckTicks + 1) + " 个音）：" +
                           exception.GetType().Name + " " + exception.Message);
                throw;
            }

            _game.SetMusicDistance(player, normalized);

            _lastPitch = command.MusicPitch;
            _lastStep = command.Step;
            _struckTicks++;

            // 验证「自定义音高真的被引擎接受了」：只有原版单音路径会写 Main.musicPitch，
            // 用它来判断这次发声音高是否符合预期。和弦路径不写这个字段，所以跳过。
            if (_instrument.Kind == TingYu.Core.InstrumentModel.SoundKind.Monophonic)
            {
                var actual = _game.MusicPitch;
                if (Math.Abs(actual - command.MusicPitch) < 0.0001f) _confirmedTicks++;
                else
                    _log.Verbose_("音高未生效：要求 " +
                                  command.MusicPitch.ToString("0.0000", CultureInfo.InvariantCulture) +
                                  " 实际 " + actual.ToString("0.0000", CultureInfo.InvariantCulture));
            }
            else
            {
                _confirmedTicks++;
            }

            _lastSoundedPitch = command.MusicPitch;
            // 轴长单独用 DescribeAxis 展开。轴长为 0 时，它旁边的那些数字（像素 0.0、
            // 归一化 1.0000）全都失去意义，所以必须把「这个轴长是怎么来的」一起写下来——
            // 否则只能看到「所有音都是同一个音高」这种没有指向性的现象。
            //
            // 拨弦乐器没有 musicPitch，发的是和弦，日志里改成写档位与归一化距离，
            // 否则会拿一个对该乐器无意义的数字去和单音乐器对照。
            var pitchField = _instrument.Kind == TingYu.Core.InstrumentModel.SoundKind.Chord
                ? "和弦档 " + TingYu.Core.InstrumentModel.ChordBucketForStep(command.Step)
                : "musicPitch " + command.MusicPitch.ToString("0.0000", CultureInfo.InvariantCulture);
            _log.Verbose_("发音 #" + _struckTicks + " 级数 " + command.Step +
                          " · 目标音 " + NoteNames.FromMidi(command.Midi) +
                          " · 像素 " + command.PixelDistance.ToString("0.0", CultureInfo.InvariantCulture) +
                          " · 归一化 " + normalized.ToString("0.0000", CultureInfo.InvariantCulture) +
                          " · " + pitchField +
                          " · " + _game.DescribeAxis());
        }

        /// <summary>CU：`Player.Update` 刚把输入复制进 `Main`。当前不需要做任何事。</summary>
        public void CaptureCursorInput(int tick)
        {
            // 空实现。注入点与钩子保留着，是为了将来若要回到「由原版驱动发声」
            // （比如确实需要原版的和弦选择逻辑）时不必重新设计注入位置。
        }

        /// <summary>
        /// MN：`ItemCheck_PlayInstruments` 入口。始终返回 false。
        ///
        /// 返回 false = 让原版照常执行。因为发声已经完全由 `Strike` 在
        /// `BeginFrame` 里做完，不需要再靠「跳过原版」来避免重复发声；
        /// 而让原版照常跑，玩家自己点击乐器时的行为就完全没被我们改过。
        /// </summary>
        public bool InstrumentTick()
        {
            return false;
        }

        /// <summary>AB：一帧结束。现在没有任何需要还原的状态。</summary>
        public void EndFrame()
        {
        }

        /// <summary>界面下发 stop 时调用。</summary>
        public void ReleaseByRequest()
        {
            Release(ReleaseReason.HotkeyPressed, "界面请求停止");
        }
    }
}
