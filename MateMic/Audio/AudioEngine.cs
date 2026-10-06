using MateMic.Core;
using MateMic.Dsp;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MateMic.Audio;

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

    /// <summary>当前真实打开的输入设备名（可能与配置里的首选设备不同，见 <see cref="IsOnFallbackInput"/>）。</summary>
    private string? _actualInputName;

    /// <summary>true = 首选输入设备缺失，当前临时跑在系统默认录音设备上。</summary>
    private bool _runningOnFallbackInput;

    /// <summary>采集回调累计次数。用于"设备是否真的在送数据"的健康校验，见 VerifyCaptureFlow。</summary>
    private long _captureCallbackCount;

    /// <summary>本次 StartCore 是"启动"还是"热插拔恢复"触发的，仅用于日志措辞。</summary>
    private string _startReason = "启动";

    /// <summary>采集流"建好但收不到数据"时还允许自动重建几次。</summary>
    private int _repairAttemptsLeft = MaxCaptureRepairAttempts;

    /// <summary>采集流自动重建的最大次数（够覆盖一次拔插带来的失败，又不会无限刷屏）。</summary>
    private const int MaxCaptureRepairAttempts = 2;

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
        // 变声紧跟在降噪之后：先拿到干净语音，再做音色变换
        VoiceChanger = new VoiceChangerEffect(Format, config.VoiceChanger);
        Loudness = new LoudnessBalanceEffect(Format, config.Loudness);
        Tone = new ToneStyleEffect(Format, config.Tone);
        Creative = new CreativeEffect(Format, config.Effect);
        Gain = new GainEffect(Format, config.Gain);

        Chain = new DynamicChain(RawTap);
        MicMixer = new MicMixer(Format, Chain);
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

    /// <summary>变声（DSP 层，位于 AI 降噪之后）。</summary>
    public VoiceChangerEffect VoiceChanger { get; }

    public LoudnessBalanceEffect Loudness { get; }
    public ToneStyleEffect Tone { get; }
    public CreativeEffect Creative { get; }
    public GainEffect Gain { get; }

    public SpectrumAnalyzer InputSpectrum { get; }
    public SpectrumAnalyzer OutputSpectrum { get; }

    /// <summary>采集侧原始信号（输入频谱在这里取样）。</summary>
    public InputTapSource RawTap { get; }

    /// <summary>
    /// 输出频谱/电平的唯一取样点是 <see cref="Bus"/>（混音之后）。
    /// 这里**不能**再给效果链输出套一个分析采样点：两路同时喂同一个分析器，
    /// 进入 FFT 的就成了"链路输出 + 混音输出"交替拼接的序列，
    /// 频谱与 RMS/峰值全都不代表真实输出（历史 bug）。
    /// </summary>

    /// <summary>是否把最终音频发送到监听设备。</summary>
    public bool MonitorEnabled => _monitorPlayer != null;

    /// <summary>监听侧已写入的样本数（诊断用：确认「单独监听」真的在送数据）。</summary>
    public long MonitorSamples => MicMixer.MonitorSamples;

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

                // 首选输入设备缺失时降级到系统默认设备再试一次：
                // 麦克风被拔掉的那段时间里，主输出（MIXLINE）与播放器不该跟着一起哑掉，
                // 否则"拔一下麦克风"就等于软件整体失效。配置里的首选 ID 原样保留，
                // 等设备插回来由 RecoverDevices 夺回。
                if (!_devices.IsPresent(_config.Devices.InputDeviceId, DataFlow.Capture))
                {
                    try
                    {
                        Log.Warn("首选输入设备当前不可用，临时改用系统默认录音设备。");
                        StartCore();
                        IsRunning = true;
                        LastError = null;
                        _runningOnFallbackInput = true;
                    }
                    catch (Exception retry)
                    {
                        LastError = retry.Message;
                        Log.Error("改用系统默认录音设备后仍然启动失败", retry);
                        StopCore();
                        IsRunning = false;
                    }
                }
            }
        }
    }

    private void StartCore()
    {
        _inputDevice = _devices.GetDevice(_config.Devices.InputDeviceId, DataFlow.Capture, out var resolvedInputId)
                        ?? throw new InvalidOperationException("未找到可用的录音设备。");

        // 设备重插后 ID 可能变化：DeviceService 会按名字兜底找到同一支设备，
        // 并把真实 ID 回传过来。这里把它写回配置，让"自动纠正"只发生一次。
        if (!string.IsNullOrWhiteSpace(resolvedInputId) &&
            !string.Equals(resolvedInputId, _config.Devices.InputDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            Log.Info("输入设备 ID 已自动更新为当前系统分配的 ID（原 ID 已失效）。");
            _config.Devices.InputDeviceId = resolvedInputId;
        }

        // 记下真正打开的设备名：与配置里的首选设备不一致就说明当前处于"降级"状态
        _actualInputName = _inputDevice.FriendlyName;
        _runningOnFallbackInput = !string.IsNullOrWhiteSpace(_config.Devices.InputDeviceId) &&
                                  !string.Equals(_config.Devices.InputDeviceId, _inputDevice.ID,
                                      StringComparison.OrdinalIgnoreCase);

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

        // 流建起来了不等于数据进得来（设备可能处于"假存活"状态），单独核对一次
        VerifyCaptureFlow(_startReason);
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
        device = _devices.GetDevice(deviceId, DataFlow.Render, out var resolvedId);

        // 输出/监听设备 ID 变化时自动纠正。
        // 注意判断顺序：先比 OutputDeviceId，且不能直接改 _config 再比 MonitorDeviceId，
        // 否则两个 ID 相同时（把输出与监听设成同一台设备是允许的）会误判。
        if (!string.IsNullOrWhiteSpace(resolvedId))
        {
            if (string.Equals(deviceId, _config.Devices.OutputDeviceId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(resolvedId, _config.Devices.OutputDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info("输出设备 ID 已自动更新为当前系统分配的 ID（原 ID 已失效）。");
                _config.Devices.OutputDeviceId = resolvedId;
            }
            else if (string.Equals(deviceId, _config.Devices.MonitorDeviceId, StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(resolvedId, _config.Devices.MonitorDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info("监听设备 ID 已自动更新为当前系统分配的 ID（原 ID 已失效）。");
                _config.Devices.MonitorDeviceId = resolvedId;
            }
        }

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

            // 监听读的是「混音总线按监听比例复制出来的环形缓冲」，不参与主输出链的拉取。
            // 它上游只有环形缓冲，但同样要过软限幅 + 单声道转立体声，否则监听里的
            // 混音峰值会比主输出高（主输出那边有限幅器，两边听感对不上）。
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
                var monitorChain = new SoftLimiterEffect(monitorBuffer.ToSampleProvider());
                player.Init(new MonoToStereoProvider(monitorChain));
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

    /// <summary>
    /// 监听设备（物理扬声器）需不需要开着。两个开关里的**任意一个**都要它：
    ///   · 工具栏「监听」——要听麦克风；
    ///   · 播放器面板「音频监听」——要听播放器。
    /// 因此监听设备的生命周期由这个并集决定，具体能听到什么再由两个闸分别控制
    /// （麦克风进监听 = <see cref="SyncMonitorMic"/>，播放器是否进监听 = <c>MicMixer.PlayerMonitorEnabled</c>）。
    ///
    /// 注意播放器**送主输出**与这个并集无关：它始终参与主输出，
    /// 所以"播给别人听"从来不需要打开监听。
    /// </summary>
    private bool ShouldRunMonitorPlayer()
        => _config.MonitorEnabled || _config.Player.AudioMonitor;

    /// <summary>按当前两个开关开启/关闭监听设备，并同步"麦克风是否进监听"。</summary>
    private void SyncMonitor(bool force = false)
    {
        var shouldEnable = ShouldRunMonitorPlayer();

        if (shouldEnable)
        {
            if (_monitorPlayer != null && !force)
            {
                SyncMonitorMic();
                return;
            }

            MicMixer.SetMonitorSink(null);
            _monitorBuffer = null;

            try
            {
                BuildMonitorPlayer();
                MicMixer.SetMonitorSink(new MonitorBufferSink(_monitorBuffer!));
                SyncMonitorMic();
                Log.Info("监听输出已开启：" + (_monitorDevice?.FriendlyName ?? "系统默认扬声器") +
                         $"，麦克风进监听={_config.MonitorEnabled}，播放器进监听={_config.Player.AudioMonitor}");
            }
            catch (Exception ex)
            {
                Log.Error("监听输出启动失败", ex);
                _monitorPlayer?.Dispose();
                _monitorPlayer = null;
                _monitorDevice = null;
                _monitorBuffer = null;
                MicMixer.SetMonitorSink(null);
                MicMixer.MonitorMicEnabled = false;
            }
        }
        else
        {
            MicMixer.SetMonitorSink(null);
            MicMixer.MonitorMicEnabled = false;
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
            Log.Info("监听输出已关闭（麦克风监听与音频监听都关着）");
        }
    }

    /// <summary>工具栏「监听」开关：让麦克风进监听（播放器的音频监听是另一个开关）。</summary>
    public void SetMonitorEnabled(bool enabled)
    {
        _config.MonitorEnabled = enabled;

        // 一律 force：这次切换会同时改变"麦克风是否进监听"，
        // 而且开着音频监听时要靠它把监听设备拉起来 / 在都关时放掉。
        SyncMonitor(force: true);
    }

    /// <summary>
    /// 麦克风进入监听的总闸。**监听设备存在**且**工具栏「监听」开着**才放行——
    /// 少了后一个条件，用户关掉监听却打开音频监听时，会连自己的麦克风一起听到。
    /// </summary>
    private void SyncMonitorMic()
    {
        MicMixer.MonitorMicEnabled = _monitorPlayer != null && _config.MonitorEnabled;
    }

    /// <summary>
    /// 「音频监听」开关：播放器（伴奏/语音包）要不要**额外**送进监听设备（物理扬声器）。
    /// 播放器音频本来就会进主输出（MIXLINE，队友/对面能听到），这个开关只影响"我自己听不听得到"；
    /// 打开它时即使工具栏「监听」关着，也会单独把监听设备拉起来。
    /// </summary>
    public void SetAudioMonitor(bool enabled)
    {
        _config.Player.AudioMonitor = enabled;
        MicMixer.PlayerMonitorEnabled = enabled;

        // 只开音频监听时也要有监听设备；两个都关时要把设备放掉
        SyncMonitor(force: true);

        Log.Info(enabled
            ? "音频监听：开（播放器音频除主输出外，也送监听设备）"
            : "音频监听：关（播放器音频只送主输出，本地监听里没有它）");
    }

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        var capture = _captureBuffer;
        if (capture == null) return;

        // 只做一次自增：这是"设备到底有没有在真的送数据"的唯一客观依据。
        // 用一个 long 而不是 bool，是为了在日志里给出"收到了多少次回调"这种可读证据。
        Interlocked.Increment(ref _captureCallbackCount);

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

    /// <summary>
    /// 启动后 3 秒核对"采集流是否真的在送数据"。
    ///
    /// 为什么必须有这一步：设备被拔掉时 WASAPI 会把流停掉，但**流的对象可能还活着**
    /// （`IsRunning` 仍为 true），此时界面看起来一切正常、实际上一个样本都进不来。
    /// 用户报的"拔了再插必须手动重选设备"里，有一部分就是这种"假存活"状态。
    /// 这里用回调计数把它变成一条明确的日志，而不是让用户去猜。
    /// </summary>
    private void VerifyCaptureFlow(string reason)
    {
        var baseline = Interlocked.Read(ref _captureCallbackCount);
        var attemptsLeft = _repairAttemptsLeft;
        Task.Run(async () =>
        {
            try
            {
                // 设备刚插上时系统需要一点时间才能真正开始供数，因此给足 3 秒
                await Task.Delay(3000).ConfigureAwait(false);
                var delta = Interlocked.Read(ref _captureCallbackCount) - baseline;
                if (delta > 0)
                {
                    Log.Info($"采集流健康检查通过（{reason}）：3 秒内收到 {delta} 次数据回调，" +
                             $"输入设备「{_actualInputName ?? "未知"}」。");
                    // 这次是好的，把重试额度补满，供下一次设备变化使用
                    _repairAttemptsLeft = MaxCaptureRepairAttempts;
                    return;
                }

                Log.Warn($"采集流健康检查未通过（{reason}）：3 秒内没有收到任何数据回调，" +
                         $"输入设备「{_actualInputName ?? "未知"}」。");

                if (attemptsLeft > 0)
                {
                    Log.Warn($"正在自动重建采集流（剩余重试 {attemptsLeft} 次）…");
                    RetryCaptureFlow(attemptsLeft - 1);
                }
                else
                {
                    Log.Warn("采集流自动重建已达上限仍然收不到数据，已停止重试。" +
                             "请检查：① Windows 设置 → 隐私和安全性 → 麦克风 是否允许桌面应用访问；" +
                             "② 设备是否被其它程序独占；③ 换一个输入设备试试。");
                }
            }
            catch (Exception ex)
            {
                Log.Debug("采集流健康检查异常（不影响音频）：" + ex.Message);
            }
        });
    }

    /// <summary>
    /// 健康检查未通过时自动重建采集流。
    ///
    /// 这是"选中了设备却仍然没有输入"的自动修复：流对象活着、IsRunning 也是 true，
    /// 但设备一个样本都不送。光记日志没用——用户还是得手动重选一次设备，
    /// 所以这里直接自动重建。重建会重走完整的降级链
    /// （低延迟+RAW → RAW → 低延迟 → 标准共享），通常换一档就能正常供数。
    /// 剩余次数由字段携带，避免重复排队检查；而且递减到 0 就停，
    /// 不会在设备真的坏掉时无限重启刷屏。
    /// </summary>
    private void RetryCaptureFlow(int attemptsLeft)
    {
        _repairAttemptsLeft = attemptsLeft;

        lock (_gate)
        {
            try
            {
                StopCore();
                StartCore();
                IsRunning = true;
                LastError = null;
                Log.Info($"采集流已重建（剩余重试 {attemptsLeft} 次），正在重新核对是否收到数据…");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Error("自动重建采集流失败", ex);
                StopCore();
                IsRunning = false;
            }
        }
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
        _actualInputName = null;
        _runningOnFallbackInput = false;
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
            NoiseGate, Denoise, VoiceChanger, Loudness, Tone, Creative, Gain,
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
            // 总开关也影响"麦克风要不要进监听"：关掉监听这一路也必须跟着静音
            SyncMonitorMic();
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
        VoiceChanger.Enabled = _config.VoiceChanger.Enabled;
        Loudness.Enabled = _config.Loudness.Enabled;
        // EQ 均衡器 / 效果器：开关打开但"没有任何实际作用"时不进入处理链，等价于关闭。
        // 均衡器看的是增益表是否平坦（选没选预设不重要——预设只是把曲线写进增益表）。
        Tone.Enabled = _config.Tone.Enabled && !EqPreset.IsFlat(_config.Tone.Gains);
        Creative.Enabled = _config.Effect.Enabled && _config.Effect.Kind.HasValue;
        Gain.Enabled = _config.Gain.Enabled;

        NoiseGate.UpdateParameters();
        Denoise.UpdateParameters();
        VoiceChanger.UpdateParameters();
        Loudness.UpdateParameters();
        Tone.UpdateParameters();
        Creative.UpdateParameters();
        Gain.UpdateParameters();

        ApplyChain();

        // 把实际生效的链路记进日志，便于排查"重启后模块没生效"这类问题
        Log.Info($"处理链状态：总开关={_config.AudioProcessingEnabled}，" +
                 $"噪声门={NoiseGate.Enabled}，降噪={Denoise.Enabled}（{Denoise.ModelName}），变声={VoiceChanger.Enabled}，" +
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

    // ------------------------------------------------------------- 热插拔恢复

    /// <summary>true = 首选输入设备缺失，当前临时跑在系统默认录音设备上。</summary>
    public bool IsOnFallbackInput => _runningOnFallbackInput;

    /// <summary>当前真实打开的输入设备名（诊断/提示用）。</summary>
    public string? ActualInputName => _actualInputName;

    /// <summary>
    /// 设备发生变化后调用：把"该开的流"重新对齐到"现在插着的设备"。
    /// 是否需要重建由 <see cref="DeviceRecoveryPolicy.Decide"/> 决定（纯函数，有回归探针覆盖）。
    /// </summary>
    /// <param name="deviceSetChanged">
    /// 本次触发是否真的带来了设备集合变化（轮询/通知都会给出这个结论）。
    /// </param>
    /// <returns>true = 本次调用重建了音频流。</returns>
    public bool RecoverDevices(bool deviceSetChanged)
    {
        var preferredPresent = _devices.IsPresent(_config.Devices.InputDeviceId, DataFlow.Capture)
                               || _devices.ResolvePreferred(_config.Devices.InputDeviceId, DataFlow.Capture) != null;

        var action = DeviceRecoveryPolicy.Decide(deviceSetChanged, IsRunning, _runningOnFallbackInput,
            preferredPresent);
        if (action == DeviceRecoveryAction.None)
        {
            return false;
        }

        Log.Info($"设备恢复：重建音频流（设备集合变化={deviceSetChanged}，流运行中={IsRunning}，" +
                 $"降级中={_runningOnFallbackInput}，首选可用={preferredPresent}）…");

        _startReason = "设备恢复";
        // 设备发生插拔意味着"收不到数据"的前提变了，重置自动修复额度再试一轮
        _repairAttemptsLeft = MaxCaptureRepairAttempts;
        Stop();
        Start();
        _startReason = "启动";

        if (!IsRunning)
        {
            Log.Warn("设备恢复失败：" + (LastError ?? "未知原因"));
            return false;
        }

        Log.Info(IsOnFallbackInput
            ? $"音频流已恢复，但仍在降级设备「{ActualInputName ?? "系统默认"}」上。"
            : $"音频流已恢复到「{ActualInputName ?? "未知设备"}」。");

        return true;
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
