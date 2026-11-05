using System.Diagnostics;

namespace MateMic.Ai.AiVoiceIpc;

/// <summary>
/// **实时链路模拟自检**（<c>MateMic.exe --rtcheck</c>）：把主进程的"取帧 → 写环 → 消费"
/// 整条路径复现一遍，自己把欠载跑出来，不必再让用户反复试听。
///
/// ## 为什么要这个
///
/// 之前的排查反复卡在同一件事上：日志里"水位下限"显示有 80ms，但欠载时实际水位是 0。
/// 原因是我把水位测在了**写侧**（刚补满一整块那一刻，必然最高），
/// 而真正的最低点在**两次写入之间的消费过程中**。
///
/// 这个自检就是按真实路径来测：
/// <code>
///   取帧线程：宿主 → 阻塞读一帧 → 写入环形缓冲（受 cap 约束，超了丢最老）
///   消费线程：每 10ms 取 480 样本（与音频线程一致），不够就算一次欠载
/// </code>
/// 跑 10 秒，报告欠载次数、消费侧水位下限、产出间隔，以及"预填充是否够"。
///
/// ## 判据
///
/// · 欠载 = 0 且消费侧水位下限 > 0 ⇒ 预填充够；
/// · 欠载 > 0 且水位下限 = 0 ⇒ 预填充不够，差额可从"产出间隔"与"预填充"推出。
/// </summary>
internal static class RealtimeCheck
{
    private const int ChainRate = 48000;
    private const int ChunkSamples = 480;    // 10ms，与音频线程一致
    private const int DurationSeconds = 10;

