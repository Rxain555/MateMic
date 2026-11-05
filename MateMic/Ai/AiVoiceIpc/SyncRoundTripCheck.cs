using System.Diagnostics;

namespace MateMic.Ai.AiVoiceIpc;

/// <summary>
/// **同步往返耗时测量**（<c>MateMic.exe --synccheck</c>）。
///
/// 目的：验证"能不能像官方 realtime_gui 那样，在音频回调里**同步**把一块交给宿主、
/// 等它算完再返回"。
///
/// 官方 `sd.Stream(blocksize=block_frame)` 就是这么做的 —— 它在回调里同步推理，
/// 因此**没有任何额外缓冲，也就没有额外延迟**；代价是推理超过块长就直接欠载。
/// 我们之前做成异步 + 预填充，虽然不阻塞音频线程，却凭空引入了一整个推理耗时的延迟。
///
/// 这条路能不能走，取决于一个数：**一次同步往返的耗时**。
/// 只要它明显小于块长（留出抖动余量），就成立；否则音频回调会被拖到超时。
///
/// 测三档块长（200 / 250 / 500ms），每档连续 20 次，报告 最小 / 中位 / 最大 与"占块长比例"。
/// </summary>
internal static class SyncRoundTripCheck
{
    private const int ChainRate = 48000;

    public static int Run(string aiDir)
    {
        Console.WriteLine("==== 同步往返耗时测量（官方 realtime_gui 的做法）====");

        var cv = Path.Combine(aiDir, "engine", "contentvec.onnx");
        var rmvpe = Path.Combine(aiDir, "engine", "rmvpe.onnx");
        var voices = Path.Combine(aiDir, "voices");
        if (!File.Exists(cv) || !File.Exists(rmvpe))
        {
            Console.WriteLine($"✗ 找不到模型：{cv} / {rmvpe}");
            return 1;
        }
        if (!Directory.Exists(voices))
        {
            Console.WriteLine($"✗ 找不到音色目录：{voices}");
            return 1;
        }

        var voice = Directory.GetFiles(voices, "*.onnx").FirstOrDefault();
        if (voice == null) { Console.WriteLine("✗ 没有音色模型"); return 1; }

        var failures = 0;

        foreach (var blockMs in new[] { 200, 250, 500 })
        {
            Console.WriteLine($"\n-- 块长 {blockMs} ms --");
            var block = blockMs * ChainRate / 1000;

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
            if (client == null) { Console.WriteLine("  启动失败：" + startError); failures++; continue; }

            try
            {
                if (!client.WaitReady(180_000, out var readyError))
                {
                    Console.WriteLine("  未就绪：" + readyError);
                    failures++;
                    continue;
                }

                var input = new float[block];
                for (var i = 0; i < block; i++) input[i] = MathF.Sin(i * 2 * MathF.PI * 220 / ChainRate) * 0.3f;
                var output = new float[block + 4096];

                // 预热两次（第一次包含 CUDA kernel 首次调度，不代表稳态）
                for (var i = 0; i < 2; i++)
                {
                    client.WriteInput(input);
                    client.ReadOutput(output);
                }

                var times = new List<double>();
                const int iterations = 20;
                for (var i = 0; i < iterations; i++)
                {
                    var clock = Stopwatch.StartNew();
                    var ok = client.WriteInput(input);
                    var got = client.ReadOutput(output);
                    clock.Stop();

                    if (!ok || got <= 0)
                    {
                        Console.WriteLine($"  第 {i + 1} 次往返失败（ok={ok}, got={got}）");
                        failures++;
                        break;
                    }
                    times.Add(clock.Elapsed.TotalMilliseconds);
                }

                if (times.Count > 0)
                {
                    var sorted = times.OrderBy(t => t).ToList();
                    var median = sorted[sorted.Count / 2];
                    var max = sorted[^1];
                    var min = sorted[0];
                    var ratio = median / blockMs * 100;
                    var ok = max < blockMs;   // 判据：**最坏情况**也要在块长内完成

                    Console.WriteLine($"  {(ok ? "✓" : "✗")} 同步往返：最小 {min:0.0} / 中位 {median:0.0} / 最大 {max:0.0} ms"
                                      + $"（块长 {blockMs} ms，中位占 {ratio:0.0}%，最大占 {max / blockMs * 100:0.0}%）");
                    if (!ok) failures++;
                }
            }
            finally
            {
                client.Dispose();
            }
        }

        Console.WriteLine(failures == 0
            ? "\n==== 全部通过：可以在音频回调里同步往返 ===="
            : $"\n==== 有 {failures} 项不满足（同步往返超时，不能直接用在音频线程）====");
        return failures == 0 ? 0 : 1;
    }
}
