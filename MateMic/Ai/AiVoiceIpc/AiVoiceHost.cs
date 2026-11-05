using System.Text.Json;

namespace MateMic.Ai.AiVoiceIpc;

/// <summary>
/// **AI 变声宿主进程**（<c>MateMic.exe --aivoice-host --shm-name &lt;名字&gt;</c>）。
///
/// 独立进程的意义：关掉 AI 变声 = 结束这个进程 ⇒ 模型内存、CUDA context、显存、faiss 索引
/// **全部由操作系统回收**。进程内的 <c>Dispose</c> 做不到这一点 ——
/// ONNX Runtime 与 CUDA 的内存池本来就不还给系统（实测关闭后仍常驻约 900 MB）。
///
/// ## 与主进程的约定
///
/// · 参数/状态：<see cref="AiVoiceControlBlock"/>（主进程写参数、宿主写状态）；
/// · 音频：两个 <see cref="AudioShmRing"/>（in = 主→宿主，out = 宿主→主）。
///   两边都是 48 kHz 单声道 float；
/// · 缓冲区几何（块长等）由参数决定，**改了参数就重建处理器**（主进程那边的 UI
///   本来就要求"先关 AI 变声再改"，所以这里不做热切换）。
///
/// ## 为什么延迟不会变差
///
/// 宿主只是原样调用 <see cref="StreamingRvc"/>：窗口、SOLA、交叉淡化、上下文长度全都不变。
/// 多出来的只有"共享内存拷贝 + 事件唤醒"，实测每块往返 13~108 µs
/// （占音频时长 0.02%~0.07%，见 <see cref="ShmRingCheck"/>）。
/// 输出环的水位管理仍由主进程负责，深度也没变。
/// </summary>
internal static class AiVoiceHost
{
    private const int ChainRate = 48000;

    /// <summary>宿主主循环。返回进程退出码（0 = 正常）。</summary>
    public static int Run(string[] args)
    {
        var name = GetArg(args, "--shm-name");
        if (string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine("[宿主] 缺少 --shm-name");
            return 2;
        }

        Console.WriteLine($"[宿主] 启动，共享内存前缀 = {name}");

        // 控制块：主进程负责创建，这里等它出现
        using var ctl = AiVoiceControlBlock.OpenHost(name + "-ctl", timeoutMs: 10000);
        if (ctl == null)
        {
            Console.Error.WriteLine("[宿主] 10 秒内未等到控制块，退出");
            return 3;
        }

        // 输入环由主进程创建，输出环由宿主创建（谁创建谁负责，避免竞态）
        var inputRing = AudioShmRing.OpenConsumer(name + "-in", timeoutMs: 10000);
        if (inputRing == null)
        {
            Console.Error.WriteLine("[宿主] 10 秒内未等到输入环，退出");
            ctl.SetState(AiVoiceHostState.Failed, 0, "输入共享内存未就绪");
            return 4;
        }

        using var outputRing = AudioShmRing.CreateProducer(name + "-out", capacitySamples: ChainRate * 2);
        ctl.SetState(AiVoiceHostState.Loading, 0.02);

        RvcBlockProcessor? processor = null;
        var appliedVersion = -1;
        var buffer48 = Array.Empty<float>();
        var out48 = Array.Empty<float>();
        var scratch48 = new float[ChainRate];

        try
        {
            while (!ctl.IsClosed && !inputRing.IsClosed)
            {
                // ---- 1) 参数变了就（重）建处理器 ----
                var (version, json) = ctl.GetParams();
                if (version != appliedVersion)
                {
                    appliedVersion = version;
                    if (string.IsNullOrEmpty(json))
                    {
                        // 还没收到参数：等一下，别把"没配置"当成"配置错误"
                        ctl.ParamsChanged.WaitOne(100);
                        continue;
                    }

                    processor?.Dispose();
                    processor = null;

                    var (ok, error) = Build(ctl, json, out processor);
                    if (!ok)
                    {
                        ctl.SetState(AiVoiceHostState.Failed, 0, error);
                        Console.Error.WriteLine("[宿主] 加载失败：" + error);
                        // 不退出：主进程可能马上改参数再试（换音色/换索引）
                        appliedVersion = version;
                        continue;
                    }

                    buffer48 = new float[processor.BlockSamples48];
                    out48 = new float[processor.BlockSamples48 + 4096];
                    ctl.SetState(AiVoiceHostState.Ready, 1.0);
                    Console.WriteLine($"[宿主] 就绪：块 {processor.BlockSamples48} 样本（{processor.BlockSamples48 / 48.0:0.#} ms）");
                }

                if (processor == null)
                {
                    // 参数还没来 / 上次失败：把输入读掉丢弃，避免主进程侧积压
                    while (inputRing.Read(scratch48) > 0) { }
                    inputRing.WaitForData(20);
                    continue;
                }

                // ---- 2) 攒够一块就处理 ----
                var got = 0;
                while (got < buffer48.Length)
                {
                    var need = buffer48.Length - got;
                    // 从环里分次读（环可能一次给不满）
                    var chunk = inputRing.Read(scratch48.AsSpan(0, Math.Min(need, scratch48.Length)));
                    if (chunk <= 0)
                    {
                        if (ctl.IsClosed) return 0;
                        // 数据不够一块：等一小会儿。**不能等太久**，否则输入环会积压成延迟
                        inputRing.WaitForData(2);
                        continue;
                    }

                    scratch48.AsSpan(0, chunk).CopyTo(buffer48.AsSpan(got));
                    got += chunk;
                }

                // ---- 3) 处理并写回 ----
                int produced;
                try
                {
                    produced = processor.Process(buffer48, out48);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[宿主] 推理失败：" + ex.Message);
                    ctl.SetState(AiVoiceHostState.Failed, 0, "推理失败：" + ex.Message);
                    return 5;
                }

                // 输出环满时**丢最旧的**（保证实时性）：这与主进程侧的水位策略一致
                var written = 0;
                while (written < produced)
                {
                    var n = outputRing.Write(out48.AsSpan(written, produced - written));
                    if (n <= 0)
                    {
                        // 环满：等主进程取走（最多 20 ms，避免死等）
                        if (!outputRing.WaitForRoom(20)) { /* 真满了就丢本块剩余 */ break; }
                        continue;
                    }
                    written += n;
                }
            }
        }
        finally
        {
            ctl.SetState(AiVoiceHostState.Stopping, 1.0);
            processor?.Dispose();
            inputRing.Dispose();
            Console.WriteLine("[宿主] 已退出");
        }

        return 0;
    }

