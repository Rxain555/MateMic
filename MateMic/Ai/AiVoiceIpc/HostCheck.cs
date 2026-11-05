using System.Diagnostics;
using MateMic.Core;

namespace MateMic.Ai.AiVoiceIpc;

/// <summary>
/// 宿主进程的**端到端自检**（<c>MateMic.exe --hostcheck [--ai &lt;组件目录&gt;]</c>）。
///
/// 走的完全是生产路径（<see cref="AiVoiceHostClient"/>）：真起子进程、真加载 ONNX 模型、
/// 真送一块音频再收回来、最后确认子进程能优雅退出。一次验证四件事：
///   1. 子进程能起来并连上共享内存；
///   2. 参数通道能把模型路径（含中文/空格）正确下发；
///   3. 加载完成后能收到音频（**进程边界没有破坏数据通路**）；
///   4. 请求关闭后子进程**自己退出**（这是"关掉就能释放内存"的前提）。
///
/// 第 3 条只验证"通路"，不验证音质 —— 音质要人耳听。
/// </summary>
internal static class HostCheck
{
    private const int ChainRate = 48000;

    public static int Run(string[] args)
    {
        Console.WriteLine("==== AI 变声宿主进程端到端自检 ====");

        var aiRoot = GetArg(args, "--ai") ?? Path.Combine(ConfigStore.ComponentsDirectory, "ai");
        var cv = Path.Combine(aiRoot, "engine", "contentvec.onnx");
        var rmvpe = Path.Combine(aiRoot, "engine", "rmvpe.onnx");
        var voicesDir = Path.Combine(aiRoot, "voices");

        Console.WriteLine($"组件目录：{aiRoot}");
        if (!File.Exists(cv)) { Console.WriteLine($"✗ 缺 {cv}"); return 1; }
        if (!File.Exists(rmvpe)) { Console.WriteLine($"✗ 缺 {rmvpe}"); return 1; }

        var voice = Directory.Exists(voicesDir)
            ? Directory.EnumerateFiles(voicesDir, "*.onnx").OrderBy(f => f).FirstOrDefault()
            : null;
        if (voice == null) { Console.WriteLine($"✗ {voicesDir} 下没有音色 .onnx"); return 1; }
        Console.WriteLine($"音色：{Path.GetFileName(voice)}");

        var failures = 0;
        var clock = Stopwatch.StartNew();

        var parameters = new AiVoiceHostParams
        {
            Cv = cv,
            Rmvpe = rmvpe,
            Voice = voice,
            Index = null,
            BlockMs = 160,
            ContextMs = 320,
            CrossfadeMs = 40,
            Semitones = 0,
            IndexRate = 0f,
            UseGpu = false,   // 自检走 CPU，避免与用户正在用的 GPU 抢资源
        };

        var client = AiVoiceHostClient.Start(parameters, ChainRate * 2, out var startError);
        if (client == null)
        {
            Console.WriteLine("✗ 启动宿主失败：" + startError);
            return 1;
        }

        try
        {
            Console.WriteLine($"子进程已启动，PID={client.HostPid}，共享内存前缀 {client.Name}");

            if (!client.WaitReady(180_000, out var readyError))
            {
                Console.WriteLine($"✗ 宿主未就绪：{readyError}");
                Console.WriteLine($"  子进程 IsAlive={client.IsAlive}｜状态={client.State}｜进度={client.Progress:0.00}");
                return 1;
            }

            clock.Stop();
            Console.WriteLine($"✓ 宿主就绪，加载耗时 {clock.ElapsedMilliseconds} ms（CPU 模式）");

            // ---- 送 3 块、收回来（CPU 推理很慢，给足超时）----
            const int blockMs = 160;
            var block = blockMs * ChainRate / 1000;
            var src = new float[block];
            for (var i = 0; i < block; i++)
                src[i] = MathF.Sin(i * 2 * MathF.PI * 220f / ChainRate) * 0.3f;

            var sendClock = Stopwatch.StartNew();
            for (var b = 0; b < 3; b++)
            {
                var written = 0;
                while (written < src.Length)
                {
                    var n = client.WriteInput(src.AsSpan(written));
                    if (n <= 0) { Thread.Sleep(2); continue; }
                    written += n;
                }
            }
            Console.WriteLine($"已送入 {3 * block} 样本（{3 * block / 48.0:0} ms 音频，3 块）");

            var received = 0;
            var buffer = new float[8192];
            var deadline = Environment.TickCount64 + 90_000;
            while (received < block * 2 && Environment.TickCount64 < deadline)
            {
                var n = client.ReadOutput(buffer);
                if (n > 0) { received += n; continue; }
                if (!client.IsAlive) break;
                Thread.Sleep(5);
            }
            sendClock.Stop();

            var receiveOk = received >= block * 2;
            Console.WriteLine($"{(receiveOk ? "✓" : "✗")} 收回 {received} 样本（{received / 48.0:0} ms 音频）"
                              + $"｜等输出 {sendClock.ElapsedMilliseconds} ms");
            if (!receiveOk) failures++;

            // ---- 优雅关闭 ----
            var stopClock = Stopwatch.StartNew();
            var graceful = client.Stop();
            stopClock.Stop();
            Console.WriteLine($"{(graceful ? "✓" : "✗")} 请求关闭后子进程自己退出={graceful}（{stopClock.ElapsedMilliseconds} ms）");
            if (!graceful) failures++;
        }
        catch (Exception ex)
        {
            Console.WriteLine("✗ 自检抛异常：" + ex);
            failures++;
        }
        finally
        {
            client.Dispose();
        }

        Console.WriteLine(failures == 0 ? "==== 全部通过 ====" : $"==== 有 {failures} 项失败 ====");
        return failures == 0 ? 0 : 1;
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
