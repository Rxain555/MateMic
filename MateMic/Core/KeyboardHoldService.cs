using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace MateMic.Core;

/// <summary>
/// 「同步按住键」：在播放音频前把一个按键按下，播放结束后松开。
///
/// 用途（游戏里的按键说话）：把「按住键」设为游戏/语音软件里正在使用的按键说话键，
/// 按下播放语音包的全局快捷键时就不必再手动按住那个键。
///
/// ⚠️ 反作弊说明（重要）
/// 本服务通过 Win32 <c>SendInput</c> 注入按键事件。注入事件带 <c>LLKHF_INJECTED</c> 标记，
/// 内核级反作弊（Vanguard / Easy Anti-Cheat / BattlEye / Faceit 等）有能力识别；
/// 虽然"模拟按键"与"自动瞄准、宏连发"的性质不同，但风控策略由厂商决定，**被判定的风险不能排除**。
/// 因此：
///   · 该功能默认关闭，必须由用户在界面上显式开启；
///   · 只使用用户自己录入的那一个按键，不做连发、不做循环、不注入任何地址/代码；
///   · 不监听、不读写其它进程。
/// 若对反作弊有顾虑，请使用硬件级方案（如键盘宏、脚踏开关、手柄映射）。
/// </summary>
public sealed class KeyboardHoldService : IDisposable
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    /// <summary>按下与松开之间的最小间隔：太短的按住有时不会被目标程序识别为一次按键。</summary>
    private const int MinHoldMilliseconds = 60;

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
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern ushort MapVirtualKey(uint uCode, uint uMapType);

    /// <summary>键盘事件队列（单一后台线程按序发送，保证"按下 → 松开"顺序不乱）。</summary>
    private readonly BlockingCollection<(bool Down, ushort Scan, bool Extended)> _queue = new();

    private readonly Thread _worker;
    private volatile bool _disposed;

    /// <summary>当前由本服务按住的键（扫描码）；_held == 0 表示没有按键被按住。</summary>
    private ushort _held;
    private bool _heldExtended;
    private int _heldSince;

    /// <summary>最近一次操作的结果信息（供状态栏提示）。</summary>
    public string? LastError { get; private set; }

    public KeyboardHoldService()
    {
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "MateMic.KeyboardHold",
        };
        _worker.Start();
    }

    /// <summary>当前是否有按键由本服务按住。</summary>
    public bool IsHolding => Volatile.Read(ref _held) != 0;

    /// <summary>
    /// 解析并按住一个按键。返回 false 表示按键无法解析或系统拒绝注入。
    /// 已经按住同一个键时不会重复按下。
    /// </summary>
    public bool Press(string gesture) => Press(gesture, skipIfAlreadyDown: true);

    /// <summary>
    /// 同 <see cref="Press(string)"/>，但可以跳过"用户已经手动按着这个键"的检测。
    /// 自检（<c>--keycheck</c>）必须跳过：它自己会去读键状态，读到按下就会误判。
    /// </summary>
    public bool Press(string gesture, bool skipIfAlreadyDown)
    {
        if (_disposed || string.IsNullOrWhiteSpace(gesture)) return false;
        if (!HotkeyService.TryParse(gesture, out _, out var virtualKey, out var error))
        {
            LastError = error;
            Log.Warn("同步按住键：无法解析按键 " + gesture + "（" + error + "）");
            return false;
        }

        var scan = (ushort)MapVirtualKey(virtualKey, 0);   // MAPVK_VK_TO_VSC
        if (scan == 0)
        {
            LastError = "该按键没有可用的扫描码，无法注入。";
            Log.Warn($"同步按住键：虚拟键 0x{virtualKey:X2} 没有扫描码，已跳过。");
            return false;
        }

        // 目标程序自己已经在按着这个键（真的手按着），此时无需注入
        if (skipIfAlreadyDown && _held == 0 && GetAsyncKeyState((int)virtualKey) < 0)
        {
            Log.Info($"同步按住键：{gesture} 当前已处于按下状态，跳过注入。");
            LastError = null;
            return true;
        }

        if (Volatile.Read(ref _held) == scan) return true;

        Release();   // 先松开上一个键，避免多个键同时被按住

        var extended = IsExtended(virtualKey);
        _heldExtended = extended;
        _heldSince = Environment.TickCount;
        Volatile.Write(ref _held, scan);
        LastError = null;
        _queue.Add((true, scan, extended));
        Log.Info($"同步按住键：按下 {gesture}（扫描码 0x{scan:X2}）");
        return true;
    }

    /// <summary>松开当前按住的键（没有按住时是空操作）。</summary>
    public void Release()
    {
        var scan = Volatile.Read(ref _held);
        if (scan == 0) return;

        var extended = _heldExtended;
        Volatile.Write(ref _held, 0);

        // 太短的"按住"可能被目标程序忽略，这里补足一个最短按住时长
        var elapsed = Environment.TickCount - _heldSince;
        if (elapsed < MinHoldMilliseconds) Thread.Sleep(MinHoldMilliseconds - elapsed);

        if (!_disposed) _queue.Add((false, (ushort)scan, extended));
        Log.Info($"同步按住键：松开（扫描码 0x{scan:X2}）");
    }

    /// <summary>退出时必须调用：把仍按住的键松开，否则会在游戏里留下"一直按着"的按键。</summary>
    public void ReleaseAll()
    {
        try
        {
            Release();
        }
        catch (Exception ex)
        {
            Log.Warn("释放同步按住键失败：" + ex.Message);
        }
    }

    /// <summary>带修饰键的组合（Ctrl/Alt 等）需要 EXTENDEDKEY 才能被正确识别。</summary>
    private static bool IsExtended(uint virtualKey) => virtualKey is 0x21 or 0x22 or 0x23 or 0x24
        or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0x6F or 0x90;

    private void WorkerLoop()
    {
        try
        {
            foreach (var (down, scan, extended) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    Send(down, scan, extended);
                }
                catch (Exception ex)
                {
                    Log.Error("注入按键失败", ex);
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // 退出路径上的正常情况
        }
    }

    private void Send(bool down, ushort scan, bool extended)
    {
        var flags = KEYEVENTF_SCANCODE;
        if (extended) flags |= KEYEVENTF_EXTENDEDKEY;
        if (!down) flags |= KEYEVENTF_KEYUP;

        var inputs = new INPUT[1];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].U.ki = new KEYBDINPUT
        {
            wVk = 0,          // 用扫描码注入，兼容 DirectInput / Raw Input 的游戏
            wScan = scan,
            dwFlags = flags,
            time = 0,
            dwExtraInfo = 0,
        };

        var sent = SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        if (sent != 1)
            Log.Warn($"SendInput 注入按键失败（{(down ? "按下" : "松开")} 扫描码 0x{scan:X2}），" +
                     $"错误码 {Marshal.GetLastWin32Error()}。可能是目标程序以更高权限运行。");
    }

    public void Dispose()
    {
        if (_disposed) return;
        ReleaseAll();
        _disposed = true;

        _queue.CompleteAdding();
        try
        {
            _worker.Join(300);
        }
        catch
        {
        }

        _queue.Dispose();
    }
}
