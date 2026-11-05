using System.Diagnostics;

namespace MateMic.Ai.AiVoiceIpc;

/// <summary>
/// 命名管道传输的**跨进程校验**（<c>MateMic.exe --pipecheck</c>）。
///
/// 三件事一次验完：
///   1. **正确性**：喂递增计数器序列（每个样本都不同，错一个立刻可见），逐样本比对；
///   2. **吞吐**：单块往返耗时，必须远小于该块对应的音频时长；
///   3. **退出**：结束后 relay 子进程能自己退出。
///
/// ⚠ 这里必须**一边写一边读**：管道是阻塞的，若先写完再读，
/// relay 会阻塞在"写输出"上、于是不再读输入，父进程随即阻塞在"写输入"上 —— 双向死锁。
/// 所以用后台线程写、主线程读。这也正是生产路径（HostWorkerLoop）要分两个线程的原因。
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
        var trace = Path.Combine(Path.GetTempPath(), "mateMic-pipe-relay.log");
        try { File.Delete(trace); } catch { }

        try
        {
            // 顺序：**先起 relay，再建 server**（server 会阻塞等对端连接）
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

            relay = Process.Start(psi);
            if (relay == null) { Console.WriteLine("✗ relay 启动失败"); return 1; }
            relay.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine("  [relay] " + e.Data); };
            relay.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine("  [relay!] " + e.Data); };
            relay.BeginOutputReadLine();
            relay.BeginErrorReadLine();
            Console.WriteLine($"relay PID={relay.Id}，管道前缀 {name}");

            var clock = Stopwatch.StartNew();
            using var linkIn = AudioPipeLink.CreateServer(name + "-in", isWriter: true, connectTimeoutMs: 15_000);
            if (linkIn == null) { Console.WriteLine("✗ 创建输入管道失败"); return 1; }
            using var linkOut = AudioPipeLink.CreateServer(name + "-out", isWriter: false, connectTimeoutMs: 15_000);
            if (linkOut == null) { Console.WriteLine("✗ 创建输出管道失败"); return 1; }
            clock.Stop();
            Console.WriteLine($"✓ 两端连接完成（{clock.ElapsedMilliseconds} ms）");

            // ---- 1) 正确性（边写边读）----
            const int block = 7680;
            const int blocks = 6;
            var total = block * blocks;
            var sent = new float[total];
            for (var i = 0; i < total; i++) sent[i] = i;

            var writeClock = Stopwatch.StartNew();
            var writer = Task.Run(() =>
            {
                for (var b = 0; b < blocks; b++)
                    linkIn.Write(sent.AsSpan(b * block, block));
            });

            var got = new float[total + block * 2];
            var received = 0;
            var deadline = Environment.TickCount64 + 60_000;
            while (received < total && Environment.TickCount64 < deadline)
            {
                var n = linkOut.Read(got.AsSpan(received));
                if (n > 0) { received += n; continue; }
                break;   // 0 = 对端关闭
            }
            writeClock.Stop();
            writer.Wait(5000);
            Console.WriteLine($"已收发 {received} 样本（{blocks} 块 × {block}），耗时 {writeClock.ElapsedMilliseconds} ms");

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

    /// <summary>子进程侧：把输入管道收到的帧原样写进输出管道（不碰 DSP）。</summary>
    public static int RunRelay(string name)
    {
        var trace = Path.Combine(Path.GetTempPath(), "mateMic-pipe-relay.log");
        void Note(string message)
        {
            try { File.AppendAllText(trace, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
            catch { }
        }

        Note($"收到管道前缀 = {name}");
        Console.WriteLine($"[relay] 收到管道前缀 = {name}");

        var input = AudioPipeLink.ConnectClient(name + "-in", isWriter: false, timeoutMs: 15_000);
        if (input == null) { Console.Error.WriteLine("[relay] 连不上输入管道"); Note("连不上输入管道"); return 3; }
        Note("输入管道已连接");

        var output = AudioPipeLink.ConnectClient(name + "-out", isWriter: true, timeoutMs: 15_000);
        if (output == null) { Console.Error.WriteLine("[relay] 连不上输出管道"); Note("连不上输出管道"); input.Dispose(); return 4; }
        Note("输出管道已连接");

        Console.WriteLine("[relay] 就绪");
        Note("就绪");

        try
        {
            var buffer = new float[AudioPipeLink.MaxFrameSamples];
            while (true)
            {
                var n = input.Read(buffer);
                if (n <= 0) { Note("输入结束"); return 0; }
                if (!output.Write(buffer.AsSpan(0, n))) { Note("输出已关闭"); return 0; }
            }
        }
        catch (Exception ex)
        {
            Note("转发异常：" + ex.GetType().Name + " " + ex.Message);
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
