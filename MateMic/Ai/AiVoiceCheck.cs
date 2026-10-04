using System.Diagnostics;
using MateMic.Core;
using NAudio.Wave;

namespace MateMic.Ai;

/// <summary>
/// AI 变声流水线自检：MateMic.exe --aicheck &lt;引擎组件目录&gt; [--cpu] [--wav &lt;音频文件&gt;] [--out &lt;输出wav&gt;]
///
/// 用途（不依赖界面与麦克风）：
///   · 验证三个模型能正确串起来、形状对得上、真的产出了音频；
///   · 量每一级的耗时与整体实时率——**这是判断能不能实时的硬指标**；
///   · 可指定真实录音作为输入，便于对比听感。
/// </summary>
public static class AiVoiceCheck
{
    public static int Run(string[] args)
    {
        var directory = Value(args, "--aicheck");
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            Console.WriteLine("[AI 自检] 用法：--aicheck <引擎组件目录> [--cpu] [--wav <音频文件>] [--out <输出wav>]");
            return 1;
        }

        var useGpu = !args.Contains("--cpu", StringComparer.OrdinalIgnoreCase);
        var wavPath = Value(args, "--wav");
        var outPath = Value(args, "--out") ?? Path.Combine(Path.GetTempPath(), "matemic-aicheck-out.wav");

        Console.WriteLine($"[AI 自检] 引擎目录：{directory}");
        Console.WriteLine($"[AI 自检] 推理后端：{(useGpu ? "DirectML（不可用则自动退回 CPU）" : "CPU")}");

        var loadWatch = Stopwatch.StartNew();
        var engine = AiVoiceEngine.TryLoad(directory, useGpu, out var message);
        loadWatch.Stop();
        if (engine == null)
        {
            Console.WriteLine($"[AI 自检] 引擎加载失败：{message}");
            Log.Error($"[AI 自检] 引擎加载失败：{message}");
            return 1;
        }

        Console.WriteLine($"[AI 自检] 引擎 {engine.Name} 加载完成：{loadWatch.ElapsedMilliseconds} ms，"
                          + $"音色模型 {engine.ModelSampleRate} Hz");

        // 准备输入（48 kHz 单声道）
        float[] input;
        if (!string.IsNullOrWhiteSpace(wavPath) && File.Exists(wavPath))
        {
            input = LoadAudio48k(wavPath);
            Console.WriteLine($"[AI 自检] 读入 {Path.GetFileName(wavPath)}：{input.Length / 48000.0:0.00} 秒");
        }
        else
        {
            var syntheticSeconds = 3.0;
            if (double.TryParse(Value(args, "--seconds"), out var parsed) && parsed > 0.5) syntheticSeconds = parsed;
            input = Synthesize(syntheticSeconds);
            Console.WriteLine("[AI 自检] 未指定 --wav，使用 3 秒合成信号");
        }

        // --stream：走真正的流式音效（含后台推理线程与环形缓冲），按音频回调的块长喂数据。
        // 这是"能不能在应用里出声"的最接近验证：能测出启动静音长度（≈延迟）与输出连续性。
        if (args.Contains("--stream", StringComparer.OrdinalIgnoreCase))
        {
            return RunStream(engine, input, outPath);
        }
        const int chunk = 48000 * 30 / 100;     // 0.30 秒一块，与实时链路一致
        var output = new List<float>(input.Length + chunk);
        var engineWatch = new Stopwatch();
        var chunks = 0;

        for (var offset = 0; offset + chunk <= input.Length; offset += chunk)
        {
            engineWatch.Start();
            var produced = engine.Convert(input.AsSpan(offset, chunk), 0f, out var count);
            engineWatch.Stop();
            for (var i = 0; i < count; i++) output.Add(produced[i]);
            chunks++;
        }

        engineWatch.Stop();
        if (chunks == 0)
        {
            Console.WriteLine("[AI 自检] 输入太短");
            return 1;
        }

