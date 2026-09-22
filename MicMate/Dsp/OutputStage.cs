using NAudio.Wave;
using MicMate.Core;

namespace MicMate.Dsp;

/// <summary>6. 增益：处理链最后一级的输出电平调整（dB），增益变化平滑无爆音。</summary>
public sealed class GainEffect : IAudioEffect
{
    private readonly GainSettings _settings;
    private readonly int _sampleRate;
    private readonly NAudio.Effects.GainEffect _inner = new();

    public GainEffect(WaveFormat format, GainSettings settings)
    {
        WaveFormat = format;
        _settings = settings;
        _sampleRate = format.SampleRate;
        _inner.Configure(format);
        UpdateParameters();
    }

    public string Name => "增益";

    public bool Enabled { get; set; } = true;

    public WaveFormat WaveFormat { get; }

    public void UpdateParameters()
    {
        _inner.GainDb = Math.Clamp(_settings.GainDb, -24f, 24f);
    }

    public int Read(Span<float> buffer)
    {
        if (!Enabled) return buffer.Length;
        _inner.Process(buffer);
        return buffer.Length;
    }
}

/// <summary>
/// 输出级软限幅：软拐点压缩 + tanh 饱和，避免 50:50 混音后削波。
/// </summary>
public sealed class SoftLimiterEffect : ISampleProvider
{
    private const float Ceiling = 0.891f;   // −1 dBFS
    private readonly NAudio.Effects.LimiterEffect _limiter = new();
    private readonly ISampleProvider? _source;

    /// <summary>独立限幅器（自己不含上游，由调用方决定读取顺序）。</summary>
    public SoftLimiterEffect(WaveFormat format)
    {
        WaveFormat = format;
        Configure();
    }

    /// <summary>串联在给定上游之后的限幅器（Read 时先拉取上游再处理）。</summary>
    public SoftLimiterEffect(ISampleProvider source)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
        Configure();
    }

    private void Configure()
    {
        _limiter.CeilingDb = -1f;
        _limiter.ReleaseMs = 60f;
        _limiter.LookaheadMs = 1.5f;
        _limiter.OversampleFactor = 2;
        _limiter.Configure(WaveFormat);
    }

    public WaveFormat WaveFormat { get; }

    public float GainReductionDb => _limiter.GainReductionDb;

    public int Read(Span<float> buffer)
    {
        if (_source != null)
        {
            var read = _source.Read(buffer);
            if (read <= 0) return read;
            buffer = buffer[..read];
        }

        _limiter.Process(buffer);
        for (var i = 0; i < buffer.Length; i++)
        {
            // 软饱和兜底，避免任何瞬时过冲
            var x = buffer[i] / Ceiling;
            if (x > 1f || x < -1f)
                buffer[i] = MathF.Tanh(x * 0.8f) * Ceiling;
        }

        return buffer.Length;
    }
}

/// <summary>单声道 → 立体声（主输出与监听输出前统一为立体声）。</summary>
public sealed class MonoToStereoProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly float[] _scratch = new float[4096];

    public MonoToStereoProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> buffer)
    {
        var frames = buffer.Length / 2;
        var produced = 0;
        while (produced < frames)
        {
            var want = Math.Min(_scratch.Length, frames - produced);
            var read = _source.Read(_scratch.AsSpan(0, want));
            if (read <= 0) break;

            for (var i = 0; i < read; i++)
            {
                var v = _scratch[i];
                buffer[(produced + i) * 2] = v;
                buffer[(produced + i) * 2 + 1] = v;
            }

            produced += read;
        }

        return produced * 2;
    }
}

/// <summary>
/// 频谱与电平分析：音频线程只把样本拷贝进无锁环形缓冲、顺带累加 RMS/峰值，
/// FFT 与频带计算都在 UI 线程按帧率（30 fps）完成，因此音频线程无分配、不加锁。
/// 采集侧的取样点见 <c>InputTapSource</c>，输出侧见 <c>OutputBus</c>。
/// </summary>
public sealed class SpectrumAnalyzer
{
    public const int FftSize = 1024;
    private const int RingSize = FftSize * 4;

    private readonly float[] _ring = new float[RingSize];
    private readonly float[] _snapshot = new float[FftSize];
    private readonly NAudio.Dsp.Complex[] _fft = new NAudio.Dsp.Complex[FftSize];
    private readonly float[] _buffer = new float[FftSize];
    private readonly float[] _window = new float[FftSize];
    private readonly float[] _bands;

