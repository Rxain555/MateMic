using System.Text.Json.Serialization;

namespace MateMic.Core;

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
    /// <summary>和声：叠一个纯五度声部。**沿用原合唱的枚举值 2**，
    /// 因此老配置里存的 2 会自动变成"和声"，不需要写迁移代码。</summary>
    Harmony = 2,
    Robot = 3,

    /// <summary>炸麦：麦克风过载模拟（增益前推 + 硬削波降位 + 带通）。</summary>
    Megaphone = 4,

    /// <summary>电话：300–3400 Hz 带通 + 轻饱和，模拟电话/对讲机。</summary>
    Telephone = 5,

    /// <summary>颤音：约 5 Hz 的周期性音量起伏。</summary>
    Tremolo = 6,
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

    /// <summary>
    /// 降噪模型标识：**空字符串 = 内置经典降噪**；外部模型用不带扩展名的文件名。
    ///
    /// 刻意不存显示名：显示名是文案，改了会让老配置全部失配、用户莫名丢掉模型选择。
    /// 默认值 dpdfnet2_48khz_hr 随程序内置分发（放在 exe 同级的 models\ 里）；
    /// 万一这台机器上没有这个文件，启动时会自动回退到内置经典降噪并写进日志。
    /// </summary>
    public string Model { get; set; } = "dpdfnet2_48khz_hr";

    public float Strength { get; set; } = 100f;
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

    /// <summary>
    /// 当前选中的预设，**只用于界面勾选状态**；null 表示"手动 EQ"或"尚未选择"。
    /// 音频处理不读它 —— 处理只看 <see cref="Gains"/>，因此界面与声音永远是同一份数据。
    /// </summary>
    public ToneStyle? Style { get; set; }

    /// <summary>
    /// EQ 各段增益（dB），长度必须是 <see cref="EqPreset.BandCount"/>。
    ///
    /// **这是均衡器的唯一真相**：
    ///   · 点预设 → 整表替换成该预设的曲线（并把 <see cref="Style"/> 设为它）；
    ///   · 手动拖某一段推子 → 只改这一段，同时把 <see cref="Style"/> 清空（表示"自定义"）；
    ///   · 重置 → 全 0，<see cref="Style"/> 也清空；
    ///   · 全 0 视为平坦响应，模块会退出处理链（等价于关闭）。
    /// 老配置没有这个字段，由 <see cref="ConfigMigrations"/> 按当时的 Style 补齐。
    /// </summary>
    public float[] Gains { get; set; } = new float[EqPreset.BandCount];
}

/// <summary>
/// 变声（DSP）。AI 变声（RVC 组件）已于 2026-10-04 从应用剔除，
/// 归档与理由见 `归档文档\AI变声-归档\归档说明.md`；这里只保留 DSP 的各项参数。
/// </summary>
/// <summary>
/// 变声模块的工作模式。**仅为兼容旧配置文件而保留**：老配置里存着 <c>"Mode": "Ai"</c>，
/// 删掉这个属性会让 JSON 反序列化报未知字段；程序已不再读取它（一律走 DSP）。
/// </summary>
public enum VoiceChangerMode
{
    /// <summary>内置 DSP 变声：零依赖、零延迟。</summary>
    Dsp = 0,

    /// <summary>（已废弃）AI 变声：曾需要先安装引擎组件并在 GPU 上推理。</summary>
    Ai = 1,
}

public sealed class VoiceChangerSettings
{
    /// <summary>
    /// 变调量（半音）。男→女约 +5~7，女→男约 −5~7。
    ///
    /// **默认 0**（2026-10-07 用户要求）：变声属于高阶功能，两个变声模块（AI / DSP）
    /// 的默认值都归零，由用户按需调；说明写在使用指南与讲解视频里，界面上不写。
    /// </summary>
    public float Semitones { get; set; }

    /// <summary>共振峰偏移（半音）：正值更"细/年轻"，负值更"厚/低沉"。这是真正的共振峰搬移。</summary>
    public float FormantSemitones { get; set; }

