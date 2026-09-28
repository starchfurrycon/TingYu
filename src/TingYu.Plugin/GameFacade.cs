using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace TingYu.Plugin
{
    /// <summary>
    /// 注入层与 Terraria 之间的唯一边界。
    ///
    /// 全部成员在构造时按 `ReflectionRequirements` 的清单解析一次，之后每 tick 只做委托式调用——
    /// 反射的 `GetField` 放进 60 Hz 的循环里会让帧时间明显变长。
    ///
    /// 三条硬规矩，每一条都是被现实撞出来的：
    /// 1. **只解析一次并缓存**。除了性能，更重要的是「绑定失败要一次报全」。
    /// 2. **继承来的成员必须用 `FlattenHierarchy`**。`Player.Center` 与 `Player.whoAmI`
    ///    定义在基类 `Terraria.Entity` 上，不展开层级就找不到——现象只是「按了键没反应」。
    /// 3. **字段与属性是两条完全不同的路**。`Main.GameUpdateCount` 与 `Player.Center`
    ///    都是属性，照直觉当字段读只会静默拿到 null。
    /// </summary>
    public sealed class GameFacade
    {
        private readonly Dictionary<string, MemberInfo> _resolved =
            new Dictionary<string, MemberInfo>(StringComparer.Ordinal);

        private readonly List<string> _missingRequired = new List<string>();
        private readonly List<string> _missingOptional = new List<string>();

        public GameFacade(Assembly game)
        {
            if (game == null) throw new ArgumentNullException("game");
            GameAssembly = game;

            foreach (var requirement in ReflectionRequirements.All)
            {
                MemberInfo member;
                string problem;
                if (ReflectionRequirements.TryResolve(game, requirement, out member, out problem))
                {
                    _resolved[Key(requirement.Type, requirement.Member)] = member;
                }
                else if (requirement.Required)
                {
                    _missingRequired.Add(problem);
                }
                else
                {
                    _missingOptional.Add(problem);
                }
            }
        }

        public Assembly GameAssembly { get; private set; }

        private static string Key(string type, string member)
        {
            return type + "|" + member;
        }

        public bool Available { get { return _missingRequired.Count == 0; } }

        /// <summary>必需成员里缺失的部分；全部可用返回 null。</summary>
        public string MissingRequired
        {
            get
            {
                return _missingRequired.Count == 0
                    ? null
                    : "以下必需成员在本机 Terraria 里没找到：" + string.Join("、", _missingRequired.ToArray());
            }
        }

        /// <summary>可选成员里缺失的部分，只写日志用。</summary>
        public string MissingOptional
        {
            get
            {
                return _missingOptional.Count == 0
                    ? null
                    : "以下可选成员没找到（相关功能会退化）：" + string.Join("、", _missingOptional.ToArray());
            }
        }

        private MemberInfo Find(string type, string member)
        {
            MemberInfo value;
            return _resolved.TryGetValue(Key(type, member), out value) ? value : null;
        }

        // ------------------------------------------------------------ 取值

        private static object Read(object instance, MemberInfo member)
        {
            if (member == null) return null;
            try
            {
                var field = member as FieldInfo;
                if (field != null) return field.GetValue(field.IsStatic ? null : instance);
                var property = member as PropertyInfo;
                return property == null ? null : property.GetValue(instance, null);
            }
            catch (Exception exception)
            {
                // 游戏正在切世界时对象可能被换掉，读不到就当作默认值。
                // 这里绝不能抛：异常会顺着钩子钻进 Terraria 的更新循环。
                //
                // 但**吞掉异常不等于没问题**：「读取抛异常」和「值本来就是 0」
                // 在调用方看起来一模一样，成因却完全不同。所以每次都记一笔，
                // 让日志能把这两种情况区分开——我正是在这里丢掉了一次关键线索。
                LastReadError = (member.DeclaringType == null ? "" : member.DeclaringType.Name + ".") +
                                member.Name + "：" + exception.GetType().Name + " " + exception.Message;
                LastReadException = exception;
                return null;
            }
        }

        /// <summary>
        /// 最近一次「读取抛异常」的完整异常对象。
        ///
        /// 光有 `Message` 往往不够——真正有指向性的是 `InnerException` 与堆栈，
        /// 而 `TargetInvocationException` 会把原始异常包在里面，消息本身通常毫无信息量
        /// （比如「Exception has been thrown by the target of an invocation.」）。
        /// </summary>
        public static Exception LastReadException;

        /// <summary>
        /// 最近一次「读取抛异常」的成员与原因；没有则为 null。
        ///
        /// 注意它区分的是「读不到」而不是「读到 0」——别把两者混为一谈。
        /// </summary>
        public static string LastReadError;

        private static void Write(object instance, MemberInfo member, object value)
        {
            if (member == null) return;
            try
            {
                var field = member as FieldInfo;
                if (field != null) field.SetValue(field.IsStatic ? null : instance, value);
            }
            catch (Exception)
            {
            }
        }

        private T Get<T>(string type, string member, T fallback)
        {
            var value = Read(null, Find(type, member));
            if (value == null) return fallback;
            try
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static bool BoolOf(object instance, MemberInfo member)
        {
            var value = Read(instance, member);
            return value != null && Convert.ToBoolean(value);
        }

        private static int IntOf(object instance, MemberInfo member, int fallback)
        {
            var value = Read(instance, member);
            if (value == null) return fallback;
            try
            {
                return Convert.ToInt32(value);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static float FloatOf(object instance, MemberInfo member, float fallback)
        {
            var value = Read(instance, member);
            if (value == null) return fallback;
            try
            {
                return Convert.ToSingle(value);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        // ------------------------------------------------------------ 全局状态

        public bool GameMenu { get { return BoolOf(null, Find("Terraria.Main", "gameMenu")); } }

        public int GameUpdateCount { get { return IntOf(null, Find("Terraria.Main", "GameUpdateCount"), 0); } }

        public bool NetModeIsClient { get { return IntOf(null, Find("Terraria.Main", "netMode"), 0) == 1; } }

        public int LocalPlayerIndex { get { return IntOf(null, Find("Terraria.Main", "myPlayer"), -1); } }

        public Array Players
        {
            get
            {
                var field = Find("Terraria.Main", "player") as FieldInfo;
                return field == null ? null : field.GetValue(null) as Array;
            }
        }

        public object LocalPlayer
        {
            get
            {
                var players = Players;
                var index = LocalPlayerIndex;
                if (players == null || index < 0 || index >= players.Length) return null;
                return players.GetValue(index);
            }
        }

        /// <summary>本地玩家处在「世界里、还活着、不在菜单」的可用状态。</summary>
        public bool LocalPlayerInWorld
        {
            get
            {
                var player = LocalPlayer;
                return player != null && PlayerActive(player) && !PlayerDead(player) && !PlayerGhost(player) && !GameMenu;
            }
        }

        public bool PlayerActive(object player) { return BoolOf(player, Find("Terraria.Player", "active")); }

        public bool PlayerDead(object player) { return BoolOf(player, Find("Terraria.Player", "dead")); }

        public bool PlayerGhost(object player) { return BoolOf(player, Find("Terraria.Player", "ghost")); }

        /// <summary>
        /// 玩家中心。`Player.Center` 是**基类 `Entity` 上的属性**，
        /// 所以这里先按属性查、再退到字段查，两条路都试。
        /// </summary>
        public Vector2Like PlayerCenter(object player)
        {
            if (player == null) return default(Vector2Like);
            var property = Find("Terraria.Player", "Center") as PropertyInfo;
            if (property != null) return Vector2Like.From(property.GetValue(player, null));
            var field = Find("Terraria.Entity", "Center") as FieldInfo;
            return field == null ? default(Vector2Like) : Vector2Like.From(field.GetValue(player));
        }

        // ------------------------------------------------------------ 物品栏

        /// <summary>`Player.inventory` 的长度（1.4.5.8 是 59）。</summary>
        public const int InventorySlotCount = 59;

        /// <summary>热键栏格数——玩家自己能按数字键直接切到的那一排。</summary>
        public const int HotbarSlotCount = 10;

        private Array Inventory(object player)
        {
            if (player == null) return null;
            var field = Find("Terraria.Player", "inventory") as FieldInfo;
            return field == null ? null : field.GetValue(player) as Array;
        }

        public int SelectedSlot(object player)
        {
            if (player == null) return 0;
            var state = Read(player, Find("Terraria.Player", "selectedItemState"));
            return state == null ? 0 : IntOf(state, Find("Terraria.Player+SelectedItemState", "selected"), 0);
        }

        /// <summary>手上那件物品的 ID；空手返回 0。</summary>
        public int SelectedItemType(object player)
        {
            var inventory = Inventory(player);
            if (inventory == null) return 0;
            var slot = SelectedSlot(player);
            if (slot < 0 || slot >= inventory.Length) return 0;
            return ItemType(inventory.GetValue(slot));
        }

        private int ItemType(object item)
        {
            if (item == null) return 0;
            if (IntOf(item, Find("Terraria.Item", "stack"), 0) <= 0) return 0;
            return IntOf(item, Find("Terraria.Item", "type"), 0);
        }

        public List<int> InventoryItemTypes(object player)
        {
            var types = new List<int>();
            var inventory = Inventory(player);
            if (inventory == null) return types;
            var limit = Math.Min(inventory.Length, InventorySlotCount);
            for (var i = 0; i < limit; i++) types.Add(ItemType(inventory.GetValue(i)));
            return types;
        }

        /// <summary>
        /// 换到指定槽位。
        ///
        /// 两处会「静默失效」的地方：
        /// 1. `selectedItemState` 是**结构体**字段。反射 `GetValue` 拿到的是副本，
        ///    在副本上调用 `Select` 只改副本，所以必须 `SetValue` 写回，否则不报错也没效果。
        /// 2. `Select` 的语义是**缓冲**：真正生效要等 `Player.Update` 里的
        ///    `selectedItemState.Update()`，而它又被「当前没在使用物品」挡着。
        ///    所以换手一定跨 tick，调用方必须重试。
        /// </summary>
        public bool SelectSlot(object player, int slot)
        {
            if (player == null || slot < 0 || slot >= InventorySlotCount) return false;
            var state = Read(player, Find("Terraria.Player", "selectedItemState"));
            if (state == null) return false;
            var method = Find("Terraria.Player+SelectedItemState", "Select") as MethodInfo;
            if (method == null) return false;
            try
            {
                method.Invoke(state, new object[] { slot });
            }
            catch (Exception)
            {
                return false;
            }
            Write(player, Find("Terraria.Player", "selectedItemState"), state);
            return true;
        }

        /// <summary>现在能不能立刻换手（`CanChangeSelectedItemImmediately`）。</summary>
        public bool CanChangeSelection(object player)
        {
            if (player == null) return false;
            var state = Read(player, Find("Terraria.Player", "selectedItemState"));
            if (state == null) return false;
            var property = Find("Terraria.Player+SelectedItemState", "CanChangeSelectedItemImmediately") as PropertyInfo;
            if (property == null) return true;
            var value = Read(state, property);
            return value == null || Convert.ToBoolean(value);
        }

        // ------------------------------------------------------------ 世界事件

        public bool BloodMoon { get { return BoolOf(null, Find("Terraria.Main", "bloodMoon")); } }

        public bool Eclipse { get { return BoolOf(null, Find("Terraria.Main", "eclipse")); } }

        public bool SnowMoon { get { return BoolOf(null, Find("Terraria.Main", "snowMoon")); } }

        public bool PumpkinMoon { get { return BoolOf(null, Find("Terraria.Main", "pumpkinMoon")); } }

        /// <summary>
        /// 沙尘暴的标志是 `Sandstorm.Happening`（`Terraria.GameContent.Events`），
        /// **不在 `Main` 上**——照直觉写成 `Main.sandstormHappening` 会一直读到 false。
        /// </summary>
        public bool Sandstorm { get { return BoolOf(null, Find("Terraria.GameContent.Events.Sandstorm", "Happening")); } }

        public int InvasionType { get { return IntOf(null, Find("Terraria.Main", "invasionType"), 0); } }

        public bool BossAlive
        {
            get
            {
                var field = Find("Terraria.Main", "npc") as FieldInfo;
                var npcs = field == null ? null : field.GetValue(null) as Array;
                if (npcs == null) return false;
                var active = Find("Terraria.NPC", "active");
                var boss = Find("Terraria.NPC", "boss");
                for (var i = 0; i < npcs.Length; i++)
                {
                    var npc = npcs.GetValue(i);
                    if (npc != null && BoolOf(npc, active) && BoolOf(npc, boss)) return true;
                }
                return false;
            }
        }

        /// <summary>会打断演奏的事件；没有则返回 null。顺序按「离玩家多近」排。</summary>
        public string WorldEventReason()
        {
            if (BossAlive) return "Boss 出现";
            if (InvasionType > 0) return "入侵事件";
            if (PumpkinMoon) return "南瓜月";
            if (SnowMoon) return "霜月";
            if (Eclipse) return "日食";
            if (BloodMoon) return "血月";
            if (Sandstorm) return "沙尘暴";
            return null;
        }

        // ------------------------------------------------------------ 音高与发声

        public int MusicNotes { get { return IntOf(null, Find("Terraria.Player", "musicNotes"), 6); } }

        public float MusicPitch
        {
            get { return FloatOf(null, Find("Terraria.Main", "musicPitch"), 0f); }
            set { Write(null, Find("Terraria.Main", "musicPitch"), value); }
        }

        /// <summary>
        /// 写 `Player.musicDist`。
        ///
        /// 这个字段**不影响发声**，只被原版用来画光标附近的音高指示，
        /// 写它纯粹是为了让指示和实际发出的音一致。
        /// </summary>
        public void SetMusicDistance(object player, float value)
        {
            if (player == null) return;
            Write(player, Find("Terraria.Player", "musicDist"), value);
        }

        /// <summary>
        /// 用于把「光标到玩家的距离」归一化的屏幕短边。
        ///
        /// 这是原版 `ItemCheck_PlayInstruments` 里 `d /= Main.Camera.SmallerScaledAxis / 2`
        /// 的那一项。首选路径是原版属性 `Main.Camera.SmallerScaledAxis`。
        ///
        /// 实测（1.4.5.8，窗口 1706x1066，缩放 1.0）：这个属性**每次读都返回 0**
        /// （它内部要读 `GraphicsDevice.Viewport` 重建视图矩阵，那条路在这里走不通）。
        /// 拿到 0 的后果非常隐蔽：距离全被归一化成 1，整首曲子只剩一个音高，
        /// 而节奏、音符数、日志全都正常——听起来就像「音源本身没有音高变化」。
        ///
        /// 所以必须有回退。这里用 `最小(屏幕宽, 屏幕高)`：
        /// `ScaledSize = 屏幕尺寸 - 视图矩阵平移*2`，而本机缩放是 1.0、平移为 0，
        /// 于是回退值与 `ScaledSize` 完全相等（实测 1066 == min(1706,1066)）。
        /// </summary>
        public float SmallerScaledAxis
        {
            get
            {
                var cameraField = Find("Terraria.Main", "Camera") as FieldInfo;
                var camera = cameraField == null ? null : cameraField.GetValue(null);
                if (camera != null)
                {
                    var fromCamera = FloatOf(camera, Find("Terraria.Graphics.Camera", "SmallerScaledAxis"), 0f);
                    if (fromCamera > 1f) return fromCamera;
                }
                return FallbackScaledAxis;
            }
        }

        /// <summary>回退算法：屏幕宽高取小者。与缩放 1.0、平移 0 时的 `ScaledSize` 等价。</summary>
        private float FallbackScaledAxis
        {
            get
            {
                var width = IntOf(null, Find("Terraria.Main", "screenWidth"), 0);
                var height = IntOf(null, Find("Terraria.Main", "screenHeight"), 0);
                if (width <= 0 || height <= 0) return 0f;
                return Math.Min(width, height);
            }
        }

        /// <summary>
        /// 诊断用：把轴长的来源写清楚。
        ///
        /// 这个方法是「轴长 = 0」这次事故留下的产物。当时的日志只写了「轴长 0.0」，
        /// 既看不出是「读失败」还是「值本来就是 0」，也看不出回退读到了什么，
        /// 只能靠一轮轮启动游戏去猜。现在一次就把三条信息都摆出来。
        /// </summary>
        public string DescribeAxis()
        {
            var cameraField = Find("Terraria.Main", "Camera") as FieldInfo;
            var camera = cameraField == null ? null : cameraField.GetValue(null);
            string fromCamera;
            if (camera == null)
            {
                fromCamera = "无相机";
            }
            else
            {
                var before = LastReadException;
                var raw = FloatOf(camera, Find("Terraria.Graphics.Camera", "SmallerScaledAxis"), 0f);
                fromCamera = raw.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                if (!ReferenceEquals(before, LastReadException) && LastReadException != null)
                    fromCamera += "（抛 " + RootCause(LastReadException).GetType().Name + "：" +
                                  RootCause(LastReadException).Message + "）";
            }

            var width = IntOf(null, Find("Terraria.Main", "screenWidth"), 0);
            var height = IntOf(null, Find("Terraria.Main", "screenHeight"), 0);
            return "轴长 " + SmallerScaledAxis.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
                   "（原版属性 " + fromCamera + " · 屏幕 " + width + "x" + height + "）";
        }

        /// <summary>
        /// 剥掉 `TargetInvocationException` 这层壳。
        ///
        /// 反射调用抛出的原始异常一定被包在里面，而外壳的消息是固定的套话
        /// 「Exception has been thrown by the target of an invocation」，
        /// 直接打出来等于没打。
        /// </summary>
        private static Exception RootCause(Exception exception)
        {
            var target = exception as TargetInvocationException;
            return target != null && target.InnerException != null ? target.InnerException : exception;
        }

        /// <summary>
        /// 复刻原版 `ItemCheck_PlayInstruments` 的音高计算，输入是「玩家中心到光标的**世界**距离」。
        ///
        /// 原版先按屏幕短边归一化并截断到 1，再映射到 `[-1, 1]`，取整后除以 `musicNotes`。
        /// 注意 `Math.Round` 用的是银行家舍入：正好落在中点上时偶数优先，
        /// 所以调用方不应该把距离恰好放在中点上（要略微偏一点）。
        /// </summary>
        public static float PitchFromWorldDistance(float worldDistance, float smallerScaledAxis, int musicNotes)
        {
            if (smallerScaledAxis <= 0f || musicNotes <= 0) return 0f;
            var normalized = worldDistance / (smallerScaledAxis / 2f);
            if (normalized > 1f) normalized = 1f;
            normalized = normalized * 2f - 1f;
            if (normalized < -1f) normalized = -1f;
            if (normalized > 1f) normalized = 1f;
            var steps = (float)Math.Round(normalized * musicNotes);
            return steps / musicNotes;
        }

        /// <summary>
        /// 弹一个音。完全替代原版那段发声代码——**不碰光标、不碰鼠标按键、不碰物品动画**。
        ///
        /// 三条路径照抄反编译结果：
        /// 竖琴 508 / 铃铛 507 用 `SoundID.Item26` / `Item35`（音高取 `musicPitch`）；
        /// 吉他斧 1305 用 `SoundID.Item47`；
        /// 常春藤 4372 / 雨歌 4057 / 星星吉他 4715 走 `PlayGuitarChord`，音高由和弦决定；
        /// 鼓 4673 走 `PlayDrums`。
        /// </summary>
        public void PlayNote(object player, int itemId, float normalizedDistance, float musicPitch)
        {
            if (player == null) return;

            var soundId = 0;
            switch (itemId)
            {
                case 508: soundId = 26; MusicPitch = musicPitch; break;
                case 507: soundId = 35; MusicPitch = musicPitch; break;
                case 1305: soundId = 47; MusicPitch = musicPitch; break;
                case 4372:
                case 4057:
                case 4715:
                    InvokeChord("PlayGuitarChord", player, normalizedDistance);
                    break;
                case 4673:
                    InvokeChord("PlayDrums", player, normalizedDistance);
                    break;
                default:
                    return;
            }

            if (soundId != 0)
            {
                PlaySound(soundId, PlayerCenter(player));
                // 单音乐器同步的是 `musicPitch`，拨弦乐器同步的是归一化距离——
                // 与原版一致（竖琴/铃铛发 num9，吉他与鼓发 num12 / num15 / num20）。
                SendPitch(player, soundId == 26 || soundId == 35 ? musicPitch : normalizedDistance);
            }

            var notify = Find("Terraria.GameContent.Achievements.AchievementsHelper", "NotifyProgressionEvent") as MethodInfo;
            if (notify != null)
            {
                try
                {
                    notify.Invoke(null, new object[] { ProgressionEventInstrument });
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>原版 `ItemCheck_PlayInstruments` 末尾上报的进度事件编号（弹奏乐器）。</summary>
        private const int ProgressionEventInstrument = 37;

        /// <summary>`NetMessage.SendData` 里同步音高用的消息编号。</summary>
        private const int PitchMessageType = 58;

        private void InvokeChord(string methodName, object player, float normalizedDistance)
        {
            var method = Find("Terraria.Player", methodName) as MethodInfo;
            if (method == null) return;
            try
            {
                method.Invoke(player, new object[] { normalizedDistance });
            }
            catch (Exception)
            {
            }
        }

        private void PlaySound(int soundId, Vector2Like position)
        {
            var method = Find("Terraria.Audio.SoundEngine", "PlaySound") as MethodInfo;
            if (method == null) return;
            // 位置参数必须是真正的 XNA `Vector2`——原版方法不接受替身类型。
            var vector = position.ToGameVector();
            if (vector == null) return;
            try
            {
                method.Invoke(null, new object[] { soundId, vector, 1, 0f });
            }
            catch (Exception)
            {
            }
        }

        /// <summary>联机时把音高告诉其他客户端。单机下原版也会发，这里保持一致。</summary>
        private void SendPitch(object player, float musicPitch)
        {
            var method = Find("Terraria.NetMessage", "SendData") as MethodInfo;
            if (method == null) return;
            var whoAmI = IntOf(player, Find("Terraria.Entity", "whoAmI"), -1);
            if (whoAmI < 0) return;
            try
            {
                method.Invoke(null, new object[] { PitchMessageType, -1, -1, null, whoAmI, musicPitch });
            }
            catch (Exception)
            {
            }
        }

        // ------------------------------------------------------------ 诊断

        /// <summary>
        /// 自检。清单只有一份（`ReflectionRequirements`），构建期的离线核对工具读的是同一份，
        /// 所以这里通过就等于那边通过——不会再出现「工具说没问题、游戏里说有问题」这种互相打架。
        /// </summary>
        public string Validate()
        {
            return MissingRequired;
        }

        /// <summary>给日志用：把解析结果写成一行，方便对照版本差异。</summary>
        public string DescribeResolution()
        {
            var builder = new StringBuilder();
            builder.Append("成员解析 ").Append(_resolved.Count).Append('/')
                   .Append(ReflectionRequirements.All.Count).Append(" 项通过");
            if (_missingOptional.Count > 0)
                builder.Append("；可选 ").Append(_missingOptional.Count).Append(" 项缺失");
            return builder.ToString();
        }
    }

    /// <summary>避免注入层直接依赖 XNA 的结构体类型；只带两个 float。</summary>
    public struct Vector2Like
    {
        public float X;
        public float Y;

        public static Vector2Like From(object vector)
        {
            if (vector == null) return default(Vector2Like);
            var type = vector.GetType();
            var x = type.GetField("X");
            var y = type.GetField("Y");
            if (x == null || y == null) return default(Vector2Like);
            return new Vector2Like { X = Convert.ToSingle(x.GetValue(vector)), Y = Convert.ToSingle(y.GetValue(vector)) };
        }

        public float Length { get { return (float)Math.Sqrt(X * X + Y * Y); } }

        /// <summary>造一个真正的 XNA `Vector2`。调用原版方法时必须给真类型，不能给这个替身。</summary>
        public object ToGameVector()
        {
            var type = Type.GetType("Microsoft.Xna.Framework.Vector2, Microsoft.Xna.Framework", false);
            if (type == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = assembly.GetType("Microsoft.Xna.Framework.Vector2", false);
                    if (type != null) break;
                }
            }
            if (type == null) return null;
            return Activator.CreateInstance(type, new object[] { X, Y });
        }
    }
}
