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

    /// <summary>
    /// SOLA 可信度下限：重叠区的平均能量低于它、或最佳归一化得分低于它，
    /// 就认为"这段没有可对齐的内容"（静音或极低电平），偏移保持 0。
    /// 见 Process 里 SOLA 一段的说明。
    /// </summary>
    private const double SolaMinEnergy = 1e-5;
    private const double SolaMinScore = 0.15;

    private readonly ContentEncoder _encoder;
    private readonly RmvpeF0 _rmvpe;
    private readonly RvcSynthesizer _synth;
    private RvcIndex? _index;

    /// <summary>索引特征占比（0~1）。没有索引时恒为 0。</summary>
    public float IndexRate { get; set; }

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
                        int semitones = 12, bool useGpu = true, bool disableSola = false,
                        string? indexSimpleDir = null, float indexRate = 0f,
                        Action<int>? onProgress = null)
    {
        Semitones = semitones;
        IndexRate = indexRate;
        // 三个模型逐个加载，按权重报进度（contentvec 约占一半体积、合成器次之、rmvpe 最小）
        onProgress?.Invoke(2);
        _encoder = new ContentEncoder(cvModel, useGpu);
        onProgress?.Invoke(45);
        _rmvpe = new RmvpeF0(rmvpeModel, useGpu);
        onProgress?.Invoke(65);
        _synth = new RvcSynthesizer(voiceModel, useGpu);
        onProgress?.Invoke(90);

        // 音色索引（可选）：放在转换后的简单格式目录里
        if (!string.IsNullOrWhiteSpace(indexSimpleDir) && Directory.Exists(indexSimpleDir))
        {
            _index = RvcIndex.Load(indexSimpleDir);
            if (_index == null) IndexRate = 0f;
        }
        else
        {
            IndexRate = 0f;
        }

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

        // 索引检索：**在 2 倍插值之前**做，与官方一致
        //（官方顺序是 hubert → index.search → interpolate）。
        // 放在插值前还省一半计算量。
        if (_index != null && IndexRate > 0)
            _index.Retrieve(features, f50, IndexRate);

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

        // ------------------------------------------------------------------
        // ★ 响度包络混合（官方的 rms_mix，默认 0.0）。
        //
        // 这是**解决"静音处仍有底噪"的关键机制**，此前完全被我漏掉了：
        //   rms1 = 输入音频的音量包络
        //   rms2 = 合成输出的音量包络
        //   infer *= (rms1 / rms2) ** (1 - rms_mix)
        //
        // 输入静音时 rms1 ≈ 0 ⇒ 输出被自动压到接近 0；输入有声时输出被归一到输入的响度。
        // **它是连续的包络而不是开关**，所以既消除了静音底噪，又不会切字头字尾、不生硬
        //（对比我之前自己拍的"静音不推理 + 淡出"，那个是块级硬切，用户评价"很生硬"）。
        //
        // 参数含义：1.0 = 不处理（保持模型原样输出）；0.0 = 完全用输入包络（官方默认，效果最强）。
        // ------------------------------------------------------------------
        ApplyRmsEnvelopeMix(seg, _in40, skip);

        // 4) SOLA：在 solaSearch 范围内搜最佳偏移
        if (_solaBuffer > 0)
        {
            var search = _solaSearch + 1;
            var bestOffset = 0;
            var bestScore = double.NegativeInfinity;
            double bestEnergy = 0;
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
                if (score > bestScore) { bestScore = score; bestOffset = off; bestEnergy = energy; }
            }

            // ⚠ 相关性不可信时**必须回退到偏移 0**。
            //
            // 对静音/极低电平的片段，所有偏移的归一化互相关都接近 0，argmax 等同于**随机取一个偏移**
            // （离线复现：连续几块选到 345、随机分布），于是每块错位都不同，
            // 输出被搅成一团不连续的噪声——这正是"不说话也有底噪、一说话噪声混着人声"的重要来源之一。
            // 判据：重叠区能量太低、或最佳得分仍然很低，就不动偏移。
            var meanEnergy = bestEnergy / Math.Max(1, _solaBuffer);
            var trustworthy = meanEnergy > SolaMinEnergy && bestScore > SolaMinScore;
            if (!trustworthy) bestOffset = 0;

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

    /// <summary>
    /// 响度包络混合比例：**0 = 完全用输入包络（官方默认，静音处彻底安静）**，
    /// 1 = 不做处理。刻意不加界面滑条，先用官方默认值。
    /// </summary>
    public float RmsMix { get; set; } = 0.0f;

    /// <summary>
    /// 响度包络混合（官方 `rms_mix`）。就地修改 <paramref name="seg"/>。
    ///
    /// 官方实现（`rvc_worker.py` 230~243 行）：
    /// <code>
    /// rms1 = librosa.feature.rms(input_tail[:len], frame_length=4*zc, hop_length=zc)
    /// rms2 = librosa.feature.rms(infer_wav,        frame_length=4*zc, hop_length=zc)
    /// rms2 = maximum(rms2, 1e-3)
    /// infer_wav *= (rms1 / rms2) ** (1.0 - rms_mix)
    /// </code>
    /// 逐样本复刻：先做 40ms 窗 / 10ms 跳的短时 RMS，再线性插值到样本级。
    /// </summary>
    private void ApplyRmsEnvelopeMix(float[] seg, float[] inputWav, int inputOffset)
    {
        if (RmsMix >= 1.0f) return;

        var len = seg.Length;
        var env1 = ShortTimeRms(inputWav, inputOffset, len);
        var env2 = ShortTimeRms(seg, 0, len);
        var power = 1.0 - RmsMix;

        for (var i = 0; i < len; i++)
        {
            var denom = MathF.Max(env2[i], 1e-3f);
            seg[i] *= MathF.Pow(env1[i] / denom, (float)power);
        }
    }

    /// <summary>短时 RMS 包络（窗 4×zc=40ms、跳 zc=10ms），线性插值到逐样本长度。</summary>
    private static float[] ShortTimeRms(float[] source, int offset, int length)
    {
        const int win = 4 * Zc;
        const int hop = Zc;
        var frames = Math.Max(1, length / hop);
        var env = new float[frames + 1];

        for (var f = 0; f <= frames; f++)
        {
            var start = offset + f * hop;
            double sum = 0;
            var count = 0;
            for (var i = 0; i < win; i++)
            {
                var idx = start + i;
                if (idx < 0 || idx >= source.Length) continue;
                var v = source[idx];
                sum += (double)v * v;
                count++;
            }
            env[f] = count > 0 ? (float)Math.Sqrt(sum / count) : 0f;
        }

        // 线性插值到逐样本（对应官方的 F.interpolate(..., mode="linear")）
        var result = new float[length];
        for (var i = 0; i < length; i++)
        {
            var pos = (double)i * frames / Math.Max(1, length - 1);
            var i0 = (int)pos;
            var frac = (float)(pos - i0);
            var i1 = Math.Min(i0 + 1, frames);
            result[i] = env[i0] * (1 - frac) + env[i1] * frac;
        }
        return result;
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _rmvpe.Dispose();
        _synth.Dispose();
        _index?.Dispose();
    }
}