    /// <summary>
    /// 性别因子 −100…+100：负值更低沉、正值更清亮。
    /// 它不是简单的整体移位，而是把基频**分布**搬到目标性别（均值按它移动、离散度归一化）。
    /// </summary>
    public float GenderFactor { get; set; }

    /// <summary>干湿比 0~100：0 = 完全原声，100 = 完全变声。</summary>
    public float Mix { get; set; } = 100f;

    public bool Enabled { get; set; }

    /// <summary>（已废弃）曾用于 DSP / AI 切换；程序已不读取，仅为兼容旧配置保留。</summary>
    public VoiceChangerMode Mode { get; set; } = VoiceChangerMode.Dsp;
}

public sealed class CreativeEffectSettings
{
    public bool Enabled { get; set; }

    /// <summary>null 表示用户尚未选择任何效果（此时模块不参与处理）。</summary>
    public CreativeEffectKind? Kind { get; set; }

    public float Amount { get; set; } = 55f;
}

/// <summary>
/// AI 变声（RVC）。处理链位置：**AI 降噪之后、DSP 变声之前**。
///
/// 为什么必须在这个位置：
///   · 在降噪之后——RVC 对噪声极敏感，会把噪声当内容一起转换；
///   · 在 DSP 变声之前——RVC 会重新分析输入基频，若先做 DSP 变调，
///     RVC 会把变调后的音高当成"原始音高"再转一遍，等于抵消掉 DSP 的变调。
///
/// 与 DSP 变声的分工：AI 决定"是谁的声音"（音色 + 基础音高），
/// DSP 在其之上做微调（额外变调 + **共振峰搬移**，后者 AI 做不到）。
/// </summary>
public sealed class AiVoiceSettings
{
    public bool Enabled { get; set; }

    /// <summary>
    /// 变调量（半音，RVC 的 f0_up_key）。**默认 0**：变声属高阶功能，
    /// 两个变声模块的默认值都归零，避免用户不明就里地听到变调后的声音。
    /// </summary>
    public float Semitones { get; set; }

    /// <summary>
    /// 音色模型（合成器 .onnx）的**文件名**（位于 data\components\ai\voices\）。
    /// 存文件名而非绝对路径，换机/换目录后仍能对上；null 表示尚未选择。
    /// </summary>
    public string? VoiceModel { get; set; }

    /// <summary>
    /// 音色索引（FAISS .index）的**文件名**（位于 data\components\ai\index\）。
    /// 可选：没有索引时"索引占比"不可调。
    /// </summary>
    public string? IndexFile { get; set; }

    /// <summary>
    /// 索引特征占比 0~100：检索结果替换内容特征的比例（RVC 的 index_rate）。
    /// 越高越贴近目标音色，但过高会损失咬字。
    /// </summary>
    public float IndexRate { get; set; } = 75f;

    /// <summary>
    /// 声线（共振峰搬移，半音）。RVC 自身不做共振峰调整，这里是**在 AI 输出之后**
    /// 用 Signalsmith Stretch 补的——正好补偿变调后"像同一个人捏着嗓子"的假声感。
    /// </summary>
    public float FormantSemitones { get; set; }

    /// <summary>
    /// 音频块长度（ms）。**决定算法延迟：延迟 = 2 × block**（官方口径）。
    /// 越小延迟越低、但每次推理要算的窗口相对越大，负载越高。
    /// </summary>
    public int BlockMs { get; set; } = 160;

    /// <summary>
    /// 上下文长度（ms）：每次推理额外多喂给模型的历史，用于保证音质连续性。
    /// 越大越稳，但耗时随 (block + context) 线性增长。
    /// </summary>
    public int ContextMs { get; set; } = 320;

    /// <summary>交叉淡化长度（ms）：块与块之间的平滑过渡。SOLA 重叠会被封顶在 40ms。</summary>
    public int CrossfadeMs { get; set; } = 40;

    /// <summary>
    /// 输出噪声门阈值（dBFS）。RVC 在无人声处也会输出微弱信号
    /// （实测原始素材 6.3% 静音帧、转换后变成 0.0%），用它把静音段压干净。
    /// 0 表示关闭。
    /// </summary>
    public float GateDb { get; set; } = -45f;
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

