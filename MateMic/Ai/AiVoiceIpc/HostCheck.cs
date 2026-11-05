using System.Diagnostics;
using System.Text.Json;
using MateMic.Core;

namespace MateMic.Ai.AiVoiceIpc;

/// <summary>
/// 宿主进程的**端到端自检**（<c>MateMic.exe --hostcheck [--ai &lt;组件目录&gt;]</c>）。
///
/// 它真的启动一个子进程、真的加载 ONNX 模型、真的送一块音频进去再收回来，
/// 所以能一次验证四件事：
///   1. 子进程能起来并连上共享内存；
///   2. 参数通道能把模型路径（含中文/空格）正确下发；
///   3. 加载完成后能收到音频（**进程边界没有破坏数据通路**）；
///   4. 请求关闭后子进程**确实退出**（这是"能释放内存"的前提）。
///
/// 第 3 条只验证"通路"，不验证音质 —— 音质要人耳听。
/// </summary>
internal static class HostCheck
{
    private const int ChainRate = 48000;

    public static int Run(string[] args)
    {
        Console.WriteLine("==== AI 变声宿主进程端到端自检 ====");

        // ---- 定位组件 ----
        var aiRoot = GetArg(args, "--ai")
                     ?? Path.Combine(ConfigStore.ComponentsDirectory, "ai");

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

        var name = "MateMicHostCheck-" + Guid.NewGuid().ToString("N")[..8];
        Console.WriteLine($"共享内存前缀：{name}");

        AiVoiceControlBlock? ctl = null;
        AudioShmRing? inRing = null;
        Process? host = null;
        var failures = 0;

        try
        {
            // ---- 建共享内存（主控侧）----
            ctl = AiVoiceControlBlock.CreateMaster(name + "-ctl");
            inRing = AudioShmRing.CreateProducer(name + "-in", ChainRate * 2);

            // ---- 启动子进程 ----
            var exe = Environment.ProcessPath;
            if (exe == null) { Console.WriteLine("✗ 取不到自身路径"); return 1; }

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--aivoice-host");
            psi.ArgumentList.Add("--shm-name");
            psi.ArgumentList.Add(name);

            var clock = Stopwatch.StartNew();
            host = Process.Start(psi);
            if (host == null) { Console.WriteLine("✗ 子进程启动失败"); return 1; }
            Console.WriteLine($"子进程已启动，PID={host.Id}");

            // 异步把子进程输出转发到控制台（否则管道满了子进程会卡住）
            host.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine("  [宿主] " + e.Data); };
            host.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine("  [宿主!] " + e.Data); };
            host.BeginOutputReadLine();
            host.BeginErrorReadLine();

            // ---- 下发参数（indexRate=0 ⇒ 不加载索引，省一截加载时间）----
            var json = JsonSerializer.Serialize(new
            {
                cv,
                rmvpe,
                voice,
                index = (string?)null,
                blockMs = 160,
                contextMs = 320,
                crossfadeMs = 40,
                semitones = 0,
                indexRate = 0f,
                useGpu = false,   // 自检走 CPU，避免和用户正在用的 GPU 抢资源
            });
            var version = ctl.SetParams(json);
            Console.WriteLine($"已下发参数（版本 {version}，{json.Length} 字节）");

            // ---- 等就绪 ----
            var (ok, error) = ctl.WaitForReady(120_000);
            clock.Stop();
            if (!ok)
            {
                Console.WriteLine($"✗ 宿主未就绪：{error}");
                // 子进程是不是已经崩了？崩了的话原因要看它的退出码与输出
                Console.WriteLine($"  子进程 HasExited={host.HasExited}"
                                  + (host.HasExited ? $"｜退出码={host.ExitCode}" : "｜仍在运行（可能是卡在某处）"));
                failures++;
            }
            else
            {
                Console.WriteLine($"✓ 宿主就绪，加载耗时 {clock.ElapsedMilliseconds} ms（CPU 模式）");
            }

            if (failures == 0)
            {
                // ---- 输出环由宿主创建，所以在这里才打开 ----
                var outRing = AudioShmRing.OpenConsumer(name + "-out", 5000);
                if (outRing == null)
                {
                    Console.WriteLine("✗ 打不开输出环");
                    failures++;
                }
                else
                {
                    using (outRing)
                    {
                        // ---- 送 3 块 440 Hz 正弦 ----
                        const int blockMs = 160;
                        var block = blockMs * ChainRate / 1000;
                        var src = new float[block];
                        for (var i = 0; i < block; i++)
                            src[i] = MathF.Sin(i * 2 * MathF.PI * 220f / ChainRate) * 0.3f;

                        var sent = 0;
                        var sendClock = Stopwatch.StartNew();
                        for (var b = 0; b < 3; b++)
                        {
                            var written = 0;
                            while (written < src.Length)
                            {
                                var n = inRing.Write(src.AsSpan(written));
                                if (n <= 0) { inRing.WaitForRoom(20); continue; }
                                written += n;
                            }
                            sent += written;
                        }

                        Console.WriteLine($"已送入 {sent} 样本（{sent / 48.0:0} ms 音频，3 块）");

                        // ---- 等输出（每块要推理一次，CPU 上约 blockMs × RTF）----
                        var received = 0;
                        var buffer = new float[8192];
                        var deadline = Environment.TickCount64 + 60_000;
                        while (received < block * 2 && Environment.TickCount64 < deadline)
                        {
                            var n = outRing.Read(buffer);
                            if (n > 0) { received += n; continue; }
                            if (host.HasExited) break;
                            Thread.Sleep(5);
                        }
                        sendClock.Stop();

                        var receiveOk = received >= block * 2;
                        Console.WriteLine($"{(receiveOk ? "✓" : "✗")} 收回 {received} 样本（{received / 48.0:0} ms 音频）"
                                          + $"｜等输出 {sendClock.ElapsedMilliseconds} ms");
                        if (!receiveOk) failures++;
                    }
                }
            }

            // ---- 关闭并确认子进程退出（这是"能释放内存"的前提）----
            var stopClock = Stopwatch.StartNew();
            ctl.RequestClose();
            var exited = host.WaitForExit(10_000);
            stopClock.Stop();

            Console.WriteLine($"{(exited ? "✓" : "✗")} 请求关闭后子进程退出={exited}（{stopClock.ElapsedMilliseconds} ms）");
            if (!exited)
            {
                failures++;
                try { host.Kill(entireProcessTree: true); Console.WriteLine("  已强制结束"); } catch { }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("✗ 自检抛异常：" + ex);
            failures++;
        }
        finally
        {
            try { if (host is { HasExited: false }) host.Kill(entireProcessTree: true); } catch { }
            host?.Dispose();
            inRing?.Dispose();
            ctl?.Dispose();
            // 清理命名事件（进程退出后句柄自然释放；这里只是为了不打乱下次自检）
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
