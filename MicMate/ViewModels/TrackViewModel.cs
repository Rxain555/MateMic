using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using MicMate.Core;

namespace MicMate.ViewModels;

/// <summary>播放列表中的一条音频（文件名 + 全局快捷键）。</summary>
public sealed class TrackViewModel : INotifyPropertyChanged
{
    private string _hotkey = string.Empty;

    public TrackViewModel(PlayerTrack model)
    {
        Model = model;
        DisplayName = string.IsNullOrWhiteSpace(model.DisplayName)
            ? Path.GetFileNameWithoutExtension(model.Path)
            : model.DisplayName;
        _hotkey = model.Hotkey;
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
        }
    }

    public string HotkeyText => string.IsNullOrWhiteSpace(_hotkey) ? "点击设置快捷键" : _hotkey;

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