    private long _writeCount;
    private long _snapshotAt = -1;
    private int _rmsSamples;
    private float _rmsPeak;

    public SpectrumAnalyzer(int bandCount = 48)
    {
        _bands = new float[bandCount];
        for (var i = 0; i < FftSize; i++)
            _window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (FftSize - 1)));
    }

    public int BandCount => _bands.Length;

    /// <summary>当前 RMS 电平（0–1，线性）。</summary>
    public float Rms { get; private set; }

    /// <summary>当前峰值（0–1，线性）。</summary>
    public float Peak { get; private set; }

    /// <summary>累计送入分析器的样本总数（用于确认该分析点是否真的被读到过）。</summary>
    public long TotalSamples => Interlocked.Read(ref _totalSamples);

    private long _totalSamples;

    /// <summary>音频线程调用：把样本写入环形缓冲区。</summary>
    public void Feed(ReadOnlySpan<float> samples)
    {
        Interlocked.Add(ref _totalSamples, samples.Length);

        var index = (int)(_writeCount % RingSize);
        var first = Math.Min(samples.Length, RingSize - index);
        samples[..first].CopyTo(_ring.AsSpan(index));
        if (first < samples.Length)
            samples[first..].CopyTo(_ring.AsSpan(0));

        _writeCount += samples.Length;

        // 顺带做轻量 RMS / 峰值统计
        var sum = 0f;
        var peak = 0f;
        for (var i = 0; i < samples.Length; i++)
        {
            var v = samples[i];
            sum += v * v;
            var a = MathF.Abs(v);
            if (a > peak) peak = a;
        }

        _rmsAccumulator += sum;
        _rmsSamples += samples.Length;
        if (peak > _rmsPeak) _rmsPeak = peak;
    }

    private double _rmsAccumulator;

    /// <summary>
    /// UI 线程调用（30 fps）：抓取最新一帧并计算 Spectrum。返回 false 表示暂无新数据。
    /// </summary>
    public bool TryUpdate()
    {
        var count = _writeCount;
        if (count < FftSize || _snapshotAt == count) return false;
        _snapshotAt = count;

        var start = (int)((count - FftSize) % RingSize);
        var first = Math.Min(FftSize, RingSize - start);
        Array.Copy(_ring, start, _snapshot, 0, first);
        if (first < FftSize)
            Array.Copy(_ring, 0, _snapshot, first, FftSize - first);

        for (var i = 0; i < FftSize; i++)
        {
            _fft[i].X = _snapshot[i] * _window[i];
            _fft[i].Y = 0f;
        }

        NAudio.Dsp.FastFourierTransform.FFT(true, 10, _fft);

        var binsPerBand = (FftSize / 2) / _bands.Length;
        for (var band = 0; band < _bands.Length; band++)
        {
            var sum = 0f;
            for (var b = 0; b < binsPerBand; b++)
            {
                var index = band * binsPerBand + b;
                sum += MathF.Sqrt(_fft[index].X * _fft[index].X + _fft[index].Y * _fft[index].Y);
            }

            var value = sum / binsPerBand;
            // 转成 dB 并归一到 0–1（-80 dBFS … 0 dBFS）
            var db = AudioMath.LinearToDb(value);
            var normalized = Math.Clamp((db + 80f) / 80f, 0f, 1f);
            _bands[band] = _bands[band] * 0.55f + normalized * 0.45f;
        }

        var samples = Math.Max(1, _rmsSamples);
        Rms = MathF.Sqrt((float)(_rmsAccumulator / samples));
        Peak = _rmsPeak;
        _rmsAccumulator = 0;
        _rmsSamples = 0;
        _rmsPeak = 0f;

        return true;
    }

    /// <summary>把最新频谱拷贝到目标数组（UI 线程）。</summary>
    public void CopyBands(float[] destination, int count)
    {
        var n = Math.Min(Math.Min(count, destination.Length), _bands.Length);
        Array.Copy(_bands, destination, n);
    }

    public void Reset()
    {
        Array.Clear(_ring);
        Array.Clear(_bands);
        _writeCount = 0;
        _snapshotAt = -1;
        Rms = 0f;
        Peak = 0f;
    }
}