    public static int Run(string aiDir)
    {
        Console.WriteLine("==== 实时链路模拟（自己复现欠载，不依赖用户试听）====");

        var cv = Path.Combine(aiDir, "engine", "contentvec.onnx");
        var rmvpe = Path.Combine(aiDir, "engine", "rmvpe.onnx");
        var voices = Path.Combine(aiDir, "voices");
        if (!File.Exists(cv) || !File.Exists(rmvpe) || !Directory.Exists(voices))
        {
            Console.WriteLine($"✗ 找不到组件内容：{aiDir}");
            return 1;
        }

        var voice = Directory.GetFiles(voices, "*.onnx").FirstOrDefault();
        if (voice == null) { Console.WriteLine("✗ 没有音色模型"); return 1; }

        // 块长可用 --block 覆盖，便于验证"块长是否大于跨进程推理耗时"这个关键关系。
        var blockMs = 80;
        for (var i = 0; i < 0; i++) { }
        if (Environment.GetEnvironmentVariable("MATEMIC_RT_BLOCK_MS") is { Length: > 0 } custom
            && int.TryParse(custom, out var parsed) && parsed >= 20 && parsed <= 1000)
        {
            blockMs = parsed;
        }
        var block = blockMs * ChainRate / 1000;   // 3840

        var parameters = new AiVoiceHostParams
        {
            Cv = cv,
            Rmvpe = rmvpe,
            Voice = voice,
            BlockMs = blockMs,
            ContextMs = 1000,
            CrossfadeMs = 40,
            Semitones = 12,
            IndexRate = 0f,
            UseGpu = true,
        };

        var client = AiVoiceHostClient.Start(parameters, out var startError);
        if (client == null) { Console.WriteLine("启动失败：" + startError); return 1; }

        try
        {
            if (!client.WaitReady(180_000, out var readyError))
            {
                Console.WriteLine("未就绪：" + readyError);
                return 1;
            }
            Console.WriteLine($"宿主就绪，块长 {blockMs}ms（{block} 样本）");

            // 环形缓冲：容量 4 秒（与生产一致）
            var ring = new float[ChainRate * 4];
            var read = 0;
            var write = 0;
            var gate = new object();
            long minAvailMs = long.MaxValue;
            long underruns = 0;
            long consumed = 0;
            var producedFrames = 0;
            long maxGapMs = 0;

            // **逐帧样本数统计**（2026-11-05 新增）。
            // 之前我一直用"帧数 × 块长"推算产出时长，前提是每帧恰好一个块。
            // 如果实际帧长不等于块长（少几个样本、或偶尔少很多），就会造成持续的小额亏损，
            // 而这与块长、推理速度都无关 —— 属于"其他地方"。
            long producedSamples = 0;
            var minFrame = int.MaxValue;
            var maxFrame = 0;
            var shortFrames = 0;   // 帧长 ≠ 块长的帧数
            var droppedFrames = 0; // ready 队列满被丢掉的帧数（模拟生产侧）

            // ---- 取帧线程：模拟 HostFetcherLoop ----
            var running = true;
            var netPadMs = (long)Math.Max(2 * blockMs, 200);   // 初始预填充：2 块
            var padSamples = (int)(netPadMs * ChainRate / 1000);

            var fetcher = new Thread(() =>
            {
                var scratch = new float[block * 2];
                var lastMs = 0L;
                var started = Stopwatch.StartNew();
                while (running)
                {
                    var got = client.ReadOutput(scratch);
                    if (got <= 0) break;

                    var nowMs = started.ElapsedMilliseconds;
                    if (lastMs != 0)
                    {
                        var gap = nowMs - lastMs;
                        if (gap > maxGapMs) maxGapMs = gap;
                    }
                    lastMs = nowMs;

                    lock (gate)
                    {
                        for (var i = 0; i < got; i++)
                        {
                            var next = (write + 1) % ring.Length;
                            if (next == read) read = (read + 1) % ring.Length;   // 满了丢最老
                            ring[write] = scratch[i];
                            write = next;
                        }

                        var avail = (write - read + ring.Length) % ring.Length;
                        while (avail > padSamples + block)
                        {
                            read = (read + 1) % ring.Length;
                            avail--;
                        }
                        producedFrames++;
                    }

                    // 逐帧样本数（与块长比较，任何偏差都记下来）
                    producedSamples += got;
                    if (got < minFrame) minFrame = got;
                    if (got > maxFrame) maxFrame = got;
                    if (got != block) shortFrames++;
                }
            })
            { IsBackground = true, Name = "rt.Fetch" };

            // 初始化环：预填充静音
            write = padSamples % ring.Length;
            read = 0;
            producedFrames = 0;

            fetcher.Start();

            // ---- 喂线程（独立）：按实时速率把"采集到的"样本送进宿主 ----
            // ⚠ 必须在**独立线程**上：WriteInput 是阻塞的，放在消费循环里会打乱 10ms 节拍，
            // 把"消费侧欠载"这个测量本身污染掉（第一版就踩了这个坑）。
            var feederStopped = false;
            var feeder = new Thread(() =>
            {
                // **攒满一整块再写**（2026-11-05 关键修正）。
                // 每 10ms 写 480 样本的话，凑一块要 8 次系统调用 + 8 次线程唤醒，
                // 这些开销会和推理线程抢 CPU，把推理从 65ms 拖到 100ms（实测）。
                // 改成攒满一块再写，调用次数降到 1/8。
                var feed = new float[block];
                var feedClock = Stopwatch.StartNew();
                var bound = 0.0;
                while (!feederStopped)
                {
                    bound += block * 1000.0 / ChainRate;   // 按块长节拍
                    var wait = bound - feedClock.Elapsed.TotalMilliseconds;
                    if (wait > 0) Thread.Sleep((int)Math.Max(0, wait));
                    if (feederStopped) break;
                    if (!client.WriteInput(feed)) break;
                }
            })
            { IsBackground = true, Name = "rt.Feed" };
            feeder.Start();

            // ---- 消费线程（主线程）：每 10ms 取 480 样本 ----
            var clock = Stopwatch.StartNew();
            var nextDeadline = 0.0;
            var chunk = new float[ChunkSamples];

            while (clock.Elapsed.TotalSeconds < DurationSeconds)
            {
                // 按 10ms 节拍推进（不用 Thread.Sleep 累加误差，用绝对时刻）
                nextDeadline += ChunkSamples * 1000.0 / ChainRate;
                var waitMs = nextDeadline - clock.Elapsed.TotalMilliseconds;
                if (waitMs > 0) Thread.Sleep((int)Math.Max(0, waitMs));
                if (clock.Elapsed.TotalSeconds >= DurationSeconds) break;

                lock (gate)
                {
                    var avail = (write - read + ring.Length) % ring.Length;
                    var availMs = avail * 1000L / ChainRate;
                    if (availMs < minAvailMs) minAvailMs = availMs;

                    if (avail >= ChunkSamples)
                    {
                        read = (read + ChunkSamples) % ring.Length;
                        consumed += ChunkSamples;
                    }
                    else
                    {
                        underruns++;
                        consumed += ChunkSamples;
                    }
                }
            }

            feederStopped = true;
            running = false;
            try { client.ClosePipes(); } catch { }
            fetcher.Join(2000);
            feeder.Join(2000);

            clock.Stop();
            var seconds = clock.Elapsed.TotalSeconds;

            Console.WriteLine($"\n-- 结果（{seconds:0.0} 秒）--");
            Console.WriteLine($"  消费 {consumed * 1000 / ChainRate / 1000.0:0.0} 秒音频｜欠载 {underruns} 次"
                              + $"（{underruns / seconds:0.0} 次/秒）");
            Console.WriteLine($"  产出 {producedFrames} 帧｜最长产出间隔 {maxGapMs}ms（块长 {blockMs}ms）");
            Console.WriteLine($"  产出样本 {producedSamples}（{producedSamples * 1000.0 / ChainRate:0.00} 秒）"
                              + $"｜帧长 最小 {minFrame} / 最大 {maxFrame}（块长 {block} 样本）"
                              + $"｜帧长≠块长的帧 **{shortFrames}** 个");
            Console.WriteLine($"  宿主报告推理耗时 {client.HostInferMs:0}ms"
                              + $"（若明显大于块长 {blockMs}ms，产能必然不足）");
            Console.WriteLine($"  预填充 {padSamples * 1000 / ChainRate}ms｜**消费侧水位下限 {minAvailMs}ms**");

            var ok = underruns == 0 && minAvailMs > 0;
            Console.WriteLine(ok
                ? "  ✓ 预填充足够：无欠载，水位始终 > 0"
                : $"  ✗ 预填充不足：水位被吃到 {minAvailMs}ms，"
                  + $"理论所需 ≈ 块长 {blockMs} + 最长间隔 {maxGapMs} = {blockMs + maxGapMs}ms");

            return ok ? 0 : 1;
        }
        finally
        {
            client.Dispose();
        }
    }
}
