using MicMate.Core;
using MicMate.Dsp;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicMate.Audio;

/// <summary>
/// 音频引擎。数据流（项目书 4.3）：
/// WASAPI 采集 → BufferedWaveProvider → 噪声门 → AI降噪 → 响度平衡 → 音色风格 → 效果器 → 增益
///   → 与播放器音频 50:50 混合 → 软限幅 → 主输出（MIXLINE）+ 监听输出（物理扬声器）
/// 采集点与最终混音点各接一个分析采样点，为实时频谱提供数据。
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int SampleRate = 48000;
    private const int CaptureBufferMilliseconds = 80;
    private const int MonitorBufferMilliseconds = 250;

    private readonly object _gate = new();
    private readonly DeviceService _devices;
    private readonly AppConfig _config;
    private readonly OutputBus Bus;
    private SoftLimiterEffect _limiter;
    private MonoToStereoProvider _stereo;

    private BufferedWaveProvider? _captureBuffer;
    private WasapiRecorder? _recorder;
    private MMDevice? _inputDevice;
    private MMDevice? _outputDevice;
    private MMDevice? _monitorDevice;
    private WasapiPlayer? _mainPlayer;
    private WasapiPlayer? _monitorPlayer;
    private BufferedWaveProvider? _monitorBuffer;
    private bool _disposed;

    public AudioEngine(DeviceService devices, AppConfig config)
    {
        _devices = devices;
        _config = config;
        Format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);

        InputSpectrum = new SpectrumAnalyzer();
        OutputSpectrum = new SpectrumAnalyzer();

        RawTap = new InputTapSource(Format, InputSpectrum);

        NoiseGate = new NoiseGateEffect(Format, config.NoiseGate);
        Denoise = new DenoiseEffect(Format, config.Denoise, new SpectralDenoiseModel());
        Loudness = new LoudnessBalanceEffect(Format, config.Loudness);
        Tone = new ToneStyleEffect(Format, config.Tone);
        Creative = new CreativeEffect(Format, config.Effect);
        Gain = new GainEffect(Format, config.Gain);

        Chain = new DynamicChain(RawTap);
        WetTap = new InputTapSource(Chain, OutputSpectrum);
        MicMixer = new MicMixer(Format, WetTap);
        Bus = new OutputBus(MicMixer, OutputSpectrum);
        _limiter = new SoftLimiterEffect(Format);
        _stereo = new MonoToStereoProvider(_limiter);
        ApplyChain();
        UpdateAllParameters();
    }

    // ------------------------------------------------------------- 对外状态

    public WaveFormat Format { get; }

    public bool IsRunning { get; private set; }

    public string? LastError { get; private set; }

    public int CaptureLatencyMs { get; private set; }

    public int OutputLatencyMs { get; private set; }

    public bool LowLatencyActive { get; private set; }

    public DynamicChain Chain { get; }

    public MicMixer MicMixer { get; }

    public NoiseGateEffect NoiseGate { get; }
    public DenoiseEffect Denoise { get; }
    public LoudnessBalanceEffect Loudness { get; }
    public ToneStyleEffect Tone { get; }
    public CreativeEffect Creative { get; }
    public GainEffect Gain { get; }

    public SpectrumAnalyzer InputSpectrum { get; }
    public SpectrumAnalyzer OutputSpectrum { get; }

    /// <summary>采集侧原始信号（输入频谱在这里取样）。</summary>
    public InputTapSource RawTap { get; }

    /// <summary>效果链输出（混音前的处理后麦克风信号）。</summary>
    public InputTapSource WetTap { get; }

    /// <summary>是否把最终音频发送到监听设备。</summary>
    public bool MonitorEnabled => _monitorPlayer != null;

    /// <summary>诊断用：混音输出被音频线程拉取的次数。</summary>
    public long MixReadCount => MicMixer.ReadCount;

    /// <summary>诊断用：输出尾链（混音之后）被拉取的次数。</summary>
    public long BusReadCount => Bus.ReadCount;

    // ------------------------------------------------------------- 启动/停止

    public void Start()
    {
        lock (_gate)
        {
            if (IsRunning) return;
            try
            {
                StartCore();
                IsRunning = true;
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Error("音频引擎启动失败", ex);
                StopCore();
                IsRunning = false;
            }
        }
    }

    private void StartCore()
    {
        _inputDevice = _devices.GetDevice(_config.Devices.InputDeviceId, DataFlow.Capture)
                        ?? throw new InvalidOperationException("未找到可用的录音设备。");

        _captureBuffer = new BufferedWaveProvider(Format, TimeSpan.FromMilliseconds(CaptureBufferMilliseconds))
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };

        RawTap.SetSource(_captureBuffer.ToSampleProvider());

        _recorder = BuildRecorderAndStart(_inputDevice);
        LowLatencyActive = _recorder.LowLatencyActive;
        CaptureLatencyMs = _recorder.LatencyMilliseconds;
        _recorder.DataAvailable += OnDataAvailable;
        _recorder.RecordingStopped += OnRecordingStopped;

        BuildMainPlayer(_config.Devices.OutputDeviceId);

        SyncMonitor(force: true);
        Log.Info($"音频引擎已启动：输入「{_inputDevice.FriendlyName}」→ 输出「{_outputDevice?.FriendlyName ?? "系统默认"}」" +
                 $"，监听「{(_monitorDevice != null ? _monitorDevice.FriendlyName : "关闭")}」" +
                 $"，低延迟 {LowLatencyActive}，采集 {CaptureLatencyMs} ms / 输出 {OutputLatencyMs} ms");
    }

    /// <summary>
    /// 采集侧：把「构建 + 开始录音」放进同一个降级循环。
    /// NAudio 的 WithRawMode / WithLowLatency 有两种失败时机（构建时抛错，或构建成功但
    /// 打开端点时才抛错），因此两者都必须覆盖，否则整条链路会启动失败。
    /// </summary>
    private WasapiRecorder BuildRecorderAndStart(MMDevice device)
    {
        var attempts = new (string Name, bool LowLatency, bool Raw)[]
        {
            ("低延迟+原始模式", true, true),
            ("原始模式", false, true),
            ("低延迟", true, false),
            ("标准共享模式", false, false),
        };

        Exception? last = null;
        foreach (var attempt in attempts)
        {
            WasapiRecorder? recorder = null;
            try
            {
                var builder = new WasapiRecorderBuilder()
                    .WithDevice(device)
                    .WithSharedMode()
                    .WithMmcssThreadPriority("Pro Audio")
                    .WithBufferLength(CaptureBufferMilliseconds)
                    .WithFormat(Format);

                if (attempt.LowLatency) builder = builder.WithLowLatency();
                if (attempt.Raw) builder = builder.WithRawMode();

                recorder = builder.Build();
                recorder.StartRecording();

                LogFallback("采集侧", attempt.Name, last);
                return recorder;
            }
            catch (Exception ex)
            {
                last = ex;
                Log.Warn($"采集侧「{attempt.Name}」不可用：{ex.GetType().Name}: {ex.Message}");
                try
                {
                    recorder?.StopRecording();
                    recorder?.Dispose();
                }
                catch
                {
                }
            }
        }

        throw last ?? new InvalidOperationException("无法创建采集流。");
    }

    /// <summary>
    /// 创建主输出。Init 也可能因为端点不支持 RAW 而抛错，因此把「创建 + Init」放在同一个降级循环里。
    /// 关键：每一级都必须使用**全新**的尾链（软限幅 + 单声道转立体声）。
    /// 这些节点内部有状态（限幅器包络、效果器缓冲），一旦某次 Init 失败，
    /// 复用同一实例会让后续尝试拿到损坏的 source，报出误导性异常
    /// （例如已关掉 RAW 却仍报 “Raw mode is not supported”）。
    /// </summary>
    private void BuildMainPlayer(string? deviceId)
    {
        var attempts = new (string Name, bool LowLatency, bool Raw)[]
        {
            ("低延迟+原始模式", true, true),
            ("低延迟", true, false),
            ("标准共享模式", false, false),
        };

        Exception? last = null;
        foreach (var attempt in attempts)
        {
            WasapiPlayer? player = null;
            MMDevice? device = null;

            // 每一级都重建尾链，绝不复用上一次失败过的实例
            var limiter = new SoftLimiterEffect(Bus);
            var stereo = new MonoToStereoProvider(limiter);

            try
            {
                player = BuildPlayer(deviceId, attempt.LowLatency, attempt.Raw, out device);
                player.Init(stereo);
                player.Play();

                _mainPlayer = player;
                _outputDevice = device;
                _limiter = limiter;
                _stereo = stereo;
                OutputLatencyMs = player.LatencyMilliseconds;
                LogFallback("输出侧", attempt.Name, last);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Log.Warn($"输出侧「{attempt.Name}」不可用：{ex.GetType().Name}: {ex.Message}");
                try
                {
                    player?.Stop();
                    player?.Dispose();
                }
                catch
                {
                }

                device?.Dispose();
            }
        }

        throw last ?? new InvalidOperationException("无法创建播放流。");
    }

    private static void LogFallback(string side, string name, Exception? error)
    {
        if (name == "低延迟+原始模式") return;
        var reason = error == null ? string.Empty : $"（上级不可用：{error.Message}）";
        Log.Warn($"{side}使用「{name}」{reason}");
    }

    /// <summary>按指定模式创建 WasapiPlayer（只创建，不 Init）。</summary>
    private WasapiPlayer BuildPlayer(string? deviceId, bool lowLatency, bool raw, out MMDevice? device)
    {
        device = _devices.GetDevice(deviceId, DataFlow.Render);

        var builder = new WasapiPlayerBuilder()
            .WithSharedMode()
            .WithMmcssThreadPriority("Pro Audio");

        if (device != null) builder = builder.WithDevice(device);
        if (lowLatency) builder = builder.WithLowLatency();
        if (raw) builder = builder.WithRawMode();

        return builder.Build();
    }

    /// <summary>监听输出：同样每次尝试都使用全新的尾链。</summary>
    private void BuildMonitorPlayer()
    {
        var attempts = new (string Name, bool LowLatency, bool Raw)[]
        {
            ("低延迟+原始模式", true, true),
            ("低延迟", true, false),
            ("标准共享模式", false, false),
        };

        Exception? last = null;
        foreach (var attempt in attempts)
        {
            WasapiPlayer? player = null;
            MMDevice? device = null;

            // 监听直接播放「混音总线复制出来的环形缓冲」，不参与主输出链的拉取。
            // 它没有上游需要处理，因此这里不需要限幅器/立体声转换节点链。
            var monitorBuffer = new BufferedWaveProvider(
                WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1),
                TimeSpan.FromMilliseconds(MonitorBufferMilliseconds))
            {
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };

            try
            {
                player = BuildPlayer(_config.Devices.MonitorDeviceId, attempt.LowLatency, attempt.Raw, out device);
                player.Init(new MonoToStereoProvider(monitorBuffer.ToSampleProvider()));
                player.Play();

                _monitorPlayer = player;
                _monitorDevice = device;
                _monitorBuffer = monitorBuffer;
                LogFallback("监听侧", attempt.Name, last);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Log.Warn($"监听侧「{attempt.Name}」不可用：{ex.GetType().Name}: {ex.Message}");
                try
                {
                    player?.Stop();
                    player?.Dispose();
                }
                catch
                {
                }

                device?.Dispose();
            }
        }

        throw last ?? new InvalidOperationException("无法创建监听播放流。");
    }

    /// <summary>根据配置开启/关闭监听输出。</summary>
    private void SyncMonitor(bool force = false)
    {
        var shouldEnable = _config.MonitorEnabled;

        if (shouldEnable)
        {
            if (_monitorPlayer != null && !force) return;

            Bus.MonitorBuffer = null;
            _monitorBuffer = null;

            try
            {
                BuildMonitorPlayer();
                Bus.MonitorBuffer = _monitorBuffer;
                Log.Info("监听输出已开启：" + (_monitorDevice?.FriendlyName ?? "系统默认扬声器"));
            }
            catch (Exception ex)
            {
                Log.Error("监听输出启动失败", ex);
                _monitorPlayer?.Dispose();
                _monitorPlayer = null;
                _monitorDevice = null;
                _monitorBuffer = null;
                Bus.MonitorBuffer = null;
            }
        }
        else
        {
            Bus.MonitorBuffer = null;
            try
            {
                _monitorPlayer?.Stop();
                _monitorPlayer?.Dispose();
            }
            catch
            {
            }

            _monitorPlayer = null;
            _monitorBuffer = null;
            _monitorDevice = null;
            Log.Info("监听输出已关闭");
        }
    }

    /// <summary>UI 上的监听开关。</summary>
    public void SetMonitorEnabled(bool enabled)
    {
        _config.MonitorEnabled = enabled;
        SyncMonitor();
    }

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        var capture = _captureBuffer;
        if (capture == null) return;

        try
        {
            // 送入分析器（输入频谱看的就是原始麦克风信号）
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer);
            RawTap.Feed(samples);
            capture.AddSamples(buffer);
        }
        catch (Exception ex)
        {
            Log.Error("采集数据处理失败", ex);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            Log.Error("录音流异常停止", e.Exception);
        else
            Log.Warn("录音流已停止。");
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopCore();
            IsRunning = false;
        }
    }

    private void StopCore()
    {
        try
        {
            if (_recorder != null)
            {
                _recorder.DataAvailable -= OnDataAvailable;
                _recorder.RecordingStopped -= OnRecordingStopped;
                _recorder.StopRecording();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("停止采集失败：" + ex.Message);
        }

        try
        {
            _mainPlayer?.Stop();
        }
        catch
        {
        }

        try
        {
            _monitorPlayer?.Stop();
        }
        catch
        {
        }

        _recorder?.Dispose();
        _recorder = null;
        _mainPlayer?.Dispose();
        _mainPlayer = null;
        _monitorPlayer?.Dispose();
        _monitorPlayer = null;
        _inputDevice?.Dispose();
        _inputDevice = null;
        _outputDevice?.Dispose();
        _outputDevice = null;
        _monitorDevice?.Dispose();
        _monitorDevice = null;
        _captureBuffer = null;
        _monitorBuffer = null;
        RawTap.SetSource(null);
    }

    // ------------------------------------------------------------- 处理链

    /// <summary>
    /// 按当前启用状态重建处理链。未启用的模块从链中物理移除（项目书 4.4），顺序固定不可拖拽。
    /// 音色风格 / 效果器在「尚未选择具体预设」时同样不参与处理。
    ///
    /// 麦克风是否送出完全由总开关决定：
    ///   · 总开关打开 → 出声（链上有模块就处理，链为空则直通）
    ///   · 总开关关闭 → 静音，对应项目书 §3.7.1 的「一键闭麦」
    /// </summary>
    public void ApplyChain()
    {
        var desired = new List<IAudioEffect>
        {
            NoiseGate, Denoise, Loudness, Tone, Creative, Gain,
        }.Where(e => e.Enabled).ToList();

        var current = Chain.Effects;
        var chainChanged = current.Count != desired.Count
                           || !current.Zip(desired).All(p => ReferenceEquals(p.First, p.Second));
        if (chainChanged)
        {
            Chain.SetEffects(desired);
            Log.Debug("处理链已更新：" + (desired.Count == 0
                ? "（空，直通）"
                : string.Join(" → ", desired.Select(e => e.Name))));
        }

        var micEnabled = _config.AudioProcessingEnabled && !_startupMute;
        if (MicMixer.MicEnabled != micEnabled)
        {
            MicMixer.MicEnabled = micEnabled;
            Log.Info(micEnabled
                ? $"麦克风输出：开启（{(desired.Count == 0 ? "直通，未启用任何模块" : string.Join(" → ", desired.Select(e => e.Name)))}）"
                : _startupMute ? "麦克风输出：静音（启动中，等待降噪模型就绪）" : "麦克风输出：静音（总开关关闭）");
        }
    }

    /// <summary>把配置中的全部参数写入 DSP 状态，并据此重建处理链。</summary>
    public void UpdateAllParameters()
    {
        NoiseGate.Enabled = _config.NoiseGate.Enabled;
        Denoise.Enabled = _config.Denoise.Enabled;
        Loudness.Enabled = _config.Loudness.Enabled;
        // 音色风格 / 效果器：开关打开但未选择预设时不进入处理链，等价于关闭
        Tone.Enabled = _config.Tone.Enabled && _config.Tone.Style.HasValue;
        Creative.Enabled = _config.Effect.Enabled && _config.Effect.Kind.HasValue;
        Gain.Enabled = _config.Gain.Enabled;

        NoiseGate.UpdateParameters();
        Denoise.UpdateParameters();
        Loudness.UpdateParameters();
        Tone.UpdateParameters();
        Creative.UpdateParameters();
        Gain.UpdateParameters();

        ApplyChain();

        // 把实际生效的链路记进日志，便于排查"重启后模块没生效"这类问题
        Log.Info($"处理链状态：总开关={_config.AudioProcessingEnabled}，" +
                 $"噪声门={NoiseGate.Enabled}，降噪={Denoise.Enabled}（{Denoise.ModelName}），" +
                 $"响度={Loudness.Enabled}，音色={Tone.Enabled}，效果={Creative.Enabled}，增益={Gain.Enabled}");
    }

    public void SetDenoiseModel(IDenoiseModel model)
    {
        Denoise.SetModel(model);
        Denoise.UpdateParameters();

        // 换模型后必须重建处理链：SetModel 会重置内部帧缓冲，
        // 而链是否包含降噪由 Enabled 决定。
        ApplyChain();
    }

    /// <summary>
    /// 启动静音门：软件刚打开、降噪模型还没重新套用时，
    /// 麦克风输出保持静音，避免把"未经降噪的底噪"先送出去一段时间。
    /// 模型应用完成后置回 false 即恢复输出。
    /// </summary>
    public bool StartupMute
    {
        get => _startupMute;
        set
        {
            if (_startupMute == value) return;
            _startupMute = value;
            ApplyChain();
            Log.Info(value ? "启动静音：等待降噪模型就绪" : "启动静音解除，恢复麦克风输出");
        }
    }

    private bool _startupMute;

    /// <summary>当前降噪模型名称（供界面提示与诊断）。</summary>
    public string DenoiseModelName => Denoise.ModelName;

    /// <summary>诊断用：降噪是否在链中、处理了多少帧、被绕过多少样本。</summary>
    public string DenoiseDiagnostics =>
        $"降噪 Enabled={Denoise.Enabled}，链中包含={Chain.Effects.Contains(Denoise)}，" +
        $"已处理 {Denoise.ProcessedFrames} 帧，被绕过 {Denoise.BypassedSamples} 样本（模型 {Denoise.ModelName}）";

    /// <summary>切换输入/输出/监听设备（重启音频流）。</summary>
    public void Reconfigure(string? inputId, string? outputId, string? monitorId)
    {
        var wasRunning = IsRunning;
        if (wasRunning) Stop();

        _config.Devices.InputDeviceId = inputId;
        _config.Devices.OutputDeviceId = outputId;
        _config.Devices.MonitorDeviceId = monitorId;

        if (wasRunning) Start();
    }

    /// <summary>UI 线程按帧率调用，刷新两路频谱与电平。</summary>
    public void UpdateAnalysis()
    {
        InputSpectrum.TryUpdate();
        OutputSpectrum.TryUpdate();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        Denoise.Model.Dispose();
        Chain.Reset();
    }
}
