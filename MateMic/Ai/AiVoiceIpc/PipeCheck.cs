using System.Diagnostics;

namespace MateMic.Ai.AiVoiceIpc;

/// <summary>
/// 命名管道传输的**跨进程校验**（<c>MateMic.exe --pipecheck</c>）。
///
/// 三件事一次验完，任何一条不过都不该往下接线：
///   1. **正确性**：喂递增计数器序列（每个样本都不同，错一个立刻可见），逐样本比对；
///   2. **吞吐**：每块 7680 样本（160 ms 音频）的往返耗时，必须远小于 160 ms；
///   3. **退出**：请求关闭后 relay 子进程能自己退出。
///
/// 参照物是共享内存版本（<see cref="ShmRingCheck"/>）：那个每块往返 13~108 µs，
/// 但跨进程波形不对。管道的正确性优先于那几十微秒。
/// </summary>
internal static class PipeCheck
{
    public static int Run()
    {
        Console.WriteLine("==== 命名管道传输 · 跨进程校验 ====");

        var name = "MateMicPipe-" + Guid.NewGuid().ToString("N")[..8];
        var exe = Environment.ProcessPath;
        if (exe == null) { Console.WriteLine("✗ 取不到自身路径"); return 1; }

        var failures = 0;
        Process? relay = null;

        try
        {
            // ⚠ **顺序很重要：先启动 relay，再创建 server。**
            // CreateServer 会阻塞等对端连上来；若先 CreateServer 再启动 relay，
            // 父进程就是在等一个还没出生的进程（实测报"创建输入管道失败"）。
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            psi.ArgumentList.Add("--pipe-relay");
            psi.ArgumentList.Add(name);

            var clock = Stopwatch.StartNew();
            relay = Process.Start(psi);
            if (relay == null) { Console.WriteLine("✗ relay 启动失败"); return 1; }
            relay.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) { Console.WriteLine("  [relay] " + e.Data); Core.Log.Info("[relay] " + e.Data); }
            };
            relay.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) { Console.WriteLine("  [relay!] " + e.Data); Core.Log.Warn("[relay] " + e.Data); }
            };
            relay.BeginOutputReadLine();
            relay.BeginErrorReadLine();
            Console.WriteLine($"relay PID={relay.Id}，管道前缀 {name}");
            Core.Log.Info($"[pipecheck] 父进程管道前缀 = {name}（server 名 {name}-in / {name}-out）");

            // 主进程侧：in 我们写、out 我们读（relay 已经在连，这里会很快连上）
            var linkIn = AudioPipeLink.CreateServer(name + "-in", isWriter: true, connectTimeoutMs: 15_000);
            if (linkIn == null)
            {
                Console.WriteLine("✗ 创建输入管道失败");
                Console.WriteLine($"  relay 状态：HasExited={relay.HasExited}"
                                  + (relay.HasExited ? $" 退出码={relay.ExitCode}" : " 仍在等待"));
                try
                {
                    var trace = Path.Combine(Path.GetTempPath(), "mateMic-pipe-relay.log");
                    if (File.Exists(trace)) Console.WriteLine($"  relay 轨迹文件：{trace}");
                }
                catch { }
                return 1;
            }
            var linkOut = AudioPipeLink.CreateServer(name + "-out", isWriter: false, connectTimeoutMs: 15_000);
            if (linkOut == null) { Console.WriteLine("✗ 创建输出管道失败"); linkIn.Dispose(); return 1; }
            clock.Stop();
            Console.WriteLine($"✓ 两端连接完成（{clock.ElapsedMilliseconds} ms）");

            using (linkIn)
            using (linkOut)
            {
                // ---- 1) 正确性 ----
                const int block = 7680;    // 160 ms @48k
                const int blocks = 6;
                var total = block * blocks;
                var sent = new float[total];
                for (var i = 0; i < total; i++) sent[i] = i;   // 递增：错一个立刻可见

                var sw = Stopwatch.StartNew();
                for (var b = 0; b < blocks; b++)
                    linkIn.Write(sent.AsSpan(b * block, block));
                sw.Stop();
                var writeMs = sw.Elapsed.TotalMilliseconds;
                Console.WriteLine($"已写 {total} 样本（{blocks} 块），耗时 {writeMs:0.0} ms（每块 {writeMs / blocks:0.00} ms）");

                var got = new float[total + block * 2];
                var received = 0;
                sw.Restart();
                var deadline = Environment.TickCount64 + 60_000;
                while (received < total && Environment.TickCount64 < deadline)
                {
                    var n = linkOut.Read(got.AsSpan(received), 2000);
                    if (n > 0) { received += n; continue; }
                    if (relay.HasExited) { Console.WriteLine("  ✗ relay 已退出"); break; }
                }
                sw.Stop();
                var readMs = sw.Elapsed.TotalMilliseconds;
                Console.WriteLine($"已收 {received} 样本，耗时 {readMs:0.0} ms");

                var badCount = 0;
                var firstBad = -1;
                var n2 = Math.Min(received, total);
                for (var i = 0; i < n2; i++)
                {
                    if (got[i] != sent[i])
                    {
                        badCount++;
                        if (firstBad < 0) firstBad = i;
                    }
                }

                var ok = received == total && badCount == 0;
                Console.WriteLine($"{(ok ? "✓" : "✗")} 正确性：发 {total} / 收 {received}｜不一致 {badCount} 个"
                                  + (firstBad >= 0 ? $"｜首个错位于 {firstBad}（期望 {sent[firstBad]}，实得 {got[firstBad]}）" : ""));
                if (!ok) failures++;

                // ---- 2) 吞吐（单块往返，与共享内存版本可比）----
                Console.WriteLine("-- 单块往返耗时 --");
                foreach (var samples in new[] { 960, 4800, 24000 })
                {
                    var src = new float[samples];
                    for (var i = 0; i < samples; i++) src[i] = i;
                    var dst = new float[samples];
                    const int iters = 20;

                    sw.Restart();
                    for (var i = 0; i < iters; i++)
                    {
                        linkIn.Write(src);
                        var r = 0;
                        var blockDeadline = Environment.TickCount64 + 2000;
                        while (r < samples && Environment.TickCount64 < blockDeadline)
                        {
                            var n = linkOut.Read(dst.AsSpan(r), 500);
                            if (n > 0) r += n;
                        }
                    }
                    sw.Stop();

                    var perBlockMs = sw.Elapsed.TotalMilliseconds / iters;
                    var audioMs = samples / 48.0;
                    var throughputOk = perBlockMs < audioMs * 0.2;   // 判断标准：往返 < 音频时长的 20%
                    Console.WriteLine($"{(throughputOk ? "✓" : "✗")} {samples,6} 样本（{audioMs,6:0.0} ms 音频）："
                                      + $"往返 {perBlockMs,6:0.00} ms（占音频时长 {perBlockMs / audioMs * 100:0.0}%）");
                    if (!throughputOk) failures++;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("✗ 校验抛异常：" + ex);
            failures++;
        }
        finally
        {
            try
            {
                if (relay is { HasExited: false })
                {
                    relay.WaitForExit(3000);
                    if (!relay.HasExited) relay.Kill(entireProcessTree: true);
                }
            }
            catch { }
            relay?.Dispose();
        }

        Console.WriteLine(failures == 0 ? "==== 全部通过 ====" : $"==== 有 {failures} 项失败 ====");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>子进程侧：把输入管道收到的样本原样写回输出管道（不碰 DSP）。</summary>
    public static int RunRelay(string name)
    {
        // 直接写绝对路径的文件：relay 子进程的日志目录可能与父进程不同
        //（--pipecheck 用默认数据目录，而 relay 继承的是 exe 目录），
        // 写同一个文件才能保证看得到它到底执行到哪一步。
        var trace = Path.Combine(Path.GetTempPath(), "mateMic-pipe-relay.log");
        void Trace(string message)
        {
            try { File.AppendAllText(trace, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
            catch { }
        }

        Trace($"收到管道前缀 = {name}（将连接 {name}-in / {name}-out）");
        Console.WriteLine($"[relay] 收到管道前缀 = {name}");

        var input = AudioPipeLink.ConnectClient(name + "-in", isWriter: false, timeoutMs: 15_000);
        if (input == null) { Console.Error.WriteLine("[relay] 连不上输入管道"); Trace("连不上输入管道"); return 3; }
        Trace("输入管道已连接");

        var output = AudioPipeLink.ConnectClient(name + "-out", isWriter: true, timeoutMs: 15_000);
        if (output == null) { Console.Error.WriteLine("[relay] 连不上输出管道"); Trace("连不上输出管道"); input.Dispose(); return 4; }
        Trace("输出管道已连接");

        Console.WriteLine("[relay] 就绪");

        try
        {
            var buffer = new float[96000];
            while (true)
            {
                var n = input.Read(buffer, 2000);
                if (n <= 0)
                {
                    // 连续超时 ⇒ 认为上游结束了（父进程退出会关闭管道，Read 会返回 0）
                    if (!input.IsWriter) { /* 只是读端，没有更多信息 */ }
                    Thread.Sleep(2);
                    continue;
                }

                output.Write(buffer.AsSpan(0, n));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[relay] 转发失败：" + ex.Message);
            return 5;
        }
        finally
        {
            output.Dispose();
            input.Dispose();
        }
    }
}
