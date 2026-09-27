using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace MateMic.Core;

/// <summary>
/// 快捷键录入的**唯一**通路。
///
/// ── 为什么不能用 WPF 的 PreviewKeyDown ────────────────────────────────
/// 中文输入法（微软拼音等 TSF 输入法）会在按键变成 WPF 的 <c>Key</c> **之前**把它吃掉：
/// 应用只能看到 <c>Key = ImeProcessed</c>、虚拟键 <c>0xE5</c>（VK_PROCESSEDKEY），
/// 物理按键信息已经丢失。历史上所有"从 WPF Key 反推按键"的兜底代码
/// （<c>CharacterFromVirtualKey</c> / <c>SymbolFromCharacter</c> / TextInput 缓存）
/// 都是在这一层猜，所以时好时坏、修了又坏。
///
/// ── 为什么窗口过程钩子也不够 ──────────────────────────────────────────
/// 实测（docs/ime-hotkey-fix.md）：在中文输入法处于组词状态时，字母键会被输入法
/// **整个吃掉**——窗口过程连 <c>WM_KEYDOWN</c> 都收不到，只收到一条
/// <c>WM_IME_COMPOSITION</c>。只有空格/回车这类"结束组词"的键才会漏到窗口。
/// 换句话说，凡是以"窗口收到按键"为前提的方案都不可靠。
///
/// ── 实际做法 ──────────────────────────────────────────────────────────
/// 录入期间安装 <c>WH_KEYBOARD_LL</c> 低层键盘钩子。它在系统把按键交给输入法之前
/// 就被调用，因此无论输入法是否组词、是否把按键吞掉，都能拿到物理按键。
/// 窗口过程钩子仍然保留：负责清掉候选窗（<c>WM_IME_SETCONTEXT</c>）与兜底。
///
/// 低层钩子只在"等待用户按键"的这几秒内安装，录完立刻卸载。
/// </summary>
public sealed class ShortcutKeyCapture : IDisposable
{
    // ---------------------------------------------------------------- 窗口消息

    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_IME_SETCONTEXT = 0x0281;

    /// <summary>WM_IME_SETCONTEXT 的 lParam 标志：让输入法不要在窗口上画组词窗。</summary>
    private const long ISC_SHOWUICOMPOSITIONWINDOW = unchecked((long)0x80000000);

    /// <summary>输入法处理按键后留下的伪虚拟键，必须丢弃。</summary>
    private const uint VK_PROCESSEDKEY = 0xE5;

    // ---------------------------------------------------------------- Win32

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern int MapVirtualKey(uint code, uint mapType);

