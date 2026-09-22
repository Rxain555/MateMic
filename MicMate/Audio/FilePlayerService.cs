using MicMate.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicMate.Audio;

/// <summary>
/// 音频文件播放器。支持 WAV / MP3 / M4A(AAC) / WMA 等 Media Foundation 可解码的格式。
/// 播放数据不经过效果链，直接送入 <see cref="Dsp.MicMixer"/>，在处理后的麦克风信号输出前
/// 以固定 50:50 比例混入（项目书 3.2.2）。
/// </summary>
public sealed class FilePlayerService : IDisposable
{
    private const float MaxPlayerGain = 2f;   // 播放器音量上限（+6 dB）

    private readonly PlayerSampleBuffer _buffer = new(TimeSpan.FromMilliseconds(1000));
    private readonly CancellationTokenSource _shutdown = new();
    private readonly float[] _blockBuffer = new float[4800];   // 100 ms @ 48 kHz

    private Thread? _worker;
    private volatile bool _stopRequested;
    private volatile string? _pendingPath;

    /// <summary>循环播放开关。</summary>
    public volatile bool LoopEnabled;

    /// <summary>播放音量（线性，0–2）。</summary>
    public volatile float Volume = 0.8f;

    /// <summary>正在播放的文件路径（null 表示空闲）。</summary>
    public volatile string? CurrentPath;

    /// <summary>播放状态变化（开始/结束/切换），用于 UI 刷新。</summary>
    public event EventHandler? StateChanged;

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

        _stopRequested = false;
        _pendingPath = path;
        StartWorker();
    }

    /// <summary>停止播放。</summary>
    public void Stop()
    {
        _stopRequested = true;
        _pendingPath = null;
        CurrentPath = null;
        _buffer.Clear();
        RaiseStateChanged();
    }

    private void StartWorker()
    {
        if (_worker is { IsAlive: true }) return;

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "MicMate.Player",
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

            try
            {
                RenderFile(path);

                if (LoopEnabled && !_stopRequested)
                {
                    // 循环播放：重新排队同一个文件
                    _stopRequested = false;
                    _pendingPath = path;
                }
                else
                {
                    CurrentPath = null;
                    RaiseStateChanged();
                    Log.Info("播放结束：" + Path.GetFileName(path));
                }
            }
            catch (Exception ex)
            {
                Log.Error($"播放 {Path.GetFileName(path)} 失败", ex);
                CurrentPath = null;
                RaiseStateChanged();
                Thread.Sleep(150);
            }
        }
    }

    private void RenderFile(string path)
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

        CurrentPath = path;
        _buffer.Clear();
        RaiseStateChanged();
        Log.Info("开始播放：" + Path.GetFileName(path));

        while (!_stopRequested && !_shutdown.IsCancellationRequested)
        {
            var read = provider.Read(_blockBuffer);
            if (read <= 0) break;

            var samples = _blockBuffer.AsSpan(0, read);
            var volume = Volume;
            if (volume <= 0.0001f) samples.Clear();
            else if (MathF.Abs(volume - 1f) > 0.0001f)
                for (var i = 0; i < samples.Length; i++) samples[i] *= volume;

            _buffer.AddSamples(samples);

            // 背压：缓冲接近上限时等待播放侧消费，避免内存无界增长
            var waited = 0;
            while (_buffer.BufferedDuration > TimeSpan.FromMilliseconds(400)
                   && !_stopRequested && !_shutdown.IsCancellationRequested)
            {
                Thread.Sleep(5);
                if (++waited > 400) break;
            }
        }
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        try
        {
            _stopRequested = true;
            _shutdown.Cancel();
            _worker?.Join(500);
        }
        catch
        {
        }

        _shutdown.Dispose();
    }
}
