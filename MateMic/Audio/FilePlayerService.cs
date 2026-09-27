using MateMic.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MateMic.Audio;

/// <summary>播放状态变化的原因（供界面决定是否要松开「同步按住键」）。</summary>
public enum PlaybackState
{
    /// <summary>开始播放一个文件。</summary>
    Started,

    /// <summary>播放自然结束（文件放完）。</summary>
    Finished,

    /// <summary>被用户显式停止。</summary>
    Stopped,

    /// <summary>被换曲打断或播放出错（后续还会有 Started / Stopped）。</summary>
    Interrupted,
}

/// <summary>
/// 音频文件播放器。支持 WAV / MP3 / M4A(AAC) / WMA 等 Media Foundation 可解码的格式。
/// 播放数据不经过效果链，直接送入 <see cref="Dsp.MicMixer"/>，在处理后的麦克风信号输出前
/// 以固定 50:50 比例混入（项目书 3.2.2）。
///
/// 换曲与停止用**代次（generation）**打断正在解码的旧曲：早期实现只设一个 `_pendingPath`，
/// 而正在 RenderFile 里的解码线程要等当前文件放完才会回头看到新路径，
/// 表现为「点第二个音频/按第二个语音包快捷键没有反应」（注释却写着"打断当前播放"）。
/// </summary>
public sealed class FilePlayerService : IDisposable
{
    private const float MaxPlayerGain = 2f;   // 播放器音量上限（+6 dB）

    /// <summary>
    /// 起播前的静音预卷时长（毫秒）。配合「同步按住键」使用：
    /// 按下发生在文件开始解码时，而声音要经过播放器缓冲才到混音总线，
    /// 因此先垫一段静音，让"按键已经按住"确定发生在音频真正出声之前
    /// （游戏/语音软件的按键说话需要一点时间才真正打开麦克风）。
    /// 代价是每次播放晚 (PreRollMs − 缓冲填充时间) 出声，用户感觉不到。
    /// </summary>
    private const int PreRollMilliseconds = 320;

    private readonly PlayerSampleBuffer _buffer = new(TimeSpan.FromMilliseconds(1000));
    private readonly CancellationTokenSource _shutdown = new();
    private readonly float[] _blockBuffer = new float[4800];   // 100 ms @ 48 kHz
    private readonly float[] _preRoll = new float[AudioEngine.SampleRate * PreRollMilliseconds / 1000];

    private Thread? _worker;
    private volatile string? _pendingPath;

    /// <summary>每次换曲/停止都 +1；正在解码的旧曲发现代次变了就立即退出。</summary>
    private int _generation;

    /// <summary>循环播放开关。</summary>
    public volatile bool LoopEnabled;

    /// <summary>播放音量（线性，0–2）。</summary>
    public volatile float Volume = 0.8f;

    /// <summary>正在播放的文件路径（null 表示空闲）。</summary>
    public volatile string? CurrentPath;

    /// <summary>播放状态变化（开始/结束/停止/打断），用于 UI 刷新与「同步按住键」。</summary>
    public event EventHandler<PlaybackState>? StateChanged;

    public ISampleProvider Output => _buffer.Reader;

    public bool IsPlaying => CurrentPath != null;

    /// <summary>滑块值 0–100 → 线性增益（0–2，+6 dB 上限）。</summary>
    public void SetVolumePercent(float percent0To100)
        => Volume = Math.Clamp(percent0To100, 0f, 100f) / 100f * MaxPlayerGain;

    /// <summary>开始播放一个文件（打断当前播放）。</summary>
    public void Play(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Log.Warn("播放失败，文件不存在：" + path);
            return;
        }