    [DllImport("imm32.dll", SetLastError = true)]
    private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);

    [DllImport("imm32.dll", SetLastError = true)]
    private static extern bool ImmAssociateContextEx(IntPtr hWnd, IntPtr hIMC, uint flags);

    private const uint IACE_DEFAULT = 0x0010;

    // ---------------------------------------------------------------- 状态

    private HwndSource? _source;
    private IntPtr _handle;
    private bool _hooked;

    /// <summary>低层键盘钩子句柄；Zero 表示当前没装。</summary>
    private IntPtr _lowLevelHook = IntPtr.Zero;

    /// <summary>必须持有委托实例：否则会被 GC 回收，钩子回调直接崩。</summary>
    private LowLevelKeyboardProc? _lowLevelProc;

    private bool _capturing;

    /// <summary>
    /// 被低层钩子吞掉、还没等到抬起事件的按键。
    /// 必须记账：吞掉"按下"却把"抬起"放过去，会让系统和输入法停在"这个键一直按着"的状态。
    /// </summary>
    private readonly HashSet<uint> _swallowedKeys = new();

    /// <summary>录入到一半的修饰键（已按下、还没等到主键）。</summary>
    private uint _pendingModifiers;

    private Action<string>? _onCaptured;
    private Action? _onCancelled;

    /// <summary>
    /// true（默认）= 拿到第一个手势就自动结束录入（界面上的正常用法）；
    /// false = 继续等待后续按键（自检要连续录入多个键）。
    /// </summary>
    private bool _stopAfterFirst = true;

    /// <summary>本次录入是否已经结束（防止回调里再次 End 造成重复处理）。</summary>
    private bool _ended;

    /// <summary>当前是否正在等待用户按键。</summary>
    public bool IsCapturing => _capturing;

    /// <summary>
    /// 低层键盘钩子当前是否已装上（只在录入期间为 true）。
    ///
    /// 这是录入通路能否扛住中文输入法的**唯一关键**：装不上时字符键会被输入法吞掉。
    /// 自检（<c>--imecheck</c>）在 <see cref="Begin"/> 之后立刻读它，装不上就判失败，
    /// 避免这个回归悄悄溜回去。
    /// </summary>
    public bool IsLowLevelHookActive => _lowLevelHook != IntPtr.Zero;

    // ---------------------------------------------------------------- 安装

    /// <summary>挂在窗口上（幂等）。必须在窗口句柄创建之后调用。</summary>
    public void Attach(nint windowHandle)
    {
        if (windowHandle == nint.Zero) return;
        if (_handle == windowHandle && _hooked) return;

        DetachWindowHook();

        _handle = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle);
        if (_source == null)
        {
            Log.Warn("快捷键录入：拿不到窗口的 HwndSource，窗口过程钩子未安装（低层钩子仍可用）。");
            return;
        }

        _source.AddHook(WndProc);
        _hooked = true;
    }

    /// <summary>窗口句柄重建后重新挂钩（从托盘恢复、跨屏 / DPI 变化）。</summary>
    public void Reattach(nint windowHandle)
    {
        DetachWindowHook();
        Attach(windowHandle);
        if (_capturing) SuppressInputMethod();
    }

    /// <summary>
    /// 开始录入。
    /// </summary>
    /// <param name="onCaptured">拿到手势时回调；空字符串表示用户要清除该快捷键。</param>
    /// <param name="onCancelled">用户按 Esc 取消时回调。</param>
    /// <param name="stopAfterFirst">
    /// true（默认）= 拿到第一个手势就结束录入（界面正常用法）；
    /// false = 继续等待后续按键（自检连续录入多个键）。
    /// </param>
    public void Begin(Action<string> onCaptured, Action? onCancelled = null, bool stopAfterFirst = true)
    {
        _onCaptured = onCaptured;
        _onCancelled = onCancelled;
        _stopAfterFirst = stopAfterFirst;
        _pendingModifiers = 0;
        _capturing = true;
        _ended = false;

        SuppressInputMethod();
        InstallLowLevelHook();

        Log.Info("快捷键录入开始：低层键盘钩子已安装，将直接读取物理按键（输入法无法拦截）。");
    }

    /// <summary>结束录入（拿到键、取消、或界面主动收尾）。可以重复调用。</summary>
    public void End()
    {
        if (_ended) return;
        _ended = true;
        _capturing = false;
        _pendingModifiers = 0;
        _onCaptured = null;
        _onCancelled = null;
        _swallowedKeys.Clear();

        UninstallLowLevelHook();
    }

    /// <summary>
    /// 把窗口上的输入法按住：WPF 侧不参与输入法，Win32 侧摘掉上下文，
    /// 这样候选窗不会弹出来（按键本身由低层钩子保证拿得到）。
    /// </summary>
    public void SuppressInputMethod()
    {
        if (_handle == nint.Zero) return;

        ImmAssociateContext(_handle, nint.Zero);
        ImmAssociateContextEx(_handle, nint.Zero, IACE_DEFAULT);
    }

    // ---------------------------------------------------------------- 低层键盘钩子

    private void InstallLowLevelHook()
    {
        if (_lowLevelHook != IntPtr.Zero) return;

        // 委托必须存成字段：局部委托会被 GC 回收，钩子回调随即崩溃
        _lowLevelProc = LowLevelCallback;
        _lowLevelHook = SetWindowsHookEx(WH_KEYBOARD_LL, _lowLevelProc, GetModuleHandle(null), 0);

        if (_lowLevelHook == IntPtr.Zero)
        {
            // 装不上不算致命：窗口过程钩子还在，只是"输入法组词时"可能拿不到字母
            var error = Marshal.GetLastWin32Error();
            Log.Error($"快捷键录入：低层键盘钩子安装失败（错误码 {error}），" +
                      "将退化为窗口过程钩子；中文输入法下可能仍收不到字符键。");
        }
    }

    private void UninstallLowLevelHook()
    {
        if (_lowLevelHook == IntPtr.Zero) return;

        try
        {
            UnhookWindowsHookEx(_lowLevelHook);
        }
        catch (Exception ex)
        {
            Log.Warn("卸载低层键盘钩子失败：" + ex.Message);
        }
        finally
        {
            _lowLevelHook = IntPtr.Zero;
            _lowLevelProc = null;
        }
    }

    private IntPtr LowLevelCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            var message = wParam.ToInt64();
            var isDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;

            if (nCode >= 0)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var vk = (uint)data.vkCode;

                if (isDown && _capturing &&
                    HandlePhysicalKey(vk, data.scanCode, "低层钩子"))
                {
                    // 录入期间吞掉"按下"：既不让它在别的窗口里打出一个字符，
                    // 也不让输入法据此开始组词。抬起事件照常放过去（见下），
                    // 避免系统和输入法停在"这个键一直按着"的状态。
                    _swallowedKeys.Add(vk);
                    return 1;
                }

                // 之前吞过这个键的"按下"，它的"抬起"要让系统看到，否则键状态会卡住
                if (!isDown && _swallowedKeys.Remove(vk))
                    return CallNextHookEx(_lowLevelHook, nCode, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            // 钩子回调里绝不能让异常逃出去：会直接杀掉进程
            Log.Error("低层键盘钩子回调异常", ex);
        }

        return CallNextHookEx(_lowLevelHook, nCode, wParam, lParam);
    }

    // ---------------------------------------------------------------- 窗口过程（兜底）

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_IME_SETCONTEXT:
                // 不让输入法在本窗口画组词候选窗。
                // 只清 lParam 里的"显示候选窗"标志，其余交给系统——不做整条消息拦截，
                // 免得把输入法自己的状态机搅坏（那会引出更难查的问题）。
                if (wParam.ToInt64() != 0)
                    return (IntPtr)(lParam.ToInt64() & ~ISC_SHOWUICOMPOSITIONWINDOW);
                break;

            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
                // 兜底：低层钩子没装上时，至少还能拿到"没被输入法吞掉"的那些键
                if (_capturing)
                {
                    var virtualKey = (uint)(wParam.ToInt64() & 0xFFFF);
                    var scanCode = (uint)((lParam.ToInt64() >> 16) & 0xFF);
                    if (HandlePhysicalKey(virtualKey, scanCode, "窗口过程")) handled = true;
                }
                break;

            case WM_KEYUP:
            case WM_SYSKEYUP:
                if (_capturing)
                {
                    handled = true;
                    return IntPtr.Zero;
                }
                break;
        }

        return IntPtr.Zero;
    }

    // ---------------------------------------------------------------- 按键判定

    /// <summary>
    /// 处理一个**物理按键**。返回 true 表示这次录入把它消费掉了（调用方应吞掉该消息）。
    /// 低层钩子与窗口过程都走这里，保证两条路径的判定完全一致。
    /// </summary>
    private bool HandlePhysicalKey(uint virtualKey, uint scanCode, string source)
    {
        if (!_capturing) return false;

        // 输入法合成出来的伪键，不是用户按的
        if (virtualKey == VK_PROCESSEDKEY) return false;

        // SendInput 合成的按键不带扫描码，这里用虚拟键补齐（真实键盘本来就有）
        if (scanCode == 0) scanCode = (uint)MapVirtualKey(virtualKey, 0);

        // 修饰键本身：记下来，继续等主键
        if (HotkeyService.IsModifierKey(virtualKey))
        {
            _pendingModifiers |= ModifierOf(virtualKey);
            return true;
        }

        // Esc：取消录入
        if (virtualKey == 0x1B)
        {
            var cancel = _onCancelled;
            End();
            cancel?.Invoke();
            return true;
        }

        // Backspace / Delete：清除该快捷键
        if (virtualKey is 0x08 or 0x2E)
        {
            Emit(string.Empty);
            return true;
        }

        // 修饰键用实时键状态：WPF 的 Keyboard.Modifiers 在这里可能还没刷新
        var allModifiers = ReadModifiers() | _pendingModifiers;
        var gesture = BuildGesture(allModifiers, virtualKey);
        if (string.IsNullOrEmpty(gesture))
        {
            Log.Warn($"快捷键录入：无法识别虚拟键 0x{virtualKey:X2}（来源={source}）。");
            return true;
        }

        Log.Info($"快捷键录入：捕获到物理按键 vk=0x{virtualKey:X2}、扫描码=0x{scanCode:X2}" +
                 $"（来源={source}），修饰键={HotkeyService.ModifierPrefix(ToModifierKeys(allModifiers))}" +
                 $"，手势=「{gesture}」");

        Emit(gesture);
        return true;
    }

    /// <summary>
    /// 交出一个手势：先按需结束本次录入，再回调。
    ///
    /// ⚠ 顺序很重要：必须**先** End 再回调。回调会去改配置、重注册全局热键，
    /// 期间如果录入还处于活动状态，新按键会插进这次回调里再触发一次。
    /// </summary>
    private void Emit(string gesture)
    {
        var callback = _onCaptured;
        if (_stopAfterFirst) End();
        callback?.Invoke(gesture);
    }

    /// <summary>按"修饰键 + 主键"拼出手势文本；主键无法识别时返回空串。</summary>
    private static string BuildGesture(uint modifiers, uint virtualKey)
    {
        var keyName = HotkeyService.KeyName(virtualKey);
        if (string.IsNullOrEmpty(keyName) || keyName.StartsWith("VK 0x", StringComparison.Ordinal))
            return string.Empty;

        // 「同步按住键」只支持单个按键：带了修饰键时这里照样给出完整手势，
        // 由上层给出"只能单个按键"的提示。
        var prefix = HotkeyService.ModifierPrefix(ToModifierKeys(modifiers));
        return string.IsNullOrEmpty(prefix) ? keyName : prefix + " + " + keyName;
    }

    /// <summary>实时读取修饰键状态（比 WPF 的 Keyboard.Modifiers 可靠）。</summary>
    private static uint ReadModifiers()
    {
        uint modifiers = 0;
        if (IsDown(0x11) || IsDown(0xA2) || IsDown(0xA3)) modifiers |= 0x0002;   // Ctrl
        if (IsDown(0x12) || IsDown(0xA4) || IsDown(0xA5)) modifiers |= 0x0001;   // Alt
        if (IsDown(0x10) || IsDown(0xA0) || IsDown(0xA1)) modifiers |= 0x0004;   // Shift
        if (IsDown(0x5B) || IsDown(0x5C)) modifiers |= 0x0008;                    // Win
        return modifiers;

        static bool IsDown(int vk) => (GetKeyState(vk) & 0x8000) != 0;
    }

    private static uint ModifierOf(uint virtualKey) => virtualKey switch
    {
        0x11 or 0xA2 or 0xA3 => 0x0002,   // Ctrl
        0x12 or 0xA4 or 0xA5 => 0x0001,   // Alt
        0x10 or 0xA0 or 0xA1 => 0x0004,   // Shift
        0x5B or 0x5C => 0x0008,           // Win
        _ => 0,
    };

    private static ModifierKeys ToModifierKeys(uint modifiers)
    {
        var result = ModifierKeys.None;
        if ((modifiers & 0x0002) != 0) result |= ModifierKeys.Control;
        if ((modifiers & 0x0001) != 0) result |= ModifierKeys.Alt;
        if ((modifiers & 0x0004) != 0) result |= ModifierKeys.Shift;
        if ((modifiers & 0x0008) != 0) result |= ModifierKeys.Windows;
        return result;
    }

    private void DetachWindowHook()
    {
        if (_source != null && _hooked)
        {
            try
            {
                _source.RemoveHook(WndProc);
            }
            catch
            {
            }
        }

        _source = null;
        _hooked = false;
    }

    public void Dispose()
    {
        End();
        DetachWindowHook();
        _handle = nint.Zero;
    }
}
