using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace TingYu.Tools
{
    /// <summary>
    /// 向游戏窗口投递按键的小工具。
    ///
    /// 为什么不用 PowerShell 的 `SendKeys`：它依赖当前线程的输入队列与窗口焦点，
    /// 而 `SetForegroundWindow` 在后台进程里经常被前台锁定规则拒绝，
    /// 结果就是「键发出去了、游戏没收到」。这里用 `AttachThreadInput`
    /// 把自己的输入队列挂到目标线程上再设前台，成功率明显更高。
    ///
    /// 按键本身走 `keybd_event` 而不是 `PostMessage(WM_KEYDOWN)`：
    /// FNA 的输入走键盘状态轮询（`Keyboard.GetState`），不发真实扫描码就读不到。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("用法: TingYu.Tools focus|keys <进程Id> [按键...]");
                Console.Error.WriteLine("按键: ENTER UP DOWN ESC LEFT RIGHT TAB SPACE F1..F12 或字母");
                return 2;
            }

            var command = args[0].ToLowerInvariant();
            int processId;
            if (!int.TryParse(args[1], out processId))
            {
                Console.Error.WriteLine("进程 Id 无效: " + args[1]);
                return 2;
            }

            Process process;
            try
            {
                process = Process.GetProcessById(processId);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("找不到进程 " + processId + ": " + exception.Message);
                return 3;
            }

            var window = WaitForWindow(process, 30000);
            if (window == IntPtr.Zero)
            {
                Console.Error.WriteLine("进程 " + processId + " 没有主窗口。");
                return 4;
            }

            var focused = Focus(window);
            Console.WriteLine("window=0x" + window.ToString("X") + " focused=" + focused);
            if (!focused) Thread.Sleep(400);

            if (command == "focus") return 0;

            for (var i = 2; i < args.Length; i++)
            {
                var key = args[i];
                if (key.StartsWith("sleep:", StringComparison.OrdinalIgnoreCase))
                {
                    int milliseconds;
                    if (int.TryParse(key.Substring(6), out milliseconds)) Thread.Sleep(milliseconds);
                    continue;
                }

                var virtualKey = ParseKey(key);
                if (virtualKey == 0)
                {
                    Console.Error.WriteLine("不认识的按键: " + key);
                    return 5;
                }
                Tap(virtualKey);
                Console.WriteLine("sent " + key);
            }

            return 0;
        }

        private static IntPtr WaitForWindow(Process process, int timeoutMs)
        {
            var deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                process.Refresh();
                if (process.HasExited) return IntPtr.Zero;
                if (process.MainWindowHandle != IntPtr.Zero) return process.MainWindowHandle;
                Thread.Sleep(300);
            }
            return IntPtr.Zero;
        }

        private static bool Focus(IntPtr window)
        {
            var targetThread = GetWindowThreadProcessId(window, IntPtr.Zero);
            var currentThread = GetCurrentThreadId();
            var attached = false;
            try
            {
                if (targetThread != currentThread)
                    attached = AttachThreadInput(currentThread, targetThread, true);

                ShowWindow(window, ShowWindowRestore);
                var result = SetForegroundWindow(window);
                SetActiveWindow(window);
                SetFocus(window);
                if (result) return true;
                return GetForegroundWindow() == window;
            }
            finally
            {
                if (attached) AttachThreadInput(currentThread, targetThread, false);
            }
        }

        private static void Tap(int virtualKey)
        {
            var scan = MapVirtualKey((uint)virtualKey, 0);
            keybd_event((byte)virtualKey, (byte)scan, 0, UIntPtr.Zero);
            Thread.Sleep(60);
            keybd_event((byte)virtualKey, (byte)scan, KeyEventKeyUp, UIntPtr.Zero);
            Thread.Sleep(120);
        }

        private static int ParseKey(string key)
        {
            var upper = key.ToUpperInvariant();
            if (upper.StartsWith("VK", StringComparison.Ordinal) && upper.Length > 2)
            {
                int value;
                if (int.TryParse(upper.Substring(2), out value)) return value;
            }
            if (upper.Length == 1 && upper[0] >= 'A' && upper[0] <= 'Z') return upper[0];
            if (upper.Length == 1 && upper[0] >= '0' && upper[0] <= '9') return upper[0];
            switch (upper)
            {
                case "ENTER": case "RETURN": return 0x0D;
                case "ESC": case "ESCAPE": return 0x1B;
                case "SPACE": return 0x20;
                case "TAB": return 0x09;
                case "UP": return 0x26;
                case "DOWN": return 0x28;
                case "LEFT": return 0x25;
                case "RIGHT": return 0x27;
                case "SHIFT": return 0x10;
                case "CONTROL": case "CTRL": return 0x11;
                case "BACK": case "BACKSPACE": return 0x08;
            }
            if (upper.Length >= 2 && upper[0] == 'F')
            {
                int index;
                if (int.TryParse(upper.Substring(1), out index) && index >= 1 && index <= 24)
                    return 0x70 + index - 1;
            }
            return 0;
        }

        private const int ShowWindowRestore = 9;
        private const uint KeyEventKeyUp = 0x0002;

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachFlag);
        [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll")] private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    }
}
