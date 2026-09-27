using NAudio.Wave;
using MateMic.Core;

namespace MateMic.Dsp;

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
///
/// 显示映射（这里调整过一轮，早期版本"只有很大声才看得见起伏"）：
///   · **对数频带**：48 个频带按等比例带宽划分，最低 **40 Hz**、最高到奈奎斯特。
///     为什么下限是 40 Hz 而不是 20 Hz：1024 点 FFT 在 48 kHz 下的频率分辨率只有 46.9 Hz，
///     20–140 Hz 这 9 个频带全部落在 bin 1–3 上，于是**连续好几根柱子读的是同一条谱线、
///     起伏完全同步**（用户反馈"前 9 条几乎是同步起伏"就是这个原因）。
///     现在 FFT 提到 2048 点（分辨率 23.4 Hz）并把下限抬到 40 Hz，
///     再保证每个频带至少覆盖一个独立的 bin，低频那几根柱子才各自独立。
///   · **峰值检波**：每个频带取该频段内的**最大**谱线幅度，而不是算术平均；
///     平均会被同频段内的噪声底谱线把真实峰值拉低。
///   · **显示范围 −90 … −26 dBFS**：这个区间是按实测标定的，不是拍脑袋定的。
///     关键事实：FFT 把宽带信号的能量摊到上千条谱线上，**单条谱线**的幅度比整段信号的
///     RMS 低 20–30 dB——
///       -46 dBFS 的宽带底噪 → 单个频带峰值约 −82 dB（实测，见 DspProbe 第 13 节）
///       -30 dBFS 的宽带信号 → 单个频带峰值约 −66 dB
///     本机实测房间底噪（G30 麦克风）整段峰值约 −43 dBFS，折到单频带只有 −75…−82 dB。
///     早期用 −80…0 dBFS 的显示范围时，正常说话（每 bin 约 −50…−70 dB）只占两三成高度，
///     房间底噪更是一整片贴底——这正是"必须很大声才看得见起伏"的成因。
///   · **非对称平滑**：上升快（0.55）、回落慢（0.12），柱形有明确的起落感。
/// </summary>
public sealed class SpectrumAnalyzer
{
    /// <summary>
    /// FFT 点数。2048 点 @ 48 kHz ⇒ 频率分辨率 23.4 Hz、窗长 42.7 ms。
    /// 分辨率太低会让低频多根柱子读同一条谱线（见类注释），因此不能再往下降。
    /// </summary>
    public const int FftSize = 2048;

    private const int RingSize = FftSize * 4;

    /// <summary>频谱显示的最低频率（Hz）。低于这个频率的多个频带会共用同一条谱线、柱子同步起伏。</summary>
    private const float LowestBandHz = 40f;

    /// <summary>显示下限（dBFS）：再低就当作 0 高度。按"麦克风底噪也能看见一两成高度"标定。</summary>
    private const float DefaultFloorDb = -90f;

    /// <summary>显示上限（dBFS）：到这里就是满高。</summary>
    private const float DefaultCeilingDb = -26f;

    private const float Attack = 0.55f;
    private const float Release = 0.12f;

    private readonly float[] _ring = new float[RingSize];
    private readonly float[] _snapshot = new float[FftSize];
    private readonly NAudio.Dsp.Complex[] _fft = new NAudio.Dsp.Complex[FftSize];
    private readonly float[] _window = new float[FftSize];
    private readonly float[] _bands;
    private readonly int[] _bandLowBin;
    private readonly int[] _bandHighBin;
    private readonly float[] _bandCentreHz;
    private readonly float[] _bandMagnitudes = new float[FftSize / 2];
    private readonly float _floorDb;
    private readonly float _ceilingDb;

    private long _writeCount;
    private long _snapshotAt = -1;
    private int _rmsSamples;
    private float _rmsPeak;

