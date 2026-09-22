using System.Text.Json.Serialization;

namespace MicMate.Core;

public enum ToneStyle
{
    Natural = 0,
    Bright = 1,
    Warm = 2,
    Deep = 3,
    Sharp = 4,
    Ethereal = 5,
}

public enum CreativeEffectKind
{
    Reverb = 0,
    Delay = 1,
    Chorus = 2,
    Robot = 3,

    /// <summary>炸麦：麦克风过载模拟（增益前推 + 硬削波降位 + 带通）。</summary>
    Megaphone = 4,
}

public sealed class NoiseGateSettings
{
    public bool Enabled { get; set; }

    /// <summary>阈值。−55 dBFS 接近安静房间的底噪水平：能压掉空调/风扇声，又不至于吃掉正常说话。</summary>
    public float ThresholdDb { get; set; } = -55f;

    public float ReleaseMs { get; set; } = 150f;
}

public sealed class DenoiseSettings
{
    public bool Enabled { get; set; }
    public string Model { get; set; } = "默认模型";
    public float Strength { get; set; } = 70f;
    public float Wet { get; set; } = 100f;
}

public sealed class LoudnessSettings
{
    public bool Enabled { get; set; }

    /// <summary>目标响度。−20 更接近语音通话的舒适区间，比 −18 保守一些。</summary>
    public float TargetLufs { get; set; } = -20f;

    public int Speed { get; set; } = 5;
}

public sealed class ToneSettings
{
    public bool Enabled { get; set; }

    /// <summary>null 表示用户尚未选择任何音色风格（此时模块不参与处理）。</summary>
    public ToneStyle? Style { get; set; }
}

public sealed class CreativeEffectSettings
{
    public bool Enabled { get; set; }

    /// <summary>null 表示用户尚未选择任何效果（此时模块不参与处理）。</summary>
    public CreativeEffectKind? Kind { get; set; }

    public float Amount { get; set; } = 55f;
}

public sealed class GainSettings
{
    public bool Enabled { get; set; }
    public float GainDb { get; set; }
}

public sealed class PlayerTrack
{
    public string Path { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Hotkey { get; set; } = string.Empty;
}

public sealed class PlayerSettings
{
    public bool MonitorTogether { get; set; }
    public bool Loop { get; set; }
    public float Volume { get; set; } = 80f;
    public List<PlayerTrack> Tracks { get; set; } = new();
}

public sealed class DeviceSettings
{
    public string? InputDeviceId { get; set; }
    public string? OutputDeviceId { get; set; }
    public string? MonitorDeviceId { get; set; }
}

/// <summary>左侧处理链各模块的展开/收起状态，随配置持久化。</summary>
public sealed class PanelExpandState
{
    public bool Gate { get; set; }
    public bool Denoise { get; set; }
    public bool Loudness { get; set; }
    public bool Tone { get; set; }
    public bool Effect { get; set; }
    public bool Gain { get; set; }
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;

    /// <summary>总旁通开关。首次启动默认关闭（未开始处理），由用户主动开启。</summary>
    public bool AudioProcessingEnabled { get; set; }

    public string ToggleHotkey { get; set; } = string.Empty;
    public bool MonitorEnabled { get; set; }
    public bool AutoStart { get; set; }
    public bool MinimizeToTray { get; set; } = true;

    public double WindowWidth { get; set; } = 1160;
    public double WindowHeight { get; set; } = 720;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;

    /// <summary>左侧处理链各模块的展开状态（收起 = false，界面默认全部收起）。</summary>
    public PanelExpandState Panels { get; set; } = new();

    public DeviceSettings Devices { get; set; } = new();
    public NoiseGateSettings NoiseGate { get; set; } = new();
    public DenoiseSettings Denoise { get; set; } = new();
    public LoudnessSettings Loudness { get; set; } = new();
    public ToneSettings Tone { get; set; } = new();
    public CreativeEffectSettings Effect { get; set; } = new();
    public GainSettings Gain { get; set; } = new();
    public PlayerSettings Player { get; set; } = new();

    [JsonIgnore]
    public bool Migrated => Version < 1;
}
