using System.Collections.Concurrent;
using System.Text.Json;

namespace MateMic.Ai.AiVoiceIpc;

/// <summary>
/// **AI 变声宿主进程**（<c>MateMic.exe --aivoice-host --shm-name &lt;名字&gt;</c>）。
///
/// 独立进程的意义：关掉 AI 变声 = 结束这个进程 ⇒ 模型内存、CUDA context、显存、faiss 索引
/// **全部由操作系统回收**。进程内的 <c>Dispose</c> 做不到这一点 ——
/// ONNX Runtime 与 CUDA 的内存池本来就不还给系统（实测关闭后仍常驻约 900 MB）。
///
/// ## 为什么是"读线程 + 推理线程"两段流水（2026-11-05 关键修正）
///
/// 第一版是**单线程串行**：`攒满一块 → 推理 → 写回 → 再攒`。
/// 于是每块耗时 = 块长（等输入）+ 推理耗时，而音频是**实时**送来的
/// ⇒ 产能 = 块长 / (块长 + 推理) < 100%，结构性供不上。
/// 实测（块长 200ms、推理 60ms）产能仅 200/260 = 77%，
/// 表现为主进程输出环持续被抽干、周期性补静音（用户听到的爆音）。
///
/// 现在拆成两段流水，**"攒块"与"推理"重叠**：
/// <code>
///   读线程：管道 ──► 攒满一块 ──► ready 队列
///   推理线程：ready 队列 ──► 推理 ──► 写回管道
/// </code>
/// 这样每块的稳态耗时降到 **max(块长, 推理耗时)**，只要推理快于块长就能实时。
/// 队列容量 2 块：够吸收抖动，又不会积压成延迟。
///
/// ## 与主进程的约定
///
/// · 参数/状态：<see cref="AiVoiceControlBlock"/>（主进程写参数、宿主写状态）；
/// · 音频：两条单向 <see cref="AudioPipeLink"/>（in = 主→宿主，out = 宿主→主），
///   均为 48 kHz 单声道 float，全同步阻塞（所以必须分线程，见上）；
/// · 缓冲几何（块长等）由参数决定，**改了参数就重建处理器**（主进程 UI 本来就要求
///   先关 AI 变声再改，所以不做热切换）。
/// </summary>
internal static class AiVoiceHost
{
    private const int ChainRate = 48000;

    /// <summary>待推理队列的容量（块）。2 块够吸收抖动，又不至于积压成延迟。</summary>
    private const int ReadyQueueDepth = 2;

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

        using var ctl = AiVoiceControlBlock.OpenHost(name + "-ctl", timeoutMs: 10_000);
        if (ctl == null)
        {
            Console.Error.WriteLine("[宿主] 10 秒内未等到控制块，退出");
            return 3;
        }

        // 音频走命名管道（全同步阻塞）：主进程写 in、读 out，宿主反向。
        // 顺序与主进程对应：主进程先起本进程，再 CreateServer(in)、CreateServer(out)，
        // 所以这里先连 in、再连 out。
        var pipeIn = AudioPipeLink.ConnectClient(name + "-in", isWriter: false, timeoutMs: 20_000);
        if (pipeIn == null)
        {
            Console.Error.WriteLine("[宿主] 连不上输入管道，退出");
            ctl.SetState(AiVoiceHostState.Failed, 0, "输入管道未连通");
            return 4;
        }

        var pipeOut = AudioPipeLink.ConnectClient(name + "-out", isWriter: true, timeoutMs: 20_000);
        if (pipeOut == null)
        {
            Console.Error.WriteLine("[宿主] 连不上输出管道，退出");
            ctl.SetState(AiVoiceHostState.Failed, 0, "输出管道未连通");
            pipeIn.Dispose();
            return 4;
        }

        ctl.SetState(AiVoiceHostState.Loading, 0.02);

        RvcBlockProcessor? processor = null;
        var appliedVersion = -1;

