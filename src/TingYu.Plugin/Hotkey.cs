using System;
using System.Runtime.InteropServices;

namespace TingYu.Plugin
{
    /// <summary>
    /// 全局热键轮询。
    ///
    /// 用 `GetAsyncKeyState` 而不是 XNA 的 `Keyboard.GetState`：前者不要求窗口在前台，
    /// 也不会被游戏自己的输入配置（比如玩家把键改了）影响；后者在失去焦点后读不到任何键。
    ///
    /// 只在游戏窗口是前台窗口时才认，避免玩家在别的程序里打字时误触发接管。
    /// </summary>
    public sealed class Hotkey
    {
        /// <summary>
        /// 默认接管键：F10。
        ///
        /// 不用 F8 的原因很具体：Terraria 1.4.5 本体把 F8 绑给了网络统计浮层
        /// （屏幕上会出现一个以 bytes 计的统计面板），按下去会弹出那个界面，
        /// 和接管撞车。F10 在 1.4.5.8 的默认按键表里是空的。
        /// </summary>
        public const int DefaultVirtualKey = 0x79; // F10

        private readonly int _virtualKey;
        private bool _wasDown;
        private readonly uint _processId = GetCurrentProcessId();

        public Hotkey(int virtualKey)
        {
            _virtualKey = virtualKey <= 0 ? DefaultVirtualKey : virtualKey;
        }

        public int VirtualKey { get { return _virtualKey; } }

        public string DisplayName
        {
            get
            {
                if (_virtualKey >= 0x70 && _virtualKey <= 0x87) return "F" + (_virtualKey - 0x70 + 1);
                if (_virtualKey >= 0x41 && _virtualKey <= 0x5A) return ((char)_virtualKey).ToString();
                if (_virtualKey >= 0x30 && _virtualKey <= 0x39) return ((char)_virtualKey).ToString();
                return "0x" + _virtualKey.ToString("X2");
            }
        }

        /// <summary>本 tick 是否出现了一次「刚按下」。前台判定也在这里做。</summary>
        public bool PollPressed()
        {
            var down = (GetAsyncKeyState(_virtualKey) & 0x8000) != 0;
            return Sample(IsGameForeground(), down);
        }

        /// <summary>纯边沿检测，便于离线测试传入按键状态。</summary>
        internal bool Sample(bool foreground, bool down)
        {
            var pressed = foreground && down && !_wasDown;
            _wasDown = down;
            return pressed;
        }

        private bool IsGameForeground()
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return false;
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            return owner == _processId;
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();
    }
}
