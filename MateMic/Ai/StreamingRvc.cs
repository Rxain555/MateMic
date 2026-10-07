namespace MateMic.Ai;

/// <summary>
/// 流式推理编排：滑动窗口 + SOLA 波形对齐 + 余弦交叉淡化。
///
/// 算法照官方 `RVCRealtimeVST\worker\rvc_worker.py`：
///     total = extra(context) + crossfade + sola_search + block
/// 每来一个 block：
///   1) 新 block 追加到滑动窗口尾部（整体左移）
///   2) 推理**整个窗口**（耗时 ∝ block + context，这是算力预算的真实来源）
///   3) 丢弃前面的 extra 段（只为给模型上下文）
///   4) 取 block + sola + search 长度
///   5) SOLA：在 sola_search 内搜索最佳偏移，使重叠区与上一块最相似
///   6) 半余弦交叉淡化后输出 block
///
/// 为什么必须有 SOLA：没有它就只能靠加长 crossfade 来掩盖接缝，
/// 而 crossfade 每长一点就多一份延迟。SOLA 把重叠**封顶在 40ms**（min(crossfade, 4*zc)）。
///
/// 官方口径的**算法延迟 = 2 × block**。
/// </summary>
public sealed class StreamingRvc : IDisposable
{
    private const int OutputRate = 40000;
    private const int Zc = 400;              // 40 kHz 下 10 ms
    private const double F0Min = 50.0, F0Max = 1100.0;

    private readonly ContentEncoder _encoder;
    private readonly RmvpeF0 _rmvpe;
    private readonly RvcSynthesizer _synth;

    private readonly int _block, _extra, _crossfade, _solaBuffer, _solaSearch, _total;
    private readonly float[] _in40;
    private readonly float[] _sola;
    private readonly float[] _fadeIn, _fadeOut;

    public int BlockSamples => _block;
    public int LatencyMs => 2 * _block * 1000 / OutputRate;
    public double LastInferMs { get; private set; }
    public double TotalInferMs { get; private set; }
    public int Blocks { get; private set; }

    public StreamingRvc(string cvModel, string rmvpeModel, string voiceModel,
                        int blockMs = 160, int contextMs = 320, int crossfadeMs = 40,
                        int semitones = 12, bool useGpu = true, bool disableSola = false)
    {
        Semitones = semitones;
        _encoder = new ContentEncoder(cvModel, useGpu);
        _rmvpe = new RmvpeF0(rmvpeModel, useGpu);
        _synth = new RvcSynthesizer(voiceModel, useGpu);

        static int ToSamples(int ms) => (int)Math.Round(ms / 1000.0 * OutputRate / Zc) * Zc;
        _block = ToSamples(blockMs);
        _extra = ToSamples(contextMs);
        _crossfade = ToSamples(crossfadeMs);
        _solaBuffer = disableSola ? 0 : Math.Min(_crossfade, 4 * Zc);   // 封顶 40ms
        _solaSearch = Zc;
        _total = _extra + _crossfade + _solaSearch + _block;

        _in40 = new float[_total];
        _sola = new float[_solaBuffer];
        _fadeIn = new float[_solaBuffer];
        _fadeOut = new float[_solaBuffer];
        for (var i = 0; i < _solaBuffer; i++)
        {
            // 半余弦窗，与官方 fade_in_window / fade_out_window 一致
            var x = (i + 1.0) / (_solaBuffer + 1.0);
            _fadeIn[i] = (float)Math.Sin(0.5 * Math.PI * x);
            _fadeOut[_solaBuffer - 1 - i] = _fadeIn[i];
        }
    }

    /// <summary>
    /// 变调量（半音）。**可在运行期修改**——它只影响每块的 f0 变换，不改动缓冲几何，
    /// 所以拖动变调滑条能立刻生效，无需重建引擎（重建一次要 1.5 秒，体验很差）。
    /// </summary>
    public int Semitones { get; set; }

