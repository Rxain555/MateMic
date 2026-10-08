using System.Collections.Concurrent;
using NAudio.Wave;
using MateMic.Core;

namespace MateMic.Dsp;

/// <summary>
/// 2. AI 降噪节点。480 samples（10 ms）一帧调用降噪后端，输出按干湿比混合：
/// output = dry × (1 − wet) + denoised × wet。
///
/// **块长无关**：每次 Read 的长度由 WASAPI 周期决定（3 ms / 10 ms / …），
/// 不保证是 480 的整数倍。因此这里用「输入累加 + 输出环形缓冲」实现：
///   · 每收到 FrameSize 个输入样本就交给模型处理一帧；
///   · 处理结果进入输出环形缓冲，按样本逐个交出。
/// 代价是**固定 480 样本（10 ms）延迟**，好处是任意块长下都不会在帧边界产生跳变
/// （旧实现把处理结果直接写回调用方的缓冲区，块长不是 480 的整数倍时会越界抛异常；
/// 若只"就地"处理已交出的样本，又会在帧边界留下台阶）。
/// 输出 = 输入延迟 480 样本，第一帧（10 ms）输出静音。
///
/// 单帧推理超过 50 ms 时记录日志并跳过该帧（输出原始音频），符合 5.2 的容错要求。
/// </summary>
public sealed class DenoiseEffect : IAudioEffect
{
    private const int FrameSize = SpectralDenoiseModel.FrameSize;
    private const long SkipThresholdMs = 50;

    /// <summary>本效果固定引入的延迟（样本）。需要做延迟补偿时用这个值。</summary>
    public const int LatencySamples = FrameSize;

    private readonly DenoiseSettings _settings;

    /// <summary>正在累加的输入帧；凑满 FrameSize 后模型就地处理这一帧。</summary>
    private readonly float[] _frame = new float[FrameSize];

    /// <summary>本帧的干信号：模型会就地改写 <see cref="_frame"/>，混合时必须留一份原始样本。</summary>
    private readonly float[] _dry = new float[FrameSize];

    /// <summary>已处理完、等待交出的样本。不变式：最多积压一帧（见 <see cref="Read"/>）。</summary>
    private readonly float[] _done = new float[FrameSize * 2];

    /// <summary>被换下来的旧模型。只能由音频线程释放，见 <see cref="SetModel"/>。</summary>
    private readonly ConcurrentQueue<IDenoiseModel> _retired = new();

    private volatile IDenoiseModel _model;

    private int _frameFill;
    private int _doneHead;
    private int _doneCount;
    private long _skippedFrames;

    /// <summary>最近一帧的推理耗时（毫秒）。audio 线程写、UI 线程读，用 Volatile。</summary>
    private double _lastInferMs;
    private long _processedFrames;
    private long _bypassedSamples;

    public DenoiseEffect(WaveFormat format, DenoiseSettings settings, IDenoiseModel model)
    {
        WaveFormat = format;
        _settings = settings;
        _model = model;
        UpdateParameters();
    }

    public string Name => "AI 降噪";

    public bool Enabled { get; set; } = true;

    public WaveFormat WaveFormat { get; }

    public IDenoiseModel Model => _model;

    public string ModelName => _model.Name;

    public string TensorInfo => _model.TensorInfo;

    public int SkippedFrames => (int)Math.Min(int.MaxValue, Interlocked.Read(ref _skippedFrames));

    /// <summary>诊断用：因未启用而被跳过的样本数（用于确认降噪到底有没有在处理）。</summary>
    public long BypassedSamples => Interlocked.Read(ref _bypassedSamples);

    /// <summary>诊断用：已处理的音频帧数。</summary>
    public long ProcessedFrames => Interlocked.Read(ref _processedFrames);

    /// <summary>一帧的时长（毫秒）。降噪按 480 样本一帧处理，本节点的时间占用率就以它为分母。</summary>
    public double FrameMs => FrameSize * 1000.0 / WaveFormat.SampleRate;

    /// <summary>本节点固定引入的算法延迟（毫秒）= 一帧。</summary>
    public double LatencyMs => FrameMs;

    /// <summary>
    /// 最近一帧降噪推理的耗时（毫秒）。供界面显示性能消耗。
    /// 用 <see cref="Stopwatch"/> 而不是 <c>Environment.TickCount64</c>：后者精度只有 1ms，
    /// 而降噪单帧往往不到 1ms，用它会一直显示 0（2026-10-08）。
    /// </summary>
    public double LastInferMs => Volatile.Read(ref _lastInferMs);

    /// <summary>
    /// 切换降噪后端（模型下拉框）。
    ///
    /// **绝不在这里 Dispose 旧模型**：本方法由 UI 线程调用，而音频线程可能正卡在
    /// <c>old.Process</c> 里；释放 ONNX 会话会导致访问已释放的原生内存（可能直接崩进程）。
    /// 因此把旧模型挂到 <see cref="_retired"/>，由音频线程在下一次 <see cref="Read"/> 开头释放——
    /// 那时它自己一定已经离开了旧模型的调用栈。
    /// </summary>
    public void SetModel(IDenoiseModel model)
    {
        var old = _model;
        if (ReferenceEquals(old, model)) return;

        _retired.Enqueue(old);
        model.Reset();
        _model = model;   // volatile 发布
        Log.Info("降噪模型已切换为 " + model.Name);
    }

