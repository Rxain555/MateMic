using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MateMic.Core;

namespace MateMic;

/// <summary>
/// 快捷键录入通路自检：<c>MateMic.exe --imecheck</c>
///
/// 为什么需要它：输入法拦截按键这个问题**反复复发过**（三轮修复）。根因是录入通路
/// 建立在"窗口能收到按键"这个假设上，而中文输入法（微软拼音等 TSF 输入法）在组词时
/// 会把字母键**整个吃掉**——窗口连 <c>WM_KEYDOWN</c> 都收不到，只收到一条
/// <c>WM_IME_COMPOSITION</c>。现在录入改由 <see cref="ShortcutKeyCapture"/> 的
/// <c>WH_KEYBOARD_LL</c> 低层键盘钩子完成，它在输入法之前拿到物理按键。
///
/// 本自检复现的正是那个失败场景（中文输入法 + 连续字母键），但不需要人手操作：
///   1. 建一个隐藏窗口，挂上**真正的** <see cref="ShortcutKeyCapture"/>；
///   2. 用 SendInput 合成 <c>N I H A O 1 空格 \</c>；
///   3. 断言每一个键都被录成正确的按键名。
///
/// 只要有人把录入改回 WPF 事件、或者把低层钩子去掉，这里立刻会红。
/// 退出码：0 = 通过，1 = 失败。
/// </summary>
public static class ImeDiagnostics
{
    /// <summary>
    /// 待测按键。
    ///
    /// ⚠ 必须包含**字母**：F 系列功能键根本不会触发中文输入法组词，
    /// 只用它们测等于什么都没测（这正是这个自检存在的意义所在）。
    ///
    /// 安全性：自检窗口里放了一个真实的文本框并让它获得焦点（见 <see cref="Run"/>），
    /// 所以万一录入通路失效、按键漏了出去，也只会落进这个自检窗口的文本框里，
    /// 不会跑到用户的记事本 / 聊天窗口去。
    /// </summary>
    private static readonly (ushort Vk, string Expect)[] Cases =
    {
        // 字母：中文输入法会尝试组词，是核心用例
        (0x4E, "N"),
        (0x49, "I"),
        (0x48, "H"),
        (0x41, "A"),
        (0x4F, "O"),
        // 数字与空格
        (0x31, "1"),
        (0x20, "空格"),
        // 功能键：确认非字符键不受影响
        (0x7C, "F13"),
        (0x83, "F20"),
        // 符号键：VK_OEM_5 反斜杠。中文输入法会把它变成顿号，
        // 是"字符键被输入法接管"的典型受害者，历史上最容易出问题的一个。
        (0xDC, "\\"),
    };

    private const uint INPUT_KEYBOARD = 1;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_IME_COMPOSITION = 0x010F;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL, wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    /// <summary>
    /// 自检结果同时写到控制台：从命令行直接运行时能马上看到结论，
    /// 不用去翻 data\logs 里的日志文件。
    /// </summary>
    private static void Report(string message)
    {
        Log.Info(message);
        Console.WriteLine(message);
    }

    /// <summary>输入法把按键变成伪键（VK_PROCESSEDKEY）的次数，正常应该为 0。</summary>
    private static int _processedKeyCount;

    /// <summary>
    /// 禁止注入的虚拟键：Win、电源/睡眠、浏览器功能键、输入法伪键。
    /// 自检只验证按键通路，绝不能产生"弹出开始菜单"这类全局副作用。
    /// </summary>
    private static bool IsForbiddenKey(ushort vk) => vk is
        0x5B or 0x5C or          // LWin / RWin —— 会打开开始菜单
        0x5D or                  // Apps 菜单键
        0x5E or 0x5F or          // 电源 / 睡眠
        0x65 or 0x66 or 0x67 or  // 浏览器搜索 / 收藏 / 主页
        0xE5;                    // VK_PROCESSEDKEY（输入法伪键）