        var seconds = output.Count / 48000.0;
        var audioSeconds = chunks * 0.30;
        var realtimeFactor = engineWatch.Elapsed.TotalSeconds / audioSeconds;

        float peak = 0, sum = 0;
        foreach (var sample in output)
        {
            var value = Math.Abs(sample);
            if (value > peak) peak = value;
            sum += sample * sample;
        }

        var rms = Math.Sqrt(sum / Math.Max(1, output.Count));
        Console.WriteLine($"[AI 自检] 处理 {chunks} 块（{audioSeconds:0.00} 秒音频），用时 {engineWatch.ElapsedMilliseconds} ms"
                          + $" → 实时率 {realtimeFactor * 100:0.0}%（>100% 表示跑不动实时）");
        Console.WriteLine($"[AI 自检] 分级耗时（每块均值）：编码器 {engine.LastEncoderMs:0.0} ms，"
                          + $"基频 {engine.LastF0Ms:0.0} ms，音色模型 {engine.LastSynthMs:0.0} ms");
        Console.WriteLine($"[AI 自检] 输出 {seconds:0.00} 秒，峰值 {20 * Math.Log10(peak + 1e-9):0.0} dBFS，"
                          + $"有效值 {20 * Math.Log10(rms + 1e-9):0.0} dBFS");

        Log.Info($"[AI 自检] 实时率 {realtimeFactor * 100:0.0}%，编码器 {engine.LastEncoderMs:0.0} ms / "
                 + $"基频 {engine.LastF0Ms:0.0} ms / 音色 {engine.LastSynthMs:0.0} ms，峰值 {peak:0.000}，有效值 {rms:0.000}");

        try
        {
            using var writer = new WaveFileWriter(outPath, new WaveFormat(48000, 16, 1));
            foreach (var sample in output)
            {
                var clamped = Math.Clamp(sample, -1f, 1f);
                var value = (short)(clamped * 32767);
                writer.WriteByte((byte)(value & 0xFF));
                writer.WriteByte((byte)((value >> 8) & 0xFF));
            }

            Console.WriteLine($"[AI 自检] 输出已写入：{outPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI 自检] 写 wav 失败：{ex.Message}");
        }