    /// <summary>降噪强度（0 = 完全直通，1 = 全量）。谱减后端由模型自身的 Strength 负责。</summary>
    public float Strength { get; private set; } = 1f;

    public float Wet { get; private set; } = 1f;

    public void UpdateParameters()
    {
        Wet = Math.Clamp(_settings.Wet / 100f, 0f, 1f);
        if (_model is SpectralDenoiseModel spectral)
        {
            // 谱减后端的强度体现在过减系数上，本节点不再额外缩放
            spectral.Strength = _settings.Strength;
            Strength = 1f;
        }
        else
        {
            // 其它后端（波形域 / 频谱域流式 / 增益曲线域）：强度就是模型输出的干湿占比，
            // 0 → 直通、100 → 全量。早期版本映射成 0.4–1.0 的"后置增益"，
            // 结果是强度 0 也还有 −8 dB，与滑块语义不符。
            Strength = Math.Clamp(_settings.Strength, 0f, 100f) / 100f;
        }
    }

    public int Read(Span<float> buffer)
    {
        // 释放被换下来的旧模型。本方法是 _model.Process 的唯一调用者，
        // 所以执行到这里时音频线程一定不在旧模型内部。
        ReleaseRetired();

        var wet = Wet * Strength;
        if (!Enabled || wet <= 0.001f)
        {
            Interlocked.Add(ref _bypassedSamples, buffer.Length);
            ResetPipeline();   // 不留中间状态，避免重新启用时吐出陈旧样本
            return buffer.Length;
        }

        for (var i = 0; i < buffer.Length; i++)
        {
            var sample = buffer[i];

            // 先交出、再收样本：这样"凑满一帧的那一个样本"不会被立刻取走，
            // 输出恰好等于输入延迟 FrameSize 个样本（= 固定 10 ms 延迟，恒定不变）。
            // 预热期（前 FrameSize 个样本）输出静音——这是帧式处理的固有代价，
            // 也是本节点唯一会丢音频的地方（仅发生在启动 / 从直通切回处理的瞬间）。
            buffer[i] = _doneCount > 0 ? Take() : 0f;

            _frame[_frameFill++] = sample;
            if (_frameFill >= FrameSize)
            {
                _frameFill = 0;
                ProcessFrame(wet);
            }

            // 不变式：_doneCount ≤ FrameSize（每收满一帧才压入一帧，且每个样本只取一个），
            // 因此 2 帧容量的环形缓冲足够。
        }

        return buffer.Length;
    }

    /// <summary>
    /// 对 <see cref="_frame"/> 中的一整帧就地降噪，并把结果（按干湿比混合后）压入输出环形缓冲。
    /// 失败或超时则压入干信号，等于本帧直通。
    /// </summary>
    private void ProcessFrame(float wet)
    {
        // 用 Stopwatch 而不是 TickCount64：降噪单帧常在 1ms 以内，1ms 精度会显示成 0
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _processedFrames);

        var model = _model;   // 本帧固定用一个模型，避免中途换模型导致半帧前后不一致
        _frame.AsSpan(0, FrameSize).CopyTo(_dry);

        try
        {
            model.Process(_frame);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _skippedFrames);
            if (Interlocked.Read(ref _skippedFrames) % 50 == 1)
                Log.Error("降噪推理失败，本帧直通", ex);
            PushFrame(_dry);
            return;
        }

        var elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - start)
                        * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Volatile.Write(ref _lastInferMs, elapsedMs);

        if (elapsedMs > SkipThresholdMs)
        {
            var skipped = Interlocked.Increment(ref _skippedFrames);
            if (skipped % 50 == 1)
                Log.Warn($"降噪单帧推理耗时 {elapsedMs:0.#} ms（> {SkipThresholdMs} ms），已跳过该帧");
            PushFrame(_dry);
            return;
        }

        var dryGain = 1f - wet;
        for (var i = 0; i < FrameSize; i++)
            _frame[i] = _dry[i] * dryGain + _frame[i] * wet;

        PushFrame(_frame);
    }

    private void PushFrame(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples) Push(sample);
    }

    private void Push(float value)
    {
        if (_doneCount == _done.Length)
        {
            // 按上面的不变式不可达；真出现也宁可丢最旧的样本，绝不越界
            _doneHead = (_doneHead + 1) % _done.Length;
            _doneCount--;
        }

        _done[(_doneHead + _doneCount) % _done.Length] = value;
        _doneCount++;
    }

    private float Take()
    {
        var value = _done[_doneHead];
        _doneHead = (_doneHead + 1) % _done.Length;
        _doneCount--;
        return value;
    }

    private void ResetPipeline()
    {
        _frameFill = 0;
        _doneHead = 0;
        _doneCount = 0;
    }

    private void ReleaseRetired()
    {
        while (_retired.TryDequeue(out var model))
        {
            try
            {
                model.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("释放旧降噪模型失败：" + ex.Message);
            }
        }
    }
}