    /// <summary>送入一块 40 kHz 音频，返回同长度的转换结果。</summary>
    public float[] Process(ReadOnlySpan<float> block40)
    {
        if (block40.Length != _block) throw new ArgumentException($"需要 {_block} 个样本");

        // 1) 滑动窗口
        Array.Copy(_in40, _block, _in40, 0, _total - _block);
        block40.CopyTo(_in40.AsSpan(_total - _block));

        // 2) 重采样到 16k（线性；产品里换成与主程序一致的重采样器）
        var n16 = _total * 16000 / OutputRate;
        var a16 = new float[n16];
        for (var i = 0; i < n16; i++)
        {
            var pos = (double)i * (_total - 1) / (n16 - 1);
            var i0 = (int)pos;
            var frac = (float)(pos - i0);
            var i1 = Math.Min(i0 + 1, _total - 1);
            a16[i] = _in40[i0] * (1 - frac) + _in40[i1] * frac;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var (features, f50) = _encoder.Encode(a16);
        var (f0raw, _, _) = _rmvpe.Extract(a16);

        // 特征 2 倍插值（nearest，输出 2T）
        var f100 = 2 * f50;
        var feats100 = new float[f100 * ContentEncoder.FeatureDim];
        for (var i = 0; i < f100; i++)
        {
            var src = Math.Min(i / 2, f50 - 1);
            Array.Copy(features, src * ContentEncoder.FeatureDim,
                       feats100, i * ContentEncoder.FeatureDim, ContentEncoder.FeatureDim);
        }

        var frames = Math.Min(f100, f0raw.Length);

        // ⚠ 合成器内部有 3 级下采样，**要求帧数是 8 的倍数**，
        // 否则报 "Reshape ... Input shape:{1,2,52,104}"。
        // 整段推理时帧数（1144）恰好是 8 的倍数，所以这个问题在离线验证里不会暴露，
        // 只有流式的小窗口（52 帧）才会踩到。
        // 补帧时：特征补 0，pitch 补 1（mel 量化的最低档，合成器按无声处理）。
        var padded = (frames + 7) / 8 * 8;

        var shift = Math.Pow(2.0, Semitones / 12.0);
        var pitchf = new float[padded];
        var pitch = new long[padded];
        var paddedFeats = new float[padded * ContentEncoder.FeatureDim];
        Array.Copy(feats100, paddedFeats, frames * ContentEncoder.FeatureDim);

        var melMin = 1127.0 * Math.Log(1 + F0Min / 700.0);
        var melMax = 1127.0 * Math.Log(1 + F0Max / 700.0);

        for (var i = 0; i < frames; i++)
        {
            var f = f0raw[i] * shift;
            pitchf[i] = (float)f;
            var mel = 1127.0 * Math.Log(1 + Math.Max(f, 0) / 700.0);
            if (mel > 0) mel = (mel - melMin) * 254.0 / (melMax - melMin) + 1.0;
            if (mel <= 1) mel = 1;
            if (mel > 255) mel = 255;
            pitch[i] = (long)Math.Round(mel);
        }
        for (var i = frames; i < padded; i++) pitch[i] = 1;      // 补的帧当作无声

        var infer = _synth.Synthesize(paddedFeats, padded, pitch, pitchf);

        sw.Stop();
        LastInferMs = sw.Elapsed.TotalMilliseconds;
        TotalInferMs += LastInferMs;
        Blocks++;

        // 3) 丢弃 extra 段
        var need = _block + _solaBuffer + _solaSearch;
        var seg = new float[need];
        var skip = _extra;
        var avail = Math.Min(need, Math.Max(0, infer.Length - skip));
        if (avail > 0) Array.Copy(infer, skip, seg, 0, avail);

        // 4) SOLA：在 solaSearch 范围内搜最佳偏移
        if (_solaBuffer > 0)
        {
            var search = _solaSearch + 1;
            var bestOffset = 0;
            var bestScore = double.NegativeInfinity;
            for (var off = 0; off < search; off++)
            {
                double dot = 0, energy = 0;
                for (var i = 0; i < _solaBuffer; i++)
                {
                    var h = seg[off + i];
                    dot += h * _sola[i];
                    energy += h * h;
                }
                var score = dot / Math.Sqrt(energy + 1e-8);
                if (score > bestScore) { bestScore = score; bestOffset = off; }
            }
            if (bestOffset > 0)
            {
                Array.Copy(seg, bestOffset, seg, 0, need - bestOffset);
                Array.Clear(seg, need - bestOffset, bestOffset);
            }
        }

        // 5) 交叉淡化
        if (_solaBuffer > 0)
        {
            for (var i = 0; i < _solaBuffer; i++)
                seg[i] = seg[i] * _fadeIn[i] + _sola[i] * _fadeOut[i];
            Array.Copy(seg, _block, _sola, 0, _solaBuffer);
        }

        var result = new float[_block];
        Array.Copy(seg, result, _block);
        return result;
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _rmvpe.Dispose();
        _synth.Dispose();
    }
}
