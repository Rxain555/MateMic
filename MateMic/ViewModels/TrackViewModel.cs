using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using MateMic.Core;

namespace MateMic.ViewModels;

/// <summary>播放列表中的一条音频（文件名 + 全局播放快捷键 + 同步按住键）。</summary>
public sealed class TrackViewModel : INotifyPropertyChanged
{
    /// <summary>按键控件的字号，必须与 <c>Ui/Theme.xaml</c> 里 InputBoxButton 的 FontSize 一致。</summary>
    private const double ButtonFontSize = 11.5;

    /// <summary>按键控件模板的左右内边距（4+4）与描边（1+1）。</summary>
    private const double ButtonChromeWidth = 10;

    /// <summary>再留一点余量，避免不同 DPI / 字体回退下差半个像素就触发省略号。</summary>
    private const double WidthSafetyMargin = 4;

    /// <summary>占位符至少要放得下「设快捷键 / 设同步键」（实测 46 DIP）。</summary>
    private const double MinimumButtonWidth = 46 + ButtonChromeWidth + WidthSafetyMargin;

    private static readonly Typeface ButtonTypeface = new(
        new FontFamily("Microsoft YaHei UI, Segoe UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private string _hotkey = string.Empty;
    private string _holdKey = string.Empty;
    private bool _holdKeyFeatureEnabled;
    private bool _isRecordingHotkey;
    private bool _isRecordingHoldKey;

    public TrackViewModel(PlayerTrack model)
    {
        Model = model;
        DisplayName = string.IsNullOrWhiteSpace(model.DisplayName)
            ? Path.GetFileNameWithoutExtension(model.Path)
            : model.DisplayName;
        _hotkey = model.Hotkey;
        _holdKey = model.HoldKey;
    }

    public PlayerTrack Model { get; }

    public string FilePath => Model.Path;

    public string DisplayName { get; }

    public string Hotkey
    {
        get => _hotkey;
        set
        {
            if (_hotkey == value) return;
            _hotkey = value;
            Model.Hotkey = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HotkeyText));
            OnPropertyChanged(nameof(IsHotkeySet));
            OnPropertyChanged(nameof(HotkeyButtonTooltip));
            OnPropertyChanged(nameof(HotkeyButtonWidth));
        }
    }

    public string HotkeyText => _isRecordingHotkey
        ? "请按键…"
        : string.IsNullOrWhiteSpace(_hotkey) ? "设快捷键" : _hotkey;

