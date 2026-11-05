using MateMic.Denoise;

namespace MateMic.Ai.AiVoiceIpc;

/// <summary>
/// 把一个 48 kHz 音频块送进 RVC 并取回 48 kHz 结果 —— 即"纯 DSP 部分"，
/// 不含任何环形缓冲、线程与水位管理。
///
/// 存在的理由：**宿主进程**需要这段逻辑，而主进程里同样的逻辑埋在
/// <c>AiVoiceEffect</c> 的 worker 里（与它自己的环耦合在一起）。
/// 两边各写一份迟早会漂移，所以这里单独抽出来给宿主用。
///
/// ⚠ 目前 <c>AiVoiceEffect</c> 还没改用这个类（它工作正常，不想为了统一而冒回归风险），
/// 也就是说**这里与它暂时是两份等价实现**。等独立进程跑通、主进程侧也切过去之后，
/// 再把 <c>AiVoiceEffect</c> 里的那份删掉，只留这一处。
/// </summary>
public sealed class RvcBlockProcessor : IDisposable
{
    private const int ChainRate = 48000;   // 主链路采样率
    private const int ModelRate = 40000;   // RVC 模型的输出采样率

    private readonly StreamingRvc _rvc;
    private readonly RateConverter _toModel;
    private readonly RateConverter _fromModel;

    /// <summary>一块的 48 kHz 样本数。</summary>
    public int BlockSamples48 { get; }

    /// <summary>
    /// 更新**热参数**（变调、索引占比）。只影响每块计算，不动缓冲几何 ——
    /// 等同进程内路径直接改 <c>StreamingRvc.Semitones / IndexRate</c>，无需重建。
    /// </summary>
    public void SetHotParams(int semitones, float indexRate)
    {
        _rvc.Semitones = semitones;
        _rvc.IndexRate = _rvc.Index != null ? Math.Clamp(indexRate, 0f, 1f) : 0f;
    }

    /// <summary>最近一块的推理耗时（毫秒）。</summary>
    public double LastInferMs { get; private set; }

    /// <summary>已处理的块数。</summary>
    public long Blocks { get; private set; }

    public RvcBlockProcessor(StreamingRvc rvc, int blockMs)
    {
        _rvc = rvc;
        BlockSamples48 = Math.Max(1, blockMs * ChainRate / 1000);
        _toModel = new RateConverter(ChainRate, ModelRate, BlockSamples48 + 64);
        _fromModel = new RateConverter(ModelRate, ChainRate, BlockSamples48 * ModelRate / ChainRate + 64);
    }

    /// <summary>
    /// 处理一块。输入必须正好 <see cref="BlockSamples48"/> 个样本。
    /// 返回写入 <paramref name="output48"/> 的样本数（调用方应保证它够大）。
    /// </summary>
    public int Process(ReadOnlySpan<float> input48, Span<float> output48)
    {
        if (input48.Length != BlockSamples48) throw new ArgumentException($"需要 {BlockSamples48} 个样本");

        // 48k → 40k
        var modelBlock = BlockSamples48 * ModelRate / ChainRate;
        var block40 = new float[modelBlock];
        var produced = _toModel.Process(input48.ToArray(), block40);
        if (produced < modelBlock) Array.Resize(ref block40, produced);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var out40 = _rvc.Process(block40);
        sw.Stop();

        LastInferMs = sw.Elapsed.TotalMilliseconds;
        Blocks++;

        // 40k → 48k
        var out48 = new float[out40.Length * ChainRate / ModelRate + 64];
        var n = _fromModel.Process(out40, out48);
        n = Math.Min(n, output48.Length);
        out48.AsSpan(0, n).CopyTo(output48);
        return n;
    }

    public void Dispose()
    {
        _rvc.Dispose();
    }
}