        engine.Dispose();
        return peak > 1e-4 ? 0 : 2;
    }

    /// <summary>把输入按音频回调的粒度喂给流式音效，检查启动延迟与输出连续性。</summary>
    private static int RunStream(AiVoiceEngine engine, float[] input, string outPath)
    {
        const int callback = 480;      // 10 ms @48k，与音频引擎的回调块长同量级
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        using var effect = new AiVoiceChangerEffect(format) { Enabled = true };
        effect.LoadEngine(engine);     // 所有权交给音效，由它释放

        var output = new List<float>(input.Length);
        var buffer = new float[callback];
        var firstSound = -1;
        var watch = Stopwatch.StartNew();

        // 关键：按实时节奏喂（每 10 ms 一块）。瞬间灌进去的话，推理线程还在处理第一块，
        // 测试就已经统计完了，会误判成"全程静音"。
        for (var offset = 0; offset + callback <= input.Length; offset += callback)
        {
            Array.Copy(input, offset, buffer, 0, callback);
            effect.Read(buffer);
            foreach (var sample in buffer)
            {
                if (firstSound < 0 && Math.Abs(sample) > 1e-3) firstSound = output.Count;
                output.Add(sample);
            }

            var target = (offset + callback) * 1000L / 48000;
            var elapsed = watch.ElapsedMilliseconds;
            if (target > elapsed) Thread.Sleep((int)Math.Min(20, target - elapsed));
        }

        // 再等一会儿让推理线程把缓冲里的结果吐完
        for (var i = 0; i < 30; i++)
        {
            Thread.Sleep(50);
            var before = effect.PushedSamples;
            Thread.Sleep(50);
            if (effect.PushedSamples == before) break;
        }

        var tailStart = output.Count / 2;
        float peak = 0, sum = 0;
        var silent = 0;
        for (var i = tailStart; i < output.Count; i++)
        {
            var value = Math.Abs(output[i]);
            if (value > peak) peak = value;
            sum += output[i] * output[i];
            if (value < 1e-4) silent++;
        }

        var rms = Math.Sqrt(sum / Math.Max(1, output.Count - tailStart));
        var silentRatio = silent * 100.0 / Math.Max(1, output.Count - tailStart);

        Console.WriteLine($"[AI 自检] 流式模式：喂入 {input.Length / 48000.0:0.00} 秒，输出 {output.Count / 48000.0:0.00} 秒");
        Console.WriteLine($"[AI 自检] 处理块数 {effect.WorkerBlocks}，写入样本 {effect.PushedSamples}，"
                          + $"丢弃样本 {effect.DroppedSamples}");
        Console.WriteLine($"[AI 自检] 首次有声样本位置：{(firstSound < 0 ? "始终静音" : firstSound + "（约 " + firstSound * 1000 / 48000 + " ms）")}");
        Console.WriteLine($"[AI 自检] 后半段：峰值 {20 * Math.Log10(peak + 1e-9):0.0} dBFS，"
                          + $"有效值 {20 * Math.Log10(rms + 1e-9):0.0} dBFS，静音占比 {silentRatio:0.0}%");

        Log.Info($"[AI 自检] 流式：块数 {effect.WorkerBlocks}，写入 {effect.PushedSamples}，丢弃 {effect.DroppedSamples}，"
                 + $"首次有声 {firstSound}，后半段峰值 {peak:0.000}，静音占比 {silentRatio:0.0}%");

        try
        {
            using var writer = new WaveFileWriter(outPath, new WaveFormat(48000, 16, 1));
            foreach (var sample in output)
            {
                var value = (short)(Math.Clamp(sample, -1f, 1f) * 32767);
                writer.WriteByte((byte)(value & 0xFF));
                writer.WriteByte((byte)((value >> 8) & 0xFF));
            }

            Console.WriteLine($"[AI 自检] 输出已写入：{outPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI 自检] 写 wav 失败：{ex.Message}");
        }

        return peak > 1e-4 ? 0 : 2;
    }

    private static string? Value(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }

        return null;
    }

    /// <summary>合成一段"类语音"信号（基频带起伏的谐波堆），仅用于链路自检。</summary>
    private static float[] Synthesize(double seconds)
    {
        var count = (int)(48000 * seconds);
        var samples = new float[count];
        var random = new Random(1234);
        var phase = 0.0;

        for (var i = 0; i < count; i++)
        {
            var t = (double)i / 48000;
            var f0 = 120 + 25 * Math.Sin(2 * Math.PI * 1.7 * t) + 6 * Math.Sin(2 * Math.PI * 5.3 * t);
            phase += 2 * Math.PI * f0 / 48000;
            var value = 0.0;
            for (var h = 1; h <= 20; h++) value += Math.Sin(phase * h) / h;
            samples[i] = (float)(0.25 * value + 0.002 * random.NextDouble());
        }

        return samples;
    }

    /// <summary>读任意音频文件并转成 48 kHz 单声道。用的是与直播链路同一个重采样器。</summary>
    private static float[] LoadAudio48k(string path)
    {
        using var reader = new AudioFileReader(path);
        var sampleProvider = (ISampleProvider)reader;
        var buffer = new float[reader.Length / 4 + 1024];
        var total = 0;

        // NAudio 3 的 ISampleProvider.Read 只接受 Span<float>
        while (total < buffer.Length)
        {
            var want = Math.Min(65536, buffer.Length - total);
            var read = sampleProvider.Read(buffer.AsSpan(total, want));
            if (read <= 0) break;
            total += read;
        }

        var resampler = new AudioResampler(reader.WaveFormat.SampleRate, 48000);
        var output = new float[resampler.MaxOutput(total) + 1024];
        var produced = resampler.Process(buffer.AsSpan(0, total), output);
        return output[..produced];
    }
}
