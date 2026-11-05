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
        const int blocks = 6;                        // 6 块，便于看差异分段分布
        var block = blockMs * ChainRate / 1000;      // 7680

        // 输入：220 Hz 正弦（低频，块边界错位会立刻变成刺耳的谐波噪声）
        var input = new float[block * blocks];
        for (var i = 0; i < input.Length; i++)
            input[i] = MathF.Sin(i * 2 * MathF.PI * 220f / ChainRate) * 0.35f;

        Console.WriteLine($"工程：块 {blockMs}ms × {blocks} 块 = {input.Length / (double)ChainRate:0.00} 秒，音色 {Path.GetFileName(voice)}");

        // ---- 路径 A：进程内（第 1 次）----
        Console.WriteLine("\n-- 路径 A1：进程内（第 1 次）--");
        var clock = Stopwatch.StartNew();
        var outputA = RunInProcess(cv, rmvpe, voice, blockMs, block, blocks, input);
        clock.Stop();
        if (outputA == null) { Console.WriteLine("✗ 进程内处理失败"); return 1; }
        Console.WriteLine($"进程内完成，{outputA.Length} 样本，用时 {clock.Elapsed.TotalSeconds:0.0} 秒");

        // ---- 路径 A2：进程内（第 2 次，**对照组**）----
        //
        // 这一组是整个排查的关键：如果"进程内跑两次"自己也差 1~10%，
        // 说明 RVC 推理本身就不确定性（多线程浮点归约顺序），
        // 那么"跨进程输出不同"根本不是问题 —— 该换个方向查音色。
        // 如果两次完全一致，才说明跨进程引入了真实差异。
        Console.WriteLine("\n-- 路径 A2：进程内（第 2 次，对照组）--");
        clock.Restart();
        var outputA2 = RunInProcess(cv, rmvpe, voice, blockMs, block, blocks, input);
        clock.Stop();
        if (outputA2 != null)
        {
            Console.WriteLine($"进程内二次完成，{outputA2.Length} 样本，用时 {clock.Elapsed.TotalSeconds:0.0} 秒");
            Report("A1 vs A2（进程内 vs 进程内）", outputA, outputA2);
        }
        else
        {
            Console.WriteLine("✗ 进程内二次处理失败");
        }

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

        // ---- 差异分布：按 1/10 分段 ----
        // 用来区分两种成因：
        //   · 差异集中在首尾某几段 ⇒ 块边界问题（SOLA / 交叉淡化 / 预填充）
        //   · 差异均匀分布在全段   ⇒ 会话级差异（浮点/线程/优化级别），与边界无关
        Console.WriteLine("\n-- 差异分布（每段 1/10）--");
        var segLen = Math.Max(1, n / 10);
        for (var s = 0; s < 10; s++)
        {
            var lo = s * segLen;
            var hi = Math.Min(n, lo + segLen);
            if (lo >= hi) break;

            double sumA2 = 0, sumB2 = 0, maxDiff2 = 0;
            var diffs = 0;
            for (var i = lo; i < hi; i++)
            {
                sumA2 += (double)outputA[i] * outputA[i];
                sumB2 += (double)outputB[i] * outputB[i];
                var d = Math.Abs(outputA[i] - outputB[i]);
                if (d > maxDiff2) maxDiff2 = d;
                if (d > 0.01) diffs++;
            }

            var cnt = hi - lo;
            var segRmsA = Math.Sqrt(sumA2 / cnt);
            var segRmsB = Math.Sqrt(sumB2 / cnt);
            var pct = segRmsA > 1e-9 ? Math.Abs(segRmsB - segRmsA) / segRmsA * 100 : 0;
            Console.WriteLine($"  段 {s + 1,2}（{lo,6}~{hi,6}）：RMS {segRmsA:0.0000} vs {segRmsB:0.0000}"
                              + $"（差 {pct,5:0.0}%）｜最大差 {maxDiff2:0.0000}｜差异样本 {diffs,6}/{cnt}");
        }

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

        var client = AiVoiceHostClient.Start(parameters, out var startError);
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
            // ⚠ 必须**边写边读**：管道是全同步阻塞的，若先写完 6 块再读，
            // 管道缓冲（64 KB）装不下 180 KB，写线程会阻塞；而宿主那边写输出也阻塞，
            // 于是双方互等 —— 死锁。所以用后台线程写、主线程读。
            var writer = Task.Run(() =>
            {
                for (var b = 0; b < blocks; b++)
                {
                    if (!client.WriteInput(input.AsSpan(b * block, block)))
                    {
                        Console.WriteLine($"  ✗ 写第 {b + 1} 块失败（对端已关闭）");
                        break;
                    }
                }
            });

            Console.WriteLine($"  已启动写入线程（{blocks} 块 × {block}），开始接收输出…");

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

    /// <summary>把两组输出做定量对比并打印（长度 / RMS / 相关系数 / 最大单点差）。</summary>
    private static void Report(string title, float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        if (n == 0) { Console.WriteLine($"  {title}：没有可比对样本"); return; }

        double sumA = 0, sumB = 0, sumAB = 0, sumAA = 0, sumBB = 0, maxDiff = 0;
        for (var i = 0; i < n; i++)
        {
            double x = a[i], y = b[i];
            sumA += x; sumB += y;
            sumAB += x * y; sumAA += x * x; sumBB += y * y;
            var d = Math.Abs(x - y);
            if (d > maxDiff) maxDiff = d;
        }

        var rmsA = Math.Sqrt(sumAA / n);
        var rmsB = Math.Sqrt(sumBB / n);
        var corr = sumAA > 0 && sumBB > 0
            ? (sumAB - sumA * sumB / n) / Math.Sqrt((sumAA - sumA * sumA / n) * (sumBB - sumB * sumB / n))
            : 0;

        Console.WriteLine($"  {title}");
        Console.WriteLine($"    长度 {a.Length} / {b.Length}｜RMS {rmsA:0.0000} / {rmsB:0.0000}"
                          + $"（差 {Math.Abs(rmsB - rmsA) / Math.Max(rmsA, 1e-9) * 100:0.0}%）");
        Console.WriteLine($"    相关系数 {corr:0.0000}｜最大单点差 {maxDiff:0.0000}");
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