    public SpectrumAnalyzer(int bandCount = 48, float floorDb = DefaultFloorDb, float ceilingDb = DefaultCeilingDb)
    {
        bandCount = Math.Max(1, bandCount);
        _bands = new float[bandCount];
        _bandLowBin = new int[bandCount];
        _bandHighBin = new int[bandCount];
        _bandCentreHz = new float[bandCount];
        _floorDb = floorDb;
        _ceilingDb = MathF.Max(floorDb + 1f, ceilingDb);

        for (var i = 0; i < FftSize; i++)
            _window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (FftSize - 1)));

        BuildLogBands();
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

        // 单次送入的样本数可能超过环形缓冲（采集回调一次给 80 ms = 3840 样本，
        // 而 ring 只有 4096；未来若块长更大就直接越界）。环形缓冲只保留"最新"的
        // RingSize 个样本，因此超出部分只保留尾部即可——既不会抛异常，频谱也拿得到最新数据。
        if (samples.Length > RingSize) samples = samples[^RingSize..];

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
    /// 把谱线按**等比例带宽**分给 bandCount 个频带，并保证每个频带至少覆盖
    /// <see cref="MinBinsPerBand"/> 条**未被前一个频带占用**的谱线。
    ///
    /// 只做对数划分是不够的：48 个频带要覆盖 9 个多八度，最低几个频带的宽度会小于一条谱线
    /// （2048 点 FFT 在 48 kHz 下分辨率 23.4 Hz，而最低频带只有 ~5 Hz 宽），
    /// 于是它们读到同一条谱线，画出来就是"连续好几根柱子完全同步起伏"
    /// ——用户反馈的"前 9 条几乎是同步起伏"就是这个原因。
    ///
    /// 这里的做法：频带仍然按对数定位，但如果算出来的宽度不足
    /// <see cref="MinBinsPerBand"/> 条谱线，上边界就顺延到够宽为止，
    /// 同时下边界至少接在上一个频带的末尾，保证没有两条频带的区间完全相同。
    /// 效果是低端频带比理想对数位置略宽（位置基本不变），但每根柱子都读到自己那份数据。
    /// </summary>
    private void BuildLogBands()
    {
        const int minBinsPerBand = 2;

        var highHz = 48000f / 2f;              // 内部链路固定 48 kHz，不引用 AudioEngine 以免下层反向依赖
        var nyquistBin = FftSize / 2;
        var hzPerBin = highHz / nyquistBin;

        var lowHz = LowestBandHz;
        var ratio = highHz / lowHz;
        var previousHigh = 1;                  // bin 0 是直流，永远是 0，从 bin 1 起步

        for (var band = 0; band < _bands.Length; band++)
        {
            var startFraction = band / (float)_bands.Length;
            var endFraction = (band + 1) / (float)_bands.Length;
            var startHz = lowHz * MathF.Pow(ratio, startFraction);
            var endHz = lowHz * MathF.Pow(ratio, endFraction);
            var centreHz = MathF.Sqrt(startHz * endHz);

            var lowBin = Math.Clamp((int)MathF.Round(startHz / hzPerBin), 1, nyquistBin - minBinsPerBand);
            var highBin = Math.Clamp((int)MathF.Ceiling(endHz / hzPerBin), lowBin + minBinsPerBand, nyquistBin);

            // 至少两条谱线宽、且不与上一个频带重叠
            if (lowBin < previousHigh) lowBin = previousHigh;
            if (lowBin > nyquistBin - minBinsPerBand) lowBin = nyquistBin - minBinsPerBand;
            if (highBin < lowBin + minBinsPerBand) highBin = lowBin + minBinsPerBand;
            if (highBin > nyquistBin) highBin = nyquistBin;

            previousHigh = highBin;

            _bandLowBin[band] = lowBin;
            _bandHighBin[band] = highBin;
            _bandCentreHz[band] = centreHz;
        }
    }

    /// <summary>第 n 个频带的中心频率（Hz），用于调试/标注。</summary>
    public float BandCentreHz(int index)
        => index >= 0 && index < _bandCentreHz.Length ? _bandCentreHz[index] : 0f;

    /// <summary>第 n 个频带实际覆盖的 bin 区间（含 low、不含 high），用于自检。</summary>
    public (int Low, int High) BandBinRange(int index)
        => index >= 0 && index < _bandLowBin.Length
            ? (_bandLowBin[index], _bandHighBin[index])
            : (0, 0);

    /// <summary>
    /// 最接近给定频率的那个频带（用于界面上的频率刻度线）。
    /// 频带中心频率被"每频带至少 2 条谱线"的约束轻微改写，因此不能按对数刻度反算下标。
    /// </summary>
    public int NearestBandIndex(float hz)
    {
        var best = 0;
        var bestDistance = float.MaxValue;
        for (var i = 0; i < _bandCentreHz.Length; i++)
        {
            var distance = MathF.Abs(_bandCentreHz[i] - hz);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = i;
        }

        return best;
    }

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

        // FFT 点数必须跟着 FftSize 走：NAudio 的第二个参数是 log2(N)，写死 10 只在 1024 点下正确
        NAudio.Dsp.FastFourierTransform.FFT(true, (int)MathF.Log2(FftSize), _fft);

        // 先算出全部谱线的幅度，再按频带取峰值
        var nyquistBin = FftSize / 2;
        var span = _bandMagnitudes.AsSpan(0, nyquistBin);
        for (var b = 0; b < nyquistBin; b++)
            span[b] = MathF.Sqrt(_fft[b].X * _fft[b].X + _fft[b].Y * _fft[b].Y);

        for (var band = 0; band < _bands.Length; band++)
        {
            var centreBin = _bandCentreHz[band] / (48000f / FftSize);
            var peak = PeakInRange(span, _bandLowBin[band], Math.Min(_bandHighBin[band], nyquistBin), centreBin);
            var db = AudioMath.LinearToDb(peak);
            var normalized = MathClamp((db - _floorDb) / (_ceilingDb - _floorDb));

            // 非对称平滑：上升快、回落慢
            var previous = _bands[band];
            _bands[band] = previous + (normalized - previous) * (normalized > previous ? Attack : Release);
        }

        var samples = Math.Max(1, _rmsSamples);
        Rms = MathF.Sqrt((float)(_rmsAccumulator / samples));
        Peak = _rmsPeak;
        _rmsAccumulator = 0;
        _rmsSamples = 0;
        _rmsPeak = 0f;

        return true;
    }

    private static float MathClamp(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

    /// <summary>
    /// 某个频带的幅度：在它覆盖的谱线里取最大值，**并在相邻两条谱线之间做线性插值**。
    ///
    /// 为什么还要插值：保证"每频带至少 2 条谱线"之后，低频那几根柱子已经不共用了，
    /// 但 2 条谱线仍然是按网格取的（比如 bin 1–2 / 3–4 …），
    /// 一个正好落在网格缝隙里的窄带信号会被相邻几个频带以不同幅度读到、位置也会偏。
    /// 这里额外在"频带中心对应的分数 bin 位置"上插一个值一起参与取最大，
    /// 峰值位置对窄带信号就准确了，宽带信号因为相邻谱线本来就差不多，几乎无影响。
    /// </summary>
    private static float PeakInRange(ReadOnlySpan<float> magnitudes, int lowBin, int highBin, float centreBin)
    {
        lowBin = Math.Max(1, Math.Min(lowBin, magnitudes.Length - 1));
        highBin = Math.Max(lowBin + 1, Math.Min(highBin, magnitudes.Length));

        var peak = 0f;
        for (var b = lowBin; b < highBin; b++)
            if (magnitudes[b] > peak) peak = magnitudes[b];

        var left = (int)MathF.Floor(centreBin);
        var fraction = centreBin - left;
        var leftIndex = Math.Clamp(left, 0, magnitudes.Length - 1);
        var rightIndex = Math.Clamp(left + 1, 0, magnitudes.Length - 1);
        var leftValue = magnitudes[leftIndex];
        var rightValue = magnitudes[rightIndex];
        var interpolated = leftValue + (rightValue - leftValue) * fraction;

        return MathF.Max(peak, interpolated);
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
