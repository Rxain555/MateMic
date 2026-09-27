using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MateMic.Core;

/// <summary>
/// 全局热键（RegisterHotKey + WM_HOTKEY）。支持任意字母/数字/功能键与组合键，禁用 Win 组合。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // 注意：这里**故意**不再有 CharacterFromVirtualKey / ToUnicodeEx 这套"由虚拟键解字符"
    // 的辅助逻辑。它是历史上为了从中文输入法污染的 WPF Key 里往回猜按键而加的，
    // 属于治标手段，已随 ShortcutKeyCapture 的落地整体删除（详见该类注释与
    // docs/ime-hotkey-fix.md）。不要再把它加回来。

    private readonly Dictionary<int, (uint Modifiers, uint Vk, Action Callback)> _registered = new();
    private HwndSource? _source;
    private IntPtr _handle;

    /// <summary>
    /// 热键 ID 从 1 开始分配。RegisterHotKey 的文档规定：
    /// 应用程序必须使用 0x0000–0xBFFF，0xC000–0xFFFF 保留给共享 DLL（GlobalAddAtom 的范围）。
    /// 早期实现从 0xC000 开始，属于越界使用，可能与其他组件的热键 ID 冲突。
    /// </summary>
    private int _nextId = 1;

    public void Attach(Window window)
    {
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (_registered.TryGetValue(id, out var entry))
            {
                handled = true;
                try
                {
                    entry.Callback();
                }
                catch (Exception ex)
                {
                    Log.Error("热键回调异常", ex);
                }
            }
        }

        return IntPtr.Zero;
    }

    public sealed record HotkeyResult(bool Success, string? Error)
    {
        public static readonly HotkeyResult Ok = new(true, null);
    }

    /// <summary>注册一个热键；返回失败原因（用于 UI 提示）。</summary>
    public HotkeyResult Register(string gesture, Action callback)
    {
        if (_handle == IntPtr.Zero) return new HotkeyResult(false, "窗口尚未准备好，无法注册全局快捷键。");
        if (string.IsNullOrWhiteSpace(gesture)) return new HotkeyResult(false, "快捷键为空。");
        if (!TryParse(gesture, out var modifiers, out var vk, out var parseError))
            return new HotkeyResult(false, parseError);

        if ((modifiers & MOD_WIN) != 0)
            return new HotkeyResult(false, "Windows 键组合被系统保留，请更换。");

        var id = _nextId++;
        if (!RegisterHotKey(_handle, id, modifiers | MOD_NOREPEAT, vk))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ERROR_HOTKEY_ALREADY_REGISTERED)
                return new HotkeyResult(false, $"快捷键 {gesture} 已被其他程序占用，请更换。");
            return new HotkeyResult(false, $"快捷键 {gesture} 注册失败（错误码 {error}）。");
        }

        _registered[id] = (modifiers, vk, callback);
        Log.Info($"已注册全局快捷键 {gesture}");
        return HotkeyResult.Ok;
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered.Keys.ToArray())
        {
            try
            {
                UnregisterHotKey(_handle, id);
            }
            catch
            {
            }
        }

        _registered.Clear();
    }

    /// <summary>
    /// 注销某一个快捷键（按手势文本找）。用于"重新录入"：录入期间先把旧热键放开，
    /// 否则用户按下的第一个键会被系统当成"已经注册过的热键"吞掉（收不到 WM_KEYDOWN），
    /// 于是想把它重新设成同一个键就变得几乎不可能。
    /// </summary>
    public bool Unregister(string gesture)
    {
        if (_handle == IntPtr.Zero || string.IsNullOrWhiteSpace(gesture)) return false;

        var target = NormalizeGesture(gesture);
        var id = _registered
            .Where(pair => NormalizeGesture(Format(pair.Value.Modifiers, pair.Value.Vk)) == target)
            .Select(pair => pair.Key)
            .FirstOrDefault();

        if (id == 0) return false;

        try
        {
            UnregisterHotKey(_handle, id);
        }
        catch
        {
        }

        _registered.Remove(id);
        Log.Debug($"已临时注销快捷键 {gesture}（重新录入期间）");
        return true;
    }

    /// <summary>把手势文本规范化，便于比较（大小写、空格、全角加号都不影响）。</summary>
    private static string NormalizeGesture(string gesture)
        => gesture.Replace('＋', '+').Replace(" ", string.Empty).Trim().ToLowerInvariant();

    // ---------------------------------------------------------------- 解析

    /// <summary>把注册表用的修饰符转成可读文本。</summary>
    public static string Format(uint modifiers, uint vk)
    {
        var parts = new List<string>();
        if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & MOD_WIN) != 0) parts.Add("Win");
        parts.Add(KeyName(vk));
        return string.Join(" + ", parts);
    }

    /// <summary>把虚拟键码转成用户可读的键名；认不出来时返回 "VK 0xNN"。</summary>
    public static string KeyName(uint vk)
    {
        if (vk is >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x30 and <= 0x39) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        // 小键盘 0-9：必须给出 TryParse 认得的名字，否则录进来也注册不上
        if (vk is >= 0x60 and <= 0x69) return "小键盘 " + (vk - 0x60);

        return vk switch
        {
            0x20 => "空格",
            0x0D => "Enter",
            0x09 => "Tab",
            0x1B => "Esc",
            0x08 => "Backspace",
            0x2E => "Delete",
            0x2D => "Insert",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",
            0x2C => "PrintScreen",
            0x91 => "ScrollLock",
            0x13 => "Pause",
            0x14 => "CapsLock",
            0x5D => "菜单键",
            0x6A => "小键盘 *",
            0x6B => "小键盘 +",
            0x6D => "小键盘 -",
            0x6E => "小键盘 .",
            0x6F => "小键盘 /",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => "VK 0x" + vk.ToString("X2"),
        };
    }

    /// <summary>
    /// 用户的修饰键（Ctrl / Alt / Shift / Win，含左右两侧的专用虚拟键码）。
    /// 录入时用它判断"这个键是修饰键，先记下来继续等主键"。
    /// </summary>
    public static bool IsModifierKey(uint virtualKey)
        => virtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C
            or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    /// <summary>解析 "Ctrl + Alt + K" 形式的快捷键文本。</summary>
    public static bool TryParse(string gesture, out uint modifiers, out uint vk, out string? error)
    {
        modifiers = 0;
        vk = 0;
        error = null;

        var tokens = gesture
            .Replace('＋', '+')
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            error = "请按下一个字母/数字/功能键。";
            return false;
        }

        var hasNonModifier = false;
        foreach (var raw in tokens)
        {
            var token = raw.Trim();
            switch (token.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= MOD_CONTROL;
                    continue;
                case "alt":
                    modifiers |= MOD_ALT;
                    continue;
                case "shift":
                    modifiers |= MOD_SHIFT;
                    continue;
                case "win":
                case "windows":
                    modifiers |= MOD_WIN;
                    continue;
            }

            hasNonModifier = true;
            if (token.Length >= 2 && (token[0] == 'F' || token[0] == 'f')
                && int.TryParse(token[1..], out var fn) && fn is >= 1 and <= 24)
            {
                vk = (uint)(0x6F + fn);
                continue;
            }

            if (token.Length == 1)
            {
                var c = char.ToUpperInvariant(token[0]);
                if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
                {
                    vk = c;
                    continue;
                }

                // 符号键：映射到对应的虚拟键码。
                // 早期版本只认字母/数字/具名键，导致 OemPipe`\`、OemOpenBrackets`[`
                // 这类符号键虽然被正确识别成字符、却在这一步被拒（报"无法识别按键"）。
                if (SymbolVirtualKeys.TryGetValue(token[0], out var symbolKey))
                {
                    vk = symbolKey;
                    continue;
                }
            }

            vk = token.ToLowerInvariant() switch
            {
                "space" or "空格" => 0x20,
                "enter" or "回车" => 0x0D,
                "tab" => 0x09,
                "esc" or "escape" => 0x1B,
                "backspace" => 0x08,
                "delete" or "del" => 0x2E,
                "insert" or "ins" => 0x2D,
                "home" => 0x24,
                "end" => 0x23,
                "pageup" => 0x21,
                "pagedown" => 0x22,
                "left" => 0x25,
                "up" => 0x26,
                "right" => 0x27,
                "down" => 0x28,
                // 下面这些是 KeyName 会产出的名字，必须能解析回去，
                // 否则用户录进来的键会在注册那一步被判为"无法识别"。
                "printscreen" or "print" => 0x2C,
                "scrolllock" => 0x91,
                "pause" => 0x13,
                "capslock" => 0x14,
                "菜单键" or "apps" => 0x5D,
                "小键盘 *" => 0x6A,
                "小键盘 +" => 0x6B,
                "小键盘 -" => 0x6D,
                "小键盘 ." => 0x6E,
                "小键盘 /" => 0x6F,
                _ => 0u,
            };

            // 「小键盘 0」～「小键盘 9」
            if (vk == 0 && token.StartsWith("小键盘 ", StringComparison.Ordinal) &&
                token.Length == 5 && token[4] is >= '0' and <= '9')
            {
                vk = (uint)(0x60 + (token[4] - '0'));
            }

            if (vk == 0)
            {
                error = $"无法识别按键 “{token}”，请按下一个字母/数字/功能键。";
                return false;
            }
        }

        if (!hasNonModifier)
        {
            error = "热键必须包含至少一个非修饰键（字母、数字或功能键）。";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 把 WPF 的 <c>Key</c> 转成快捷键文本。
    ///
    /// ⚠ 已废弃（保留仅为对照）：中文输入法活跃时 WPF 只会给出 <c>Key.ImeProcessed</c>，
    /// 这里必然返回空串，因此**不能**作为录入依据。录入统一走
    /// <see cref="ShortcutKeyCapture"/>（低层键盘钩子读物理按键）。
    /// 不要在任何地方调用它。
    /// </summary>
    [Obsolete("中文输入法下 WPF 的 Key 是 ImeProcessed，无法用于录入；请使用 ShortcutKeyCapture。")]
    public static string FromWpfKey(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & System.Windows.Input.ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & System.Windows.Input.ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((modifiers & System.Windows.Input.ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((modifiers & System.Windows.Input.ModifierKeys.Windows) != 0) parts.Add("Win");

        var keyText = key switch
        {
            >= System.Windows.Input.Key.A and <= System.Windows.Input.Key.Z => key.ToString(),
            >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 => key.ToString()[1..],
            >= System.Windows.Input.Key.F1 and <= System.Windows.Input.Key.F24 => key.ToString(),
            >= System.Windows.Input.Key.NumPad0 and <= System.Windows.Input.Key.NumPad9 => "小键盘 " + key.ToString()[6..],
            System.Windows.Input.Key.Space => "空格",
            System.Windows.Input.Key.Enter => "Enter",
            System.Windows.Input.Key.Tab => "Tab",
            System.Windows.Input.Key.Escape => "Esc",
            System.Windows.Input.Key.Back => "Backspace",
            System.Windows.Input.Key.Delete => "Delete",
            System.Windows.Input.Key.Insert => "Insert",
            System.Windows.Input.Key.Home => "Home",
            System.Windows.Input.Key.End => "End",
            System.Windows.Input.Key.PageUp => "PageUp",
            System.Windows.Input.Key.PageDown => "PageDown",
            System.Windows.Input.Key.Left => "←",
            System.Windows.Input.Key.Up => "↑",
            System.Windows.Input.Key.Right => "→",
            System.Windows.Input.Key.Down => "↓",
            System.Windows.Input.Key.OemComma => ",",
            System.Windows.Input.Key.OemPeriod => ".",
            System.Windows.Input.Key.OemMinus => "-",
            System.Windows.Input.Key.OemPlus => "=",
            System.Windows.Input.Key.OemQuestion => "/",
            System.Windows.Input.Key.OemSemicolon => ";",
            System.Windows.Input.Key.OemQuotes => "'",
            System.Windows.Input.Key.OemOpenBrackets => "[",
            System.Windows.Input.Key.OemCloseBrackets => "]",
            System.Windows.Input.Key.OemPipe => "\\",
            System.Windows.Input.Key.OemTilde => "`",
            _ => string.Empty,
        };

        if (keyText.Length == 0) return string.Empty;
        parts.Add(keyText);
        return string.Join(" + ", parts);
    }

    /// <summary>
    /// 符号 → 虚拟键码映射（OEM 键）。
    /// RegisterHotKey 需要虚拟键码，而符号键在 WPF 侧是以字符形式给出的，
    /// 因此这里把常见符号映射到它在标准 US 键盘布局上的虚拟键码。
    /// </summary>
    private static readonly Dictionary<char, uint> SymbolVirtualKeys = new()
    {
        [';'] = 0xBA, [':'] = 0xBA,
        ['='] = 0xBB, ['+'] = 0xBB,
        [','] = 0xBC, ['<'] = 0xBC,
        ['-'] = 0xBD, ['_'] = 0xBD,
        ['.'] = 0xBE, ['>'] = 0xBE,
        ['/'] = 0xBF, ['?'] = 0xBF,
        ['`'] = 0xC0, ['~'] = 0xC0,
        ['['] = 0xDB, ['{'] = 0xDB,
        ['\\'] = 0xDC, ['|'] = 0xDC,
        [']'] = 0xDD, ['}'] = 0xDD,
        ['\''] = 0xDE, ['"'] = 0xDE,
        // 中文标点（输入法可能直接给出）
        ['、'] = 0xDC, ['。'] = 0xBE, ['，'] = 0xBC, ['；'] = 0xBA,
        ['：'] = 0xBA, ['？'] = 0xBF, ['！'] = 0x31,
        ['（'] = 0x39, ['）'] = 0x30,
        ['【'] = 0xDB, ['】'] = 0xDD,
        ['“'] = 0xDE, ['”'] = 0xDE, ['‘'] = 0xDE, ['’'] = 0xDE,
        ['－'] = 0xBD, ['＝'] = 0xBB, ['～'] = 0xC0,
    };

    /// <summary>把修饰键转成快捷键前缀文本。</summary>
    public static string ModifierPrefix(System.Windows.Input.ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & System.Windows.Input.ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & System.Windows.Input.ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((modifiers & System.Windows.Input.ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((modifiers & System.Windows.Input.ModifierKeys.Windows) != 0) parts.Add("Win");
        return string.Join(" + ", parts);
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