    /// <summary>
    /// 该条目正在录入**播放快捷键**。录入提示只显示在"被点的那一个控件"上，
    /// 因此两个键各有一个独立的标志位——共用一个布尔值时两个框会同时显示「请按键…」。
    /// </summary>
    public bool IsRecordingHotkey
    {
        get => _isRecordingHotkey;
        set
        {
            if (_isRecordingHotkey == value) return;
            _isRecordingHotkey = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HotkeyText));
            OnPropertyChanged(nameof(HotkeyButtonWidth));
        }
    }

    /// <summary>该条目正在录入**同步按住键**。</summary>
    public bool IsRecordingHoldKey
    {
        get => _isRecordingHoldKey;
        set
        {
            if (_isRecordingHoldKey == value) return;
            _isRecordingHoldKey = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HoldKeyText));
            OnPropertyChanged(nameof(HoldKeyButtonWidth));
        }
    }

    /// <summary>
    /// 快捷键控件的宽度：按**当前文字实测宽度**给，因此既能像输入框那样紧凑，
    /// 又不会把长键位（例：`Ctrl + Alt + Shift + F12`）截断。
    /// 固定列宽要么浪费空间、要么截断长键位，这里改成量出来是多少就给多少。
    /// </summary>
    public double HotkeyButtonWidth => MeasureButtonWidth(HotkeyText);

    /// <summary>是否已绑定快捷键（用于"未设置时显示成灰字占位符"）。</summary>
    public bool IsHotkeySet => !string.IsNullOrWhiteSpace(_hotkey);

    /// <summary>按文字实测宽度算按键控件需要多宽（含内边距、描边与余量）。</summary>
    private static double MeasureButtonWidth(string text)
    {
        if (string.IsNullOrEmpty(text)) return MinimumButtonWidth;

        // PixelsPerDip 传 1.0：WPF 的布局单位就是 DIP，与屏幕 DPI 无关
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            ButtonTypeface, ButtonFontSize, Brushes.Black, 1.0);

        return Math.Max(MinimumButtonWidth,
            Math.Ceiling(formatted.WidthIncludingTrailingWhitespace) + ButtonChromeWidth + WidthSafetyMargin);
    }

    /// <summary>快捷键控件的悬浮提示：说明怎么操作，并重复一遍当前键位（万一按钮里被截断）。</summary>
    public string HotkeyButtonTooltip => string.IsNullOrWhiteSpace(_hotkey)
        ? "点击设置该文件的全局播放快捷键（录入时按 Esc 取消、Backspace 清除）"
        : $"全局播放快捷键：{_hotkey}（点击可重新设置；录入时按 Esc 取消、Backspace 清除）";

    /// <summary>播放前自动按下、播放结束后松开的按键（空 = 不使用）。</summary>
    public string HoldKey
    {
        get => _holdKey;
        set
        {
            if (_holdKey == value) return;
            _holdKey = value;
            Model.HoldKey = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HoldKeyText));
            OnPropertyChanged(nameof(IsHoldKeySet));
            OnPropertyChanged(nameof(HoldKeyButtonWidth));
        }
    }

    /// <summary>配置里的总开关是否允许使用「同步按住键」。</summary>
    public bool HoldKeyFeatureEnabled
    {
        get => _holdKeyFeatureEnabled;
        set
        {
            if (_holdKeyFeatureEnabled == value) return;
            _holdKeyFeatureEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HoldKeyEnabled));   // 少了这一条，「设同步键」按钮会一直停在灰的
            OnPropertyChanged(nameof(HoldKeyText));
            OnPropertyChanged(nameof(HoldKeyTooltip));
            OnPropertyChanged(nameof(IsHoldKeySet));
            OnPropertyChanged(nameof(HoldKeyButtonWidth));
        }
    }

    /// <summary>
    /// 「设同步键」控件上显示的文字：未启用总开关时提示"未启用"，
    /// 已启用但还没设按键时提示"设同步键"，设了就直接显示按键（像输入框一样）。
    /// </summary>
    public string HoldKeyText
    {
        get
        {
            if (_isRecordingHoldKey) return "请按键…";
            if (!_holdKeyFeatureEnabled) return "未启用";
            if (string.Equals(_holdKey, string.Empty, StringComparison.Ordinal)) return "设同步键";
            return _holdKey;
        }
    }

    public bool HoldKeyEnabled => _holdKeyFeatureEnabled;

    /// <summary>和快捷键控件一样：宽度按实测文字给（单个按键名最长的是「小键盘 *」）。</summary>
    public double HoldKeyButtonWidth => MeasureButtonWidth(HoldKeyText);

    public string HoldKeyTooltip
    {
        get
        {
            if (!_holdKeyFeatureEnabled)
                return "「同步按住键」总开关未启用：请先在上方的「同步按住键」开关处启用（注意反作弊风险）。";

            return string.IsNullOrEmpty(_holdKey)
                ? "点击设置按键：播放该音频前自动按下、播放结束后自动松开（用来替代游戏里手动按「按键说话」）。"
                : $"当前按键：{_holdKey}（点击可重新设置；录入时按 Esc 取消、Backspace 清除）";
        }
    }

    /// <summary>是否已设好同步按键（未设或总开关关着时显示成灰字占位符）。</summary>
    public bool IsHoldKeySet => _holdKeyFeatureEnabled && !string.IsNullOrWhiteSpace(_holdKey);

    private bool _isCurrent;

    /// <summary>是否为当前正在播放的条目（用于行高亮）。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