    public static void Run()
    {
        // 文本框是故意放的：万一录入通路失效、按键漏了出去，也只会落进这里，
        // 不会跑到用户的记事本/聊天窗口里。同时它也是一个"泄漏探测器"。
        var leakBox = new TextBox
        {
            Margin = new Thickness(12, 8, 12, 0),
            MinHeight = 24,
            IsReadOnly = true,
            Text = string.Empty,
        };

        var window = new Window
        {
            Title = "MateMic 快捷键录入自检",
            Width = 420,
            Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            Content = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    new TextBlock
                    {
                        Text = "正在自检快捷键录入通路，请不要操作键盘。\n" +
                               "（下面这个输入框用来兜住可能漏出来的字符）",
                        Margin = new Thickness(12, 12, 12, 0),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    leakBox,
                },
            },
        };

        var capture = new ShortcutKeyCapture();
        var gestures = new List<string>();
        var frameHandle = IntPtr.Zero;

        // 必须在录入期间读：录完之后钩子会被卸载，属性自然变回 false
        var lowLevelHookWasActive = false;

        // 输入法是否真的介入了（组词或产生了字符）。用来证明"这次测试确实触碰了输入法"。
        var imeCompositionCount = 0;

        window.Loaded += (_, _) =>
        {
            frameHandle = new WindowInteropHelper(window).Handle;
            capture.Attach(frameHandle);

            // 让文本框拿到键盘焦点：万一有按键漏出去，也不会离开本窗口
            leakBox.Focus();
            Keyboard.Focus(leakBox);

            HwndSource.FromHwnd(frameHandle)?.AddHook((IntPtr _, int msg, IntPtr wParam,
                IntPtr _, ref bool handledFlag) =>
            {
                switch (msg)
                {
                    case WM_KEYDOWN:
                    case WM_SYSKEYDOWN:
                        if ((ushort)(wParam.ToInt64() & 0xFFFF) == 0xE5) _processedKeyCount++;
                        break;
                    case WM_IME_COMPOSITION:
                        imeCompositionCount++;
                        break;
                }

                return IntPtr.Zero;
            });
        };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        var index = -1;
        var focusFailures = 0;
        var started = false;
        ushort? pendingKeyUp = null;

        timer.Tick += (_, _) =>
        {
            // 自检进程是被脚本拉起来的后台进程，默认抢不到前台焦点；
            // 抢不到就没法把按键送进本窗口，必须重试。
            if (frameHandle != IntPtr.Zero && GetForegroundWindow() != frameHandle)
            {
                if (++focusFailures > 12)
                {
                    Finish(window, capture, gestures, leakBox.Text,
                        imeCompositionCount, focusFailure: true, lowLevelHookWasActive);
                    return;
                }

                ForceForeground(window, frameHandle);
                return;
            }

            // 只开始一次录入，并且 stopAfterFirst:false —— 连续录所有用例。
            // （界面上的正常用法是录一个键就结束；自检要一次把用例跑完。）
            if (!started)
            {
                started = true;
                capture.Begin(gesture => gestures.Add(gesture), stopAfterFirst: false);
                lowLevelHookWasActive = capture.IsLowLevelHookActive;
                Report($"[自检] 低层键盘钩子可用 = {lowLevelHookWasActive}");
            }

            // 每个 tick 只注入一个事件：先把上一个键抬起来，再按下当前键
            if (pendingKeyUp is { } up)
            {
                SendKey(up, down: false);
                pendingKeyUp = null;
            }

            index++;

            if (index >= Cases.Length)
            {
                timer.Stop();
                Finish(window, capture, gestures, leakBox.Text,
                    imeCompositionCount, focusFailure: false, lowLevelHookWasActive);
                return;
            }

            SendKey(Cases[index].Vk, down: true);
            pendingKeyUp = Cases[index].Vk;
        };