        Interlocked.Increment(ref _generation);   // 让正在解码的旧曲退出
        _buffer.Clear();                          // 丢掉上一首残留在缓冲里的音频
        _pendingPath = path;
        StartWorker();
    }

    /// <summary>停止播放（立即生效，不等当前文件放完）。</summary>
    public void Stop()
    {
        Interlocked.Increment(ref _generation);
        _pendingPath = null;
        CurrentPath = null;
        _buffer.Clear();
        RaiseStateChanged(PlaybackState.Stopped);
    }

    private bool IsCurrent(int generation) => generation == Volatile.Read(ref _generation);

    private void StartWorker()
    {
        if (_worker is { IsAlive: true }) return;

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "MateMic.Player",
            Priority = ThreadPriority.AboveNormal,
        };
        _worker.Start();
    }

    private void WorkerLoop()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            var path = _pendingPath;
            if (path == null)
            {
                Thread.Sleep(30);
                continue;
            }

            _pendingPath = null;
            var generation = Volatile.Read(ref _generation);

            try
            {
                var stats = RenderFile(path, generation);
                if (!IsCurrent(generation)) continue;   // 已被换曲/停止打断，不要再动状态

                if (LoopEnabled)
                {
                    // 循环播放：重新排队同一个文件（代次不变，所以不会自我打断）
                    _pendingPath = path;
                }
                else
                {
                    // 关键：解码到文件末尾 ≠ 声音放完。
                    // 环形缓冲里可能还压着几百毫秒没播出去的音频（背压水位 ~400 ms），
                    // 这里必须等它排空，"播放结束"才是真的结束。
                    // 否则「同步按住键」会在声音播完之前就松开——实测能早 300+ 毫秒。
                    var drained = WaitForBufferDrain(generation);

                    LastReleaseDiagnostics =
                        $"「{Path.GetFileName(path)}」解码 {stats.DecodedMs} ms，结束时缓冲还剩 {stats.TailMs} ms，" +
                        (drained ? "已等缓冲排空后再判定结束" : "等待排空时被打断（按停止/换曲处理）");

                    CurrentPath = null;
                    RaiseStateChanged(PlaybackState.Finished);
                    Log.Info("播放结束：" + Path.GetFileName(path) +
                             $"（解码 {stats.DecodedMs} ms，尾部缓冲 {stats.TailMs} ms）");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"播放 {Path.GetFileName(path)} 失败", ex);
                if (IsCurrent(generation))
                {
                    CurrentPath = null;
                    RaiseStateChanged(PlaybackState.Interrupted);
                }

                Thread.Sleep(150);
            }
        }
    }

    /// <summary>
    /// 解码循环结束后的收尾：等环形缓冲里剩下的音频真正播完。
    /// 返回 false 表示等待期间被停止/换曲打断了（此时不该再判定为"自然播放结束"）。
    /// </summary>
    private bool WaitForBufferDrain(int generation)
    {
        while (_buffer.BufferedDuration > TimeSpan.FromMilliseconds(20))
        {
            if (!IsCurrent(generation) || _shutdown.IsCancellationRequested) return false;
            Thread.Sleep(5);
        }

        return true;
    }

    /// <summary>
    /// 播放数据：解码出来的总时长与"解码结束时缓冲里还剩多少"。
    /// 用于确认同步按住键的松开时机（见 <see cref="LastReleaseDiagnostics"/>）。
    /// </summary>
    private readonly record struct RenderStats(int DecodedMs, int TailMs);

    /// <summary>最近一次播放结束时的收尾诊断（供界面/日志确认按住键松开的时机）。</summary>
    public string? LastReleaseDiagnostics { get; private set; }

    private RenderStats RenderFile(string path, int generation)
    {
        using var reader = new MediaFoundationReader(path);
        ISampleProvider provider = reader.ToSampleProvider();

        // 统一到 48 kHz / 单声道 float
        if (provider.WaveFormat.SampleRate != AudioEngine.SampleRate)
        {
            try
            {
                var waveProvider = new SampleToWaveProvider(provider);
                var resampled = new MediaFoundationResampler(waveProvider,
                    WaveFormat.CreateIeeeFloatWaveFormat(AudioEngine.SampleRate, provider.WaveFormat.Channels))
                {
                    ResamplerQuality = 60,
                };
                provider = resampled.ToSampleProvider();
            }
            catch (Exception ex)
            {
                Log.Warn("Media Foundation 重采样失败，改用 WDL 重采样：" + ex.Message);
                provider = new WdlResamplingSampleProvider(provider, AudioEngine.SampleRate);
            }
        }

        if (provider.WaveFormat.Channels != 1)
            provider = new StereoToMonoFloatProvider(provider);

        if (!IsCurrent(generation)) return default;   // 打开文件期间被换曲了

        CurrentPath = path;
        _buffer.Clear();
        _buffer.AddSamples(_preRoll);   // 起播预卷：见 PreRollMilliseconds
        RaiseStateChanged(PlaybackState.Started);
        Log.Info("开始播放：" + Path.GetFileName(path));

        var decodedSamples = 0;

        while (IsCurrent(generation) && !_shutdown.IsCancellationRequested)
        {
            var read = provider.Read(_blockBuffer);
            if (read <= 0) break;

            var samples = _blockBuffer.AsSpan(0, read);
            var volume = Volume;
            if (volume <= 0.0001f) samples.Clear();
            else if (MathF.Abs(volume - 1f) > 0.0001f)
                for (var i = 0; i < samples.Length; i++) samples[i] *= volume;

            _buffer.AddSamples(samples);
            decodedSamples += read;

            // 背压：缓冲接近上限时等待播放侧消费，避免内存无界增长
            var waited = 0;
            while (_buffer.BufferedDuration > TimeSpan.FromMilliseconds(400)
                   && IsCurrent(generation) && !_shutdown.IsCancellationRequested)
            {
                Thread.Sleep(5);
                if (++waited > 400) break;
            }
        }

        // 解码完最后一块时缓冲里还剩多少音频（含起播预卷）。
        // 这个值就是"松开按住键会早多少"的量级来源。
        var tailMs = (int)_buffer.BufferedDuration.TotalMilliseconds;
        var decodedMs = (int)(decodedSamples * 1000L / AudioEngine.SampleRate);

        return new RenderStats(decodedMs, tailMs);
    }

    private void RaiseStateChanged(PlaybackState state)
    {
        try
        {
            StateChanged?.Invoke(this, state);
        }
        catch (Exception ex)
        {
            Log.Warn("播放状态回调异常：" + ex.Message);
        }
    }

    public void Dispose()
    {
        try
        {
            Interlocked.Increment(ref _generation);
            _shutdown.Cancel();
            _worker?.Join(500);
        }
        catch
        {
        }

        _shutdown.Dispose();
    }
}