        try
        {
            // ---- 等参数并加载 ----
            while (!ctl.IsClosed)
            {
                var (version, json) = ctl.GetParams();
                if (version == appliedVersion) { ctl.ParamsChanged.WaitOne(100); continue; }

                appliedVersion = version;
                if (string.IsNullOrEmpty(json)) { ctl.ParamsChanged.WaitOne(100); continue; }

                processor?.Dispose();
                processor = null;

                var (ok, error) = Build(ctl, json, out processor);
                if (!ok)
                {
                    ctl.SetState(AiVoiceHostState.Failed, 0, error);
                    Console.Error.WriteLine("[宿主] 加载失败：" + error);
                    // 不退出：主进程可能马上改参数再试（换音色/换索引）
                    continue;
                }

                Console.WriteLine($"[宿主] 就绪：块 {processor.BlockSamples48} 样本（{processor.BlockSamples48 / 48.0:0.#} ms）");
                break;
            }

            if (processor == null)
            {
                Console.Error.WriteLine("[宿主] 没拿到可用参数，退出");
                return 5;
            }

            ctl.SetState(AiVoiceHostState.Ready, 1.0);

            // ---- 两段流水 ----
            //
            // ⚠ 用**固定双槽位 + SemaphoreSlim**，不用 BlockingCollection
            //（2026-11-05 实测：每块产出间隔 126ms 而块长只有 120ms，多出的 6ms
            //  就是"队列交接 + 线程唤醒"的固定开销；按 10 秒算会让产能只有 94.8%，
            //  持续低于消费速度 ⇒ 水位必然被吃穿 ⇒ 周期性爆音。
            //  BlockingCollection 内部有锁 + 队列 + 条件变量，比裸信号量重。
            //  但**不能**退回单线程串行：那会让间隔变成 块长 + 推理，更糟。）
            var slots = new float[2][];
            var freeSlots = new SemaphoreSlim(2, 2);   // 可写槽位数
            var filledSlots = new SemaphoreSlim(0, 2); // 可读槽位数
            var slotWrite = 0;
            var slotRead = 0;
            var running = true;

            // 读线程：攒满一块就放进下一个空槽位。
            var reader = new Thread(() =>
            {
                var buffer = new float[processor.BlockSamples48];
                try
                {
                    while (running && !ctl.IsClosed)
                    {
                        var got = 0;
                        while (got < buffer.Length)
                        {
                            var need = buffer.Length - got;
                            var n = pipeIn.Read(buffer.AsSpan(got, need));
                            if (n <= 0) { running = false; return; }
                            got += n;
                        }

                        // 等空槽位（最多 10ms，等到丢本块保实时）
                        if (!freeSlots.Wait(10)) continue;

                        slots[slotWrite] = buffer;
                        slotWrite = (slotWrite + 1) % 2;
                        filledSlots.Release();

                        buffer = new float[processor.BlockSamples48];
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[宿主] 读取线程异常：" + ex.Message);
                }
            })
            { IsBackground = true, Name = "AiVoiceHost.Reader" };

            // 推理线程：从槽位取块，推理，写回。
            var worker = new Thread(() =>
            {
                var out48 = new float[processor.BlockSamples48 + 4096];
                try
                {
                    while (running && !ctl.IsClosed)
                    {
                        if (!filledSlots.Wait(50)) continue;

                        var block = slots[slotRead];
                        slotRead = (slotRead + 1) % 2;
                        freeSlots.Release();

                        // 热参数：每块读一次（变调/索引占比改了立刻生效，不重建引擎）
                        var (hotSemitones, hotRate) = ctl.GetHotParams();
                        processor.SetHotParams(hotSemitones, hotRate);

                        var produced = processor.Process(block, out48);
                        ctl.SetHostInferMs(processor.LastInferMs);

                        // ⚠ 不要每块写日志：宿主的 stdout 被父进程重定向，
                        // 每块写一行会**阻塞推理线程**（实测把产出间隔从 80ms 拉到 160ms）。
                        if (processor.Blocks % 200 == 0)
                        {
                            Console.WriteLine($"[宿主] 已处理 {processor.Blocks} 块，最近一次推理 {processor.LastInferMs:0}ms");
                        }

                        if (!pipeOut.Write(out48.AsSpan(0, produced)))
                        {
                            running = false;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[宿主] 推理线程异常：" + ex.Message);
                }
            })
            { IsBackground = true, Name = "AiVoiceHost.Worker" };

            reader.Start();
            worker.Start();

            // 主线程只负责"等结束信号"
            while (running && !ctl.IsClosed) Thread.Sleep(50);

            running = false;
            try { pipeIn.Dispose(); } catch { }      // 打断阻塞读
            try { pipeOut.Dispose(); } catch { }     // 打断阻塞写

            reader.Join(2000);
            worker.Join(2000);

            return 0;
        }
        finally
        {
            ctl.SetState(AiVoiceHostState.Stopping, 1.0);
            processor?.Dispose();
            try { pipeOut.Dispose(); } catch { }
            try { pipeIn.Dispose(); } catch { }
            Console.WriteLine("[宿主] 已退出");
        }
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
