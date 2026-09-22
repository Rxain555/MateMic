using NAudio.Wave;
using MicMate.Core;

namespace MicMate.Dsp;

/// <summary>
/// 2. AI 降噪节点。480 samples（10 ms）一帧调用降噪后端，输出按干湿比混合：
/// output = dry × (1 − wet) + denoised × wet。
/// 单帧推理超过 50 ms 时记录日志并跳过该帧（输出原始音频），符合 5.2 的容错要求。
/// </summary>
public sealed class DenoiseEffect : IAudioEffect
{
    private const int FrameSize = SpectralDenoiseModel.FrameSize;
    private const long SkipThresholdMs = 50;

    private readonly DenoiseSettings _settings;
    private readonly float[] _frame = new float[FrameSize];
    /// <summary>帧工作区：模型就地处理这一帧，结果留在同数组。</summary>
    private readonly float[] _out = new float[FrameSize * 2];

    /// <summary>输入队列：收集调用方样本，凑满一帧就交给工作区处理。</summary>
    private float[] _inQueue = new float[FrameSize * 4];

    /// <summary>输出队列：存放已处理完、等待交出的输出帧。</summary>
    private float[] _outQueue = new float[FrameSize * 4];

    private int _inputFill;
    private int _outputFill;
    private IDenoiseModel _model;
    private int _skippedFrames;
    private long _processedFrames;

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

    public int SkippedFrames => _skippedFrames;

    /// <summary>切换降噪后端（模型下拉框）。</summary>
    public void SetModel(IDenoiseModel model)
    {
        var old = _model;
        _model = model;
        _inputFill = 0;
        _outputFill = 0;
        if (!ReferenceEquals(old, model)) old.Dispose();
        Log.Info("降噪模型已切换为 " + model.Name);
    }

    /// <summary>降噪强度：谱减后端的过减系数 / 模型后置增益。</summary>
    public float Strength { get; private set; } = 1f;

    public float Wet { get; private set; } = 1f;

    public void UpdateParameters()
    {
        Wet = Math.Clamp(_settings.Wet / 100f, 0f, 1f);
        if (_model is SpectralDenoiseModel spectral)
        {
            spectral.Strength = _settings.Strength;
            Strength = 1f;
        }
        else
        {
            // 模型后置增益：0 → 直通，100 → 全量
            Strength = 0.4f + Math.Clamp(_settings.Strength, 0f, 100f) / 100f * 0.6f;
        }
    }

    public int Read(Span<float> buffer)
    {
        if (!Enabled || Wet <= 0.001f)
        {
            _bypassedSamples += buffer.Length;
            return buffer.Length;
        }

        // 算法：960 样本滑动窗口 + 帧级推进。
        //
        //   _working[0..FrameSize)          ：上一帧处理后、正在等待交出的样本（已降噪）
        //   _working[FrameSize..2*FrameSize)：正处于"一帧延迟"中的原始输入
        //
        //   每次读入样本 → 填进窗口后半段 → 一旦后半段填满一帧：
        //     · 把 new - old 的增量叠加到窗口前半段（完成干湿混合）
        //     · 前半段就是本次要给调用方的输出
        //     · 整体左移一帧：后半段（刚处理完的）进入前半段，空出后半段收新数据
        //
        // 为什么必须用两帧长度的窗口：任何"延迟 480"的实现，其本块输出都需要
        // 「本块输入 + 上一帧残留在窗口里的原始样本」同时可见；只保存一帧的写法
        // 在输出位置需要未来样本时无法补齐，导致非整除块长出现咔嗒声（实测 0.16–0.30 跳变）。
        var written = 0;   // 本次已写出的输出样本数（保证输出从 buffer 头连续给出）
        var offset = 0;
        while (offset < buffer.Length)
        {
            var need = FrameSize - _inputFill;
            var take = Math.Min(need, buffer.Length - offset);
            if (take > 0)
            {
                buffer.Slice(offset, take).CopyTo(_working.AsSpan(FrameSize + _inputFill));
                _inputFill += take;
                offset += take;
            }

            if (_inputFill < FrameSize) break;

            // 一帧输入就绪：用 _frame 保留原始输入，供 ProcessFrame 做干湿混合
            _working.AsSpan(FrameSize, FrameSize).CopyTo(_frame);
            ProcessFrame();

            // 把降噪结果相对原始输入的增量，叠加到窗口前半段（那里正是同位置的原始样本）
            for (var i = 0; i < FrameSize; i++)
                _working[i] += _out[i] - _frame[i];

            // 交出窗口前半段（已降噪、且对齐）
            var give = Math.Min(FrameSize, buffer.Length - written);
            _working.AsSpan(0, give).CopyTo(buffer[written..]);
            written += give;

            // 整体左移一帧，后半段变为新的前半段
            _working.AsSpan(FrameSize).CopyTo(_working);
            _inputFill -= FrameSize;
        }

        // 预热阶段输出不足时，不足部分保持原样（直通），避免静音段
        for (var i = written; i < buffer.Length; i++) buffer[i] = buffer[i];

        return buffer.Length;
    }

    /// <summary>诊断用：因未启用而被跳过的样本数（用于确认降噪到底有没有在处理）。</summary>
    public long BypassedSamples => _bypassedSamples;

    /// <summary>诊断用：已处理的音频帧数。</summary>
    public long ProcessedFrames => _processedFrames;

    private long _bypassedSamples;

    /// <summary>
    /// 对 <see cref="_frame"/> 队首的一整帧就地降噪，结果写入 <see cref="_out"/> 的同位置。
    /// 干湿混合在"原始输入"与"降噪结果"之间进行，因此先把原样拷贝进 _out 作为干信号。
    /// </summary>
    private void ProcessFrame()
    {
        var start = Environment.TickCount64;

        _processedFrames++;

        // 干信号：本帧的原始输入（_frame 会被模型就地改写）
        _frame.AsSpan(0, FrameSize).CopyTo(_out);

        float[] processed;
        try
        {
            DenoiseFrame(_frame);

            // 模型就地处理后，_frame[0..FrameSize) 即降噪结果
            processed = _frame;
        }
        catch (Exception ex)
        {
            _skippedFrames++;
            Log.Error("降噪推理失败，本帧直通", ex);
            return;   // _out 里已是干信号，等于直通
        }

        var elapsed = Environment.TickCount64 - start;
        if (elapsed > SkipThresholdMs)
        {
            _skippedFrames++;
            if (_skippedFrames % 50 == 1)
                Log.Warn($"降噪单帧推理耗时 {elapsed} ms（> {SkipThresholdMs} ms），已跳过该帧");
            return;   // 同样保持干信号直通
        }

        var dry = 1f - Wet;
        for (var i = 0; i < FrameSize; i++)
        {
            var denoised = processed[i] * Strength;
            _out[i] = _out[i] * dry + denoised * Wet;
        }
    }

    private void DenoiseFrame(float[] frame) => _model.Process(frame);
}
