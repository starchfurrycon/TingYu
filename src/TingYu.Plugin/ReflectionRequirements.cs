using System;
using System.Collections.Generic;
using System.Reflection;

namespace TingYu.Plugin
{
    /// <summary>成员的种类。反射里「字段」和「属性」是两条完全不同的路，写错就静默拿不到值。</summary>
    public enum MemberKind
    {
        Field,
        Property,
        Method
    }

    /// <summary>
    /// 注入层需要的每一个游戏成员，**只在这一处声明**。
    ///
    /// 为什么要单独抽出来：这份清单的消费方有两个——运行时的 `GameFacade.Validate()`
    /// 与构建期的离线核对工具。之前它们是两份各写各的列表，结果互相打架：
    /// 工具说「全部通过」，插件却在游戏里报「反射自检失败」。
    /// 现在工具直接读这份清单，两边不可能再不一致。
    ///
    /// `Required = false` 的项缺了不算致命（少一点功能），但都会出现在日志里。
    /// </summary>
    public static class ReflectionRequirements
    {
        public sealed class Requirement
        {
            public string Type;
            public string Member;
            public MemberKind Kind;
            public bool Required;

            /// <summary>方法用到的参数类型全名；字段与属性留空。</summary>
            public string[] Parameters;

            /// <summary>给日志与核对输出用的说明。</summary>
            public string Note;

            public string Display
            {
                get { return Type + "." + Member + "（" + KindName(Kind) + "）"; }
            }

            public static string KindName(MemberKind kind)
            {
                switch (kind)
                {
                    case MemberKind.Field: return "字段";
                    case MemberKind.Property: return "属性";
                    default: return "方法";
                }
            }
        }

        public static IReadOnlyList<Requirement> All
        {
            get { return Requirements; }
        }

        /// <summary>
        /// 有一条要特别注意：**继承来的成员必须靠 `FlattenHierarchy` 才能找到**。
        /// `Player.Center` 与 `Player.whoAmI` 都定义在基类 `Terraria.Entity` 上，
        /// 只查 `Player` 会得出「不存在」的错误结论。
        /// </summary>
        private static readonly Requirement[] Requirements =
        {
            // ---------------------------------------------------------- Main（静态）
            Field("Terraria.Main", "player", true, "全部玩家"),
            Field("Terraria.Main", "npc", true, "全部 NPC"),
            Field("Terraria.Main", "myPlayer", true, "本地玩家下标"),
            Field("Terraria.Main", "Camera", true, "相机（屏幕缩放轴长从这里读）"),
            Field("Terraria.Main", "gameMenu", true, "是否停在菜单"),
            Field("Terraria.Main", "musicPitch", true, "当前音高（用于验证发声）"),
            Field("Terraria.Main", "screenPosition", false, "世界↔屏幕坐标换算"),
            Field("Terraria.Main", "netMode", false, "单机/联机判定"),
            Field("Terraria.Main", "bloodMoon", false, "血月"),
            Field("Terraria.Main", "eclipse", false, "日食"),
            Field("Terraria.Main", "snowMoon", false, "霜月"),
            Field("Terraria.Main", "pumpkinMoon", false, "南瓜月"),
            Field("Terraria.Main", "invasionType", false, "入侵事件"),
            Property("Terraria.Main", "GameUpdateCount", false, "每逻辑帧自增，用于热键节流"),
            Field("Terraria.Main", "screenWidth", false, "屏幕宽（轴长回退用）"),
            Field("Terraria.Main", "screenHeight", false, "屏幕高（轴长回退用）"),

            // ---------------------------------------------------------- Player
            Field("Terraria.Player", "active", true, "玩家是否激活"),
            Field("Terraria.Player", "dead", true, "是否死亡"),
            Field("Terraria.Player", "ghost", false, "是否处于鬼魂状态"),
            Field("Terraria.Player", "inventory", true, "物品栏"),
            Field("Terraria.Player", "selectedItemState", true, "选中的物品栏格（结构体）"),
            Field("Terraria.Player", "musicDist", false, "光标距离（显示用）"),
            Property("Terraria.Player", "Center", true, "玩家中心（定义在基类 Entity 上）"),
            Field("Terraria.Entity", "whoAmI", false, "玩家下标（联机同步用，定义在基类上）"),
            Field("Terraria.Player", "musicNotes", false, "音高档数（常量 6）"),

            // ---------------------------------------------------------- 嵌套结构 SelectedItemState
            Field("Terraria.Player+SelectedItemState", "selected", true, "当前选中的格子索引"),
            Property("Terraria.Player+SelectedItemState", "CanChangeSelectedItemImmediately", false, "现在能不能换手"),
            Method("Terraria.Player+SelectedItemState", "Select", true, "换手（只缓冲，下一帧才生效）",
                "System.Int32"),

            // ---------------------------------------------------------- Item
            Field("Terraria.Item", "type", true, "物品 ID"),
            Field("Terraria.Item", "stack", true, "堆叠数量"),

            // ---------------------------------------------------------- NPC
            Field("Terraria.NPC", "active", true, "NPC 是否激活"),
            Field("Terraria.NPC", "boss", false, "是否 Boss"),

            // ---------------------------------------------------------- 世界事件
            Field("Terraria.GameContent.Events.Sandstorm", "Happening", false, "沙尘暴是否发生"),

            // ---------------------------------------------------------- 发声
            Method("Terraria.Player", "PlayGuitarChord", false, "吉他和弦（雨歌 / 常春藤 / 星星吉他走这里）",
                "System.Single"),
            Method("Terraria.Player", "PlayDrums", false, "鼓", "System.Single"),
            // 参数写全：`PlaySound` 有四个重载，其中两个的第一个参数都是 int，
            // 只有靠完整的参数类型表才能唯一定位到「int, Vector2, int, float」这一个。
            Method("Terraria.Audio.SoundEngine", "PlaySound", false, "播声音（参数含 XNA Vector2，按名字延迟绑定）",
                "System.Int32", "Microsoft.Xna.Framework.Vector2", "System.Int32", "System.Single"),
            Method("Terraria.NetMessage", "SendData", false, "联机同步音高",
                "System.Int32", "System.Int32", "System.Int32", "Terraria.Localization.NetworkText",
                "System.Int32", "System.Single"),
            Method("Terraria.GameContent.Achievements.AchievementsHelper", "NotifyProgressionEvent", false,
                "进度事件（弹奏乐器）", "System.Int32")
        };