        timer.Start();
        window.ShowDialog();
        capture.Dispose();
    }

    private static void Finish(Window window, ShortcutKeyCapture capture, List<string> gestures,
        string leakedText, int imeCompositionCount, bool focusFailure, bool lowLevelHookWasActive)
    {
        capture.End();
        window.Close();

        AttachConsole(-1);
        Report("========== 快捷键录入通路自检 ==========");
        Report($"输入法布局：0x{GetKeyboardLayout(0).ToInt64():X8}");

        if (focusFailure)
        {
            Log.Error("[自检] 结果：无法获得前台焦点，自检作废（请在有交互桌面的环境下运行）。");
            Environment.ExitCode = 1;
            return;
        }

        var ok = true;

        if (!lowLevelHookWasActive)
        {
            // 低层钩子没装上时，字符键会被输入法吞掉——这正是要防的回归，必须报失败
            ok = false;
            Log.Error("[自检] 低层键盘钩子没有装上：中文输入法下字符键会重新被吞掉。");
        }

        if (gestures.Count != Cases.Length)
        {
            ok = false;
            Log.Error($"[自检] 收到 {gestures.Count} 个按键，期望 {Cases.Length} 个。");
        }

        for (var i = 0; i < Cases.Length; i++)
        {
            var (vk, expect) = Cases[i];
            var actual = i < gestures.Count ? gestures[i] : "（没收到）";
            var match = string.Equals(actual, expect, StringComparison.Ordinal);
            if (!match) ok = false;
            Report($"  [{(match ? "通过" : "失败")}] vk=0x{vk:X2} 期望「{expect}」实际「{actual}」");
        }

        if (_processedKeyCount > 0)
        {
            ok = false;
            Log.Error($"[自检] 收到了 {_processedKeyCount} 次 VK_PROCESSEDKEY(0xE5)：" +
                      "说明按键又被输入法接管了。");
        }

        // 泄漏探测：录入期间按键应当被完全吞掉，一个字都不该落进自检窗口的文本框。
        // 反过来说，文本框是空的也证明"按键确实被拦住了"。
        if (!string.IsNullOrEmpty(leakedText))
        {
            ok = false;
            Log.Error($"[自检] 有 {leakedText.Length} 个字符漏进了窗口输入框：「{leakedText}」——" +
                      "录入期间的按键没有被完整拦截。");
        }

        // 说明本次测试到底有没有真的触碰输入法。只用 F 键测是没有说服力的，
        // 这一点必须显式写出来，免得下次有人又把它改成"只测功能键"。
        Report($"[自检] 输入法组词消息（WM_IME_COMPOSITION）次数 = {imeCompositionCount}");
        if (imeCompositionCount == 0)
        {
            Report("[自检] 提示：本次未观察到输入法组词。若当前输入法处于英文态，" +
                   "请切到中文态再跑一次，才能覆盖真正的失败场景。");
        }

        Report(ok
            ? "[自检] 结果：通过 ✅ —— 物理按键直接到达录入通路，输入法无法拦截。"
            : "[自检] 结果：失败 ❌ —— 请把上面日志发出来。");

        Environment.ExitCode = ok ? 0 : 1;
    }

    /// <summary>把自检窗口强行拉到前台（后台启动的进程默认没这个权限）。</summary>
    private static void ForceForeground(Window window, IntPtr handle)
    {
        window.Activate();
        SetForegroundWindow(handle);
        BringWindowToTop(handle);

        var foreground = GetForegroundWindow();
        var target = GetWindowThreadProcessId(foreground, IntPtr.Zero);
        var self = GetCurrentThreadId();
        if (target != 0 && target != self)
        {
            AttachThreadInput(self, target, true);
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
            AttachThreadInput(self, target, false);
        }
    }

    private static void SendKey(ushort vk, bool down)
    {
        if (IsForbiddenKey(vk))
        {
            Log.Error($"[自检] 拒绝注入特殊键 vk=0x{vk:X2}（会改变系统状态，例如 Win 键会弹出开始菜单）。");
            return;
        }

        const uint KEYEVENTF_KEYUP = 0x0002;
        var inputs = new INPUT[1];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].U.ki = new KEYBDINPUT
        {
            wVk = vk,
            wScan = 0,
            dwFlags = down ? 0u : KEYEVENTF_KEYUP,
            time = 0,
            dwExtraInfo = 0,
        };

        if (SendInput(1, inputs, Marshal.SizeOf<INPUT>()) != 1)
            Log.Warn($"[自检] SendInput 注入 vk=0x{vk:X2} 失败，错误码 {Marshal.GetLastWin32Error()}");
    }
}
