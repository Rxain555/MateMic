using System.Diagnostics;

namespace MateMic.Ai.AiVoiceIpc;

/// <summary>
/// 共享内存环的自检（<c>MateMic.exe --shmcheck</c>）。
///
/// 验证三件事，任何一条不过都说明"独立进程"这条路走不通：
///   1. **数据完整性** —— 写进去的样本要一个不差地读回来（含跨回绕点的情况）；
///   2. **回绕正确** —— 写超过容量后，环仍按先进先出给出正确内容；
///   3. **吞吐开销** —— 每块 960~24000 样本的读写耗时必须在微秒级，
///      否则"共享内存不增加延迟"这个前提就不成立。
///
/// 单进程内就能验（同一进程里 CreateProducer + OpenConsumer 同一名字），
/// 跨进程的真实通路在阶段 B 里再验。
/// </summary>
public static class ShmRingCheck
{
    private const string RingName = "MateMicShmCheckRing";
    private const string ControlName = "MateMicShmCheckCtl";
    private const int Capacity = 48000;   // 1 秒 @ 48 kHz

    public static void Run()
    {
        Console.WriteLine("==== 共享内存环自检 ====");

        var failures = 0;
        AudioShmRing? producer = null;
        AudioShmRing? consumer = null;

        try
        {
            producer = AudioShmRing.CreateProducer(RingName, Capacity);
            consumer = AudioShmRing.OpenConsumer(RingName, 2000);

            if (consumer == null)
            {
                Console.WriteLine("✗ 消费者打不开映射（OpenConsumer 返回 null）");
                failures++;
                return;
            }

            Console.WriteLine($"环容量 = {producer.CapacitySamples} 样本（{producer.CapacitySamples / 48.0:0} ms @48k）");

            // ---- 1) 基本完整性 ----
            var payload = MakeSamples(960, seed: 1);
            var wrote = producer.Write(payload);
            var got = new float[960];
            var read = consumer.Read(got);

            var ok1 = wrote == 960 && read == 960 && Same(payload, got);
            Console.WriteLine($"{(ok1 ? "✓" : "✗")} 基本读写：写 {wrote} / 读 {read}｜内容一致={read == 960 && Same(payload, got)}");
            if (!ok1) failures++;

            // ---- 2) 回绕（连续写 3 倍容量，逐块读写比对）----
            var wrapFail = 0;
            var block = 480;
            for (var round = 0; round < 3 * Capacity / block; round++)
            {
                var chunk = MakeSamples(block, seed: round);
                var w = producer.Write(chunk);
                var rbuf = new float[block];
                var r = consumer.Read(rbuf);
                if (w != block || r != block || !Same(chunk, rbuf)) wrapFail++;
            }

            Console.WriteLine($"{(wrapFail == 0 ? "✓" : "✗")} 回绕：写满 {3 * Capacity} 样本（{3 * Capacity / block} 块），不一致 {wrapFail} 块");
            if (wrapFail != 0) failures++;

            // ---- 3) 吞吐 ----
            Console.WriteLine("-- 吞吐（每块写入+读出的总耗时）--");
            foreach (var samples in new[] { 960, 4800, 24000 })
            {
                // 先清空
                var sink = new float[Capacity];
                while (consumer.Read(sink) > 0) { }

                var src = MakeSamples(samples, seed: 7);
                var dst = new float[samples];
                const int iters = 200;

                var sw = Stopwatch.StartNew();
                for (var i = 0; i < iters; i++)
                {
                    producer.Write(src);
                    consumer.Read(dst);
                }

                sw.Stop();
                var perBlockUs = sw.Elapsed.TotalMilliseconds * 1000.0 / iters;
                var perBlockMs = perBlockUs / 1000.0;
                var audioMs = samples / 48.0;
                var ok = perBlockMs < 1.0;   // 判断标准：单块往返 < 1 ms
                Console.WriteLine($"{(ok ? "✓" : "✗")} {samples,6} 样本（{audioMs,6:0.0} ms 音频）："
                                  + $"往返 {perBlockUs,7:0.0} µs（占音频时长 {perBlockMs / audioMs * 100:0.00}%）");
                if (!ok) failures++;
            }

            // ---- 4) 控制块（参数 + 状态）----
            using var master = AiVoiceControlBlock.CreateMaster(ControlName);
            using var host = AiVoiceControlBlock.OpenHost(ControlName, 2000);

            if (host == null)
            {
                Console.WriteLine("✗ 控制块：宿主侧打不开");
                failures++;
            }
            else
            {
                // 参数：带中文和空格的路径最能暴露编码问题
                var json = "{\"blockMs\":160,\"contextMs\":320,\"crossfadeMs\":40,"
                           + "\"pitch\":0,\"indexRate\":0.5,"
                           + "\"voice\":\"D:\\\\音色 模型\\\\测试.onnx\",\"index\":null}";
                var setVersion = master.SetParams(json);
                var (getVersion, gotJson) = host.GetParams();
                var paramOk = getVersion == setVersion && gotJson == json;
                Console.WriteLine($"{(paramOk ? "✓" : "✗")} 控制块参数：版本 {setVersion}→{getVersion}｜"
                                  + $"JSON 一致={gotJson == json}｜长度 {json.Length}");
                if (!paramOk) failures++;

                // 状态：宿主写、主控读
                host.SetState(AiVoiceHostState.Ready, 1.0);
                var (state, progress, error) = master.GetState();
                var stateOk = state == AiVoiceHostState.Ready && Math.Abs(progress - 1.0) < 1e-9 && error.Length == 0;
                Console.WriteLine($"{(stateOk ? "✓" : "✗")} 控制块状态：{state}｜进度 {progress:0.00}｜错误「{error}」");
                if (!stateOk) failures++;

                // 失败状态要能带回错误文本（宿主加载失败时主进程要靠它显示原因）
                host.SetState(AiVoiceHostState.Failed, 0, "音色模型文件不存在");
                var (failedState, _, failedError) = master.GetState();
                var failOk = failedState == AiVoiceHostState.Failed && failedError == "音色模型文件不存在";
                Console.WriteLine($"{(failOk ? "✓" : "✗")} 控制块错误传递：「{failedError}」");
                if (!failOk) failures++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("✗ 自检抛异常：" + ex);
            failures++;
        }
        finally
        {
            consumer?.Dispose();
            producer?.Dispose();
        }

        Console.WriteLine(failures == 0 ? "==== 全部通过 ====" : $"==== 有 {failures} 项失败 ====");
    }

    private static float[] MakeSamples(int count, int seed)
    {
        var buffer = new float[count];
        for (var i = 0; i < count; i++)
            buffer[i] = MathF.Sin((i + seed * 37) * 0.013f) * 0.7f;
        return buffer;
    }

    private static bool Same(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;   // 位级相等，不需要容差（同一份 float 原样搬运）
        return true;
    }
}