        private static Requirement Field(string type, string member, bool required, string note)
        {
            return new Requirement { Type = type, Member = member, Kind = MemberKind.Field, Required = required, Note = note };
        }

        private static Requirement Property(string type, string member, bool required, string note)
        {
            return new Requirement { Type = type, Member = member, Kind = MemberKind.Property, Required = required, Note = note };
        }

        /// <summary>
        /// 声明一条方法需求。
        ///
        /// **参数顺序是坑**：`note` 必须排在 `params string[] parameters` **之前**，
        /// 否则调用方写成 `Method(..., "说明文字", "System.Int32")` 时，
        /// 那段说明文字会被当成第一个参数类型，于是匹配必定失败。
        /// 这个错误的表现是「所有方法都报参数不匹配」，排查起来很绕。
        /// </summary>
        private static Requirement Method(string type, string member, bool required, string note, params string[] parameters)
        {
            return new Requirement
            {
                Type = type,
                Member = member,
                Kind = MemberKind.Method,
                Required = required,
                Parameters = parameters ?? new string[0],
                Note = note
            };
        }

        /// <summary>
        /// 在给定程序集里解析一条需求。找不到就把原因（以及「它其实是另一种成员」这种线索）写进 `problem`。
        ///
        /// `+` 分隔嵌套类型，与反射里 `Type.GetType` 的写法一致。
        /// </summary>
        public static bool TryResolve(Assembly game, Requirement requirement, out MemberInfo member, out string problem)
        {
            member = null;
            problem = null;

            var type = FindType(game, requirement.Type);
            if (type == null)
            {
                problem = requirement.Display + " 所在的类型不存在";
                return false;
            }

            if (requirement.Kind == MemberKind.Method)
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                       BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                {
                    if (method.Name != requirement.Member) continue;
                    var parameters = method.GetParameters();
                    if (parameters.Length < requirement.Parameters.Length) continue;

                    var matched = true;
                    for (var i = 0; i < requirement.Parameters.Length && matched; i++)
                        matched = parameters[i].ParameterType.FullName == requirement.Parameters[i];
                    if (!matched) continue;

                    // 剩下的参数必须都是可选的，否则调用时会缺参数。
                    for (var i = requirement.Parameters.Length; i < parameters.Length && matched; i++)
                        matched = parameters[i].IsOptional;
                    if (!matched) continue;

                    member = method;
                    return true;
                }
                problem = requirement.Display + " 没有匹配的重载（参数 " + string.Join(", ", requirement.Parameters) + "）";
                return false;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;

            if (requirement.Kind == MemberKind.Field)
            {
                var field = type.GetField(requirement.Member, flags);
                if (field != null) { member = field; return true; }

                var asProperty = type.GetProperty(requirement.Member, flags);
                problem = requirement.Display + " 不存在" +
                          (asProperty != null ? "，但存在同名属性，应声明为 Property" : "，也不存在同名属性");
                return false;
            }

            var property2 = type.GetProperty(requirement.Member, flags);
            if (property2 != null)
            {
                if (property2.GetGetMethod(true) == null)
                {
                    problem = requirement.Display + " 没有 getter";
                    return false;
                }
                member = property2;
                return true;
            }

            var asField = type.GetField(requirement.Member, flags);
            problem = requirement.Display + " 不存在" +
                      (asField != null ? "，但存在同名字段，应声明为 Field" : "，也不存在同名字段");
            return false;
        }

        /// <summary>找类型。`Outer+Inner` 表示嵌套类型。</summary>
        public static Type FindType(Assembly game, string name)
        {
            var type = game.GetType(name, false);
            if (type != null) return type;

            var separator = name.IndexOf('+');
            if (separator < 0) return null;
            var outer = game.GetType(name.Substring(0, separator), false);
            if (outer == null) return null;
            return outer.GetNestedType(name.Substring(separator + 1), BindingFlags.Public | BindingFlags.NonPublic);
        }
    }
}