    /// <summary>
    /// 同步按住键（按键说话的替代）：播放前按下该键、播放结束后松开。
    /// 空字符串表示该条目不使用此功能。
    /// </summary>
    public string HoldKey { get; set; } = string.Empty;
}

public sealed class PlayerSettings
{
    /// <summary>
    /// 界面上播放器面板的「音频监听」开关：播放器（伴奏/语音包）要不要**额外**送进监听设备。
    /// 注意播放器音频**始终**进主输出（MIXLINE，队友能听到），这个开关只管"我自己的扬声器里听不听得到"。
    /// 与工具栏的监听设备开关组合出四种行为，见 <c>Dsp/MicMixer.cs</c> 类注释与 README §5。
    ///
    /// **默认打开**（2026-10-06 用户要求）：放语音包时自己通常也要听见。
    /// </summary>
    public bool AudioMonitor { get; set; } = true;

    public bool Loop { get; set; }

    /// <summary>播放器音量。**默认 40**（2026-10-06 用户要求，原来 80 偏响）。</summary>
    public float Volume { get; set; } = 40f;

    /// <summary>
    /// 总开关：是否允许「同步按住键」。默认关闭。
    /// 该功能会用 SendInput 向系统注入按键，理论上存在被反作弊系统判定的风险，
    /// 因此必须由用户显式开启后才会生效。
    /// </summary>
    public bool EnableHoldKey { get; set; }

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

    /// <summary>变声模块的展开状态。</summary>
    public bool VoiceChanger { get; set; }

    /// <summary>AI 变声模块的展开状态。</summary>
    public bool AiVoice { get; set; }
}

public sealed class AppConfig
{
    /// <summary>
    /// 配置结构版本。**默认 0 表示"尚未迁移"**，`ConfigStore.Load` 会交给
    /// <see cref="ConfigMigrations.Apply"/> 补到当前版本。
    /// 刻意不用 <c>ConfigMigrations.Current</c> 当默认值：老配置的 JSON 里可能**没有** Version 字段，
    /// 那样反序列化后会保留默认值、被误判成"已是最新"而跳过迁移。
    /// </summary>
    public int Version { get; set; }

    /// <summary>总旁通开关。首次启动默认关闭（未开始处理），由用户主动开启。</summary>
    public bool AudioProcessingEnabled { get; set; }

    public string ToggleHotkey { get; set; } = string.Empty;
    /// <summary>
    /// 工具栏「监听」开关：**麦克风**是否送到监听设备。
    /// 它不再等于"监听设备开关"——播放器面板的「音频监听」也能单独把监听设备拉起来
    /// （见 <c>AudioEngine.ShouldRunMonitorPlayer</c>），因此这个字段只决定"麦克风要不要进监听"。
    /// </summary>
    public bool MonitorEnabled { get; set; }
    public bool AutoStart { get; set; }

    /// <summary>
    /// 关闭主窗口时收进系统托盘（而不是退出程序）。
    /// 早期版本的这个开关同时管「最小化到托盘」，而最小化本来就该只进任务栏，
    /// 现在该开关只作用于关闭按钮（最小化恢复为普通的任务栏最小化）。
    /// </summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>
    /// 深色配色。**默认开启**（2026-10-05 用户要求默认深色），由工具栏的「深色模式」开关切换。
    /// 配色本身由 <c>Ui/ThemeManager.cs</c> 决定，这里只存开/关。
    ///
    /// ⚠ 老配置（Version 1）里存的 <c>false</c> 是**当时的旧默认值**而非用户主动选择，
    /// 因此由 <see cref="ConfigMigrations"/> 一次性翻成 <c>true</c>；此后用户再关掉不会被改写。
    /// </summary>
    public bool DarkMode { get; set; } = true;

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

    /// <summary>变声（DSP 层）。</summary>
    public VoiceChangerSettings VoiceChanger { get; set; } = new();

    /// <summary>AI 变声（RVC）。位于降噪之后、DSP 变声之前。</summary>
    public AiVoiceSettings AiVoice { get; set; } = new();

    public GainSettings Gain { get; set; } = new();
    public PlayerSettings Player { get; set; } = new();

    [JsonIgnore]
    public bool Migrated => Version < 1;
}
