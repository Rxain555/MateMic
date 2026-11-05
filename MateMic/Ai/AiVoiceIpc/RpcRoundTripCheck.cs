using System.Diagnostics;
using MateMic.Core;
using MateMic.Denoise;

namespace MateMic.Ai.AiVoiceIpc;

/// <summary>
/// **往返一致性自检**（<c>MateMic.exe --rpc-check [--ai &lt;组件目录&gt;]</c>）。
///
/// 目的：判断"跨进程后音色不对"到底是**搬运路径**的问题，还是**主进程环管理**的问题。
///
/// 做法：同一段音频、同样的块长与参数，先走进程内（`StreamingRvc` 直接处理），
/// 再走跨进程（宿主处理），把两份输出逐样本比对：
///   · 若两份**基本一致**（相关系数 > 0.99）⇒ 搬运路径没问题，
///     问题在主进程的输入/输出环管理（欠载、丢块、错位）；
///   · 若两份**明显不同** ⇒ 宿主里的 DSP 与进程内不等价，问题在宿主。
///
/// 用正弦波而不是语音：正弦对"块边界错位/采样率不匹配"极其敏感（会立刻变成
/// 刺耳的非谐波声音），但它**不含语音语义**，所以只能看出波形差异、看不出音色好坏 ——
/// 这正好符合本自检的用途（只判"是不是同一份信号"）。
/// </summary>
internal static class RpcRoundTripCheck
{
    private const int ChainRate = 48000;
    private const int ModelRate = 40000;

    public static int Run(string[] args)
    {
        Console.WriteLine("==== 跨进程往返一致性自检 ====");

        var aiRoot = GetArg(args, "--ai") ?? Path.Combine(ConfigStore.ComponentsDirectory, "ai");
        var cv = Path.Combine(aiRoot, "engine", "contentvec.onnx");
        var rmvpe = Path.Combine(aiRoot, "engine", "rmvpe.onnx");
        var voicesDir = Path.Combine(aiRoot, "voices");
        if (!File.Exists(cv) || !File.Exists(rmvpe)) { Console.WriteLine($"✗ 组件不全：{aiRoot}"); return 1; }

        var voice = Directory.Exists(voicesDir)
            ? Directory.EnumerateFiles(voicesDir, "*.onnx").OrderBy(f => f).FirstOrDefault()
            : null;
        if (voice == null) { Console.WriteLine($"✗ {voicesDir} 下没有音色"); return 1; }

        const int blockMs = 160;
        const int blocks = 6;                        // 6 块 = 0.96 秒，够比对且 CPU 上跑得快
        var block = blockMs * ChainRate / 1000;      // 7680

        // 输入：220 Hz 正弦（低频，块边界错位会立刻变成刺耳的谐波噪声）
        var input = new float[block * blocks];
        for (var i = 0; i < input.Length; i++)
            input[i] = MathF.Sin(i * 2 * MathF.PI * 220f / ChainRate) * 0.35f;

        Console.WriteLine($"工程：块 {blockMs}ms × {blocks} 块 = {input.Length / (double)ChainRate:0.00} 秒，音色 {Path.GetFileName(voice)}");

        // ---- 路径 A：进程内 ----
        Console.WriteLine("\n-- 路径 A：进程内 --");
        var clock = Stopwatch.StartNew();
        var outputA = RunInProcess(cv, rmvpe, voice, blockMs, block, blocks, input);
        clock.Stop();
        if (outputA == null) { Console.WriteLine("✗ 进程内处理失败"); return 1; }
        Console.WriteLine($"进程内完成，{outputA.Length} 样本，用时 {clock.Elapsed.TotalSeconds:0.0} 秒");

        // ---- 路径 B：跨进程 ----
        Console.WriteLine("\n-- 路径 B：跨进程（宿主）--");
        clock.Restart();
        var outputB = RunInHost(cv, rmvpe, voice, blockMs, block, blocks, input);
        clock.Stop();
        if (outputB == null) { Console.WriteLine("✗ 跨进程处理失败"); return 1; }
        Console.WriteLine($"跨进程完成，{outputB.Length} 样本，用时 {clock.Elapsed.TotalSeconds:0.0} 秒");

        // ---- 比对 ----
        Console.WriteLine("\n-- 比对 --");
        var n = Math.Min(outputA.Length, outputB.Length);
        if (n == 0) { Console.WriteLine("✗ 没有可比对的样本"); return 1; }

        double sumA = 0, sumB = 0, sumAB = 0, sumAA = 0, sumBB = 0, maxDiff = 0;
        for (var i = 0; i < n; i++)
        {
            var a = outputA[i];
            var b = outputB[i];
            sumA += a; sumB += b;
            sumAB += a * b; sumAA += a * a; sumBB += b * b;
            var d = Math.Abs(a - b);
            if (d > maxDiff) maxDiff = d;
        }

        var rmsA = Math.Sqrt(sumAA / n);
        var rmsB = Math.Sqrt(sumBB / n);
        var corr = sumAA > 0 && sumBB > 0
            ? (sumAB - sumA * sumB / n) / Math.Sqrt((sumAA - sumA * sumA / n) * (sumBB - sumB * sumB / n))
            : 0;

        Console.WriteLine($"长度     A={outputA.Length}  B={outputB.Length}");
        Console.WriteLine($"RMS      A={rmsA:0.0000}  B={rmsB:0.0000}   （相差 {Math.Abs(rmsA - rmsB) / Math.Max(rmsA, 1e-9) * 100:0.0}%）");
        Console.WriteLine($"相关系数 = {corr:0.0000}   （1.0000 = 完全一致）");
        Console.WriteLine($"最大单点差 = {maxDiff:0.0000}");

        var same = corr > 0.99 && Math.Abs(rmsA - rmsB) / Math.Max(rmsA, 1e-9) < 0.02;
        Console.WriteLine(same
            ? "✓ 两份输出基本一致 ⇒ 搬运路径没问题，问题出在主进程的环管理"
            : "✗ 两份输出明显不同 ⇒ 宿主里的 DSP 与进程内不等价，问题在宿主");
        return same ? 0 : 1;
    }