    /// <summary>按参数 JSON 构建处理器（含分阶段加载计时，用于判断那 1.5~2 秒能否压缩）。</summary>
    private static (bool Ok, string Error) Build(AiVoiceControlBlock ctl, string json, out RvcBlockProcessor? processor)
    {
        processor = null;
        try
        {
            var p = JsonSerializer.Deserialize<HostParams>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (p == null) return (false, "参数 JSON 解析为空");
            if (string.IsNullOrWhiteSpace(p.Voice)) return (false, "未指定音色模型");
            if (string.IsNullOrWhiteSpace(p.Cv)) return (false, "未指定 contentvec 模型");
            if (string.IsNullOrWhiteSpace(p.Rmvpe)) return (false, "未指定 rmvpe 模型");

            ctl.SetState(AiVoiceHostState.Loading, 0.05);

            // 索引：可选。读它要几百毫秒，没开索引就别读（这也是"1.5 秒"里能省的一部分）
            FaissIndex? index = null;
            if (!string.IsNullOrWhiteSpace(p.Index) && p.IndexRate > 0.001f)
            {
                ctl.SetState(AiVoiceHostState.Loading, 0.1);
                index = FaissIndex.Open(p.Index);
            }

            ctl.SetState(AiVoiceHostState.Loading, 0.2);

            var rvc = new StreamingRvc(
                p.Cv, p.Rmvpe, p.Voice,
                blockMs: p.BlockMs,
                contextMs: p.ContextMs,
                crossfadeMs: p.CrossfadeMs,
                semitones: p.Semitones,
                useGpu: p.UseGpu,
                index: index,
                indexRate: p.IndexRate,
                onProgress: percent => ctl.SetState(AiVoiceHostState.Loading, 0.2 + percent / 100.0 * 0.75));

            processor = new RvcBlockProcessor(rvc, p.BlockMs);
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    /// <summary>主进程通过参数 JSON 下发的配置。</summary>
    private sealed class HostParams
    {
        public string? Cv { get; set; }
        public string? Rmvpe { get; set; }
        public string? Voice { get; set; }
        public string? Index { get; set; }
        public int BlockMs { get; set; } = 160;
        public int ContextMs { get; set; } = 320;
        public int CrossfadeMs { get; set; } = 40;
        public int Semitones { get; set; }
        public float IndexRate { get; set; }
        public bool UseGpu { get; set; } = true;
    }
}