    /// <summary>路径 A：完全照搬 AiVoiceEffect 的处理链（48k → 40k → Process → 48k），但全在进程内。</summary>
    private static float[]? RunInProcess(string cv, string rmvpe, string voice, int blockMs, int block, int blocks, float[] input)
    {
        StreamingRvc? rvc = null;
        try
        {
            rvc = new StreamingRvc(cv, rmvpe, voice,
                blockMs: blockMs, contextMs: 2000, crossfadeMs: 40,
                semitones: 0, useGpu: false, index: null, indexRate: 0f);

            var toModel = new RateConverter(ChainRate, ModelRate, block + 64);
            var fromModel = new RateConverter(ModelRate, ChainRate, block * ModelRate / ChainRate + 64);

            var output = new List<float>(block * blocks);
            var chunk = new float[block];
            var modelBlock = block * ModelRate / ChainRate;

            for (var b = 0; b < blocks; b++)
            {
                Array.Copy(input, b * block, chunk, 0, block);
                var block40 = new float[modelBlock];
                var produced = toModel.Process(chunk, block40);
                if (produced < modelBlock) Array.Resize(ref block40, produced);

                var out40 = rvc.Process(block40);

                var out48 = new float[out40.Length * ChainRate / ModelRate + 64];
                var n = fromModel.Process(out40, out48);
                output.AddRange(out48.AsSpan(0, n).ToArray());

                Console.WriteLine($"  进程内块 {b + 1}：输入 {block} → 模型 {block40.Length} → 输出 {out40.Length} → 回48k {n}（累计 {output.Count}）");
            }

            return output.ToArray();
        }
        catch (Exception ex)
        {
            Console.WriteLine("  进程内异常：" + ex.Message);
            return null;
        }
        finally
        {
            try { rvc?.Dispose(); } catch { }
        }
    }

    /// <summary>路径 B：通过宿主进程处理同样的音频。</summary>
    private static float[]? RunInHost(string cv, string rmvpe, string voice, int blockMs, int block, int blocks, float[] input)
    {
        var parameters = new AiVoiceHostParams
        {
            Cv = cv,
            Rmvpe = rmvpe,
            Voice = voice,
            BlockMs = blockMs,
            ContextMs = 2000,
            CrossfadeMs = 40,
            Semitones = 0,
            IndexRate = 0f,
            UseGpu = false,
        };

        var client = AiVoiceHostClient.Start(parameters, ChainRate * 4, out var startError);
        if (client == null) { Console.WriteLine("  启动失败：" + startError); return null; }

        try
        {
            if (!client.WaitReady(180_000, out var readyError))
            {
                Console.WriteLine("  未就绪：" + readyError);
                return null;
            }

            var output = new List<float>(block * blocks);
            var buffer = new float[block * 4];
            var sent = 0;

            // ⚠ 必须先**全部喂完**再收输出。
            // 一开始写成"喂一块→等这块输出→再喂下一块"，结果死锁：
            // 宿主每块产出 7679 样本（`_fromModel` 重采样尾部差 1 个，正常现象），
            // 而测试在等 7680，于是永远等不到、也不再喂下一块，宿主就一直"等输入"。
            for (var b = 0; b < blocks; b++)
            {
                var written = 0;
                while (written < block)
                {
                    var w = client.WriteInput(input.AsSpan(b * block + written, block - written));
                    if (w <= 0) { Thread.Sleep(1); continue; }
                    written += w;
                }
                sent += written;
            }

            Console.WriteLine($"  已全部喂入 {sent} 样本，开始接收输出…");

            // 收输出：等到"至少 blocks-1 块"或者没动静了为止（每块少 1 个样本是正常的）
            var want = (long)block * blocks * 99 / 100;
            var deadline = Environment.TickCount64 + 120_000;
            var lastProgress = Environment.TickCount64;
            while (output.Count < want && Environment.TickCount64 < deadline)
            {
                var g = client.ReadOutput(buffer);
                if (g > 0)
                {
                    output.AddRange(buffer.AsSpan(0, g).ToArray());
                    lastProgress = Environment.TickCount64;
                    continue;
                }

                if (!client.IsAlive)
                {
                    Console.WriteLine($"  ✗ 宿主已退出（状态 {client.State}，错误「{client.LastError}」）");
                    break;
                }

                // 连续 5 秒没有任何输出 ⇒ 认为不会再有了
                if (Environment.TickCount64 - lastProgress > 5000)
                {
                    Console.WriteLine($"  输出停止：已有 {output.Count} 样本（目标 {want}）");
                    break;
                }

                Thread.Sleep(1);
            }

            Console.WriteLine($"  接收完成：{output.Count} 样本｜宿主推理 {client.HostInferMs:0}ms｜仍存活 {client.IsAlive}");

            return output.ToArray();
        }
        catch (Exception ex)
        {
            Console.WriteLine("  跨进程异常：" + ex.Message);
            return null;
        }
        finally
        {
            client.Stop();
            client.Dispose();
        }
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
