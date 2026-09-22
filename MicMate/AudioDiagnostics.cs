using MicMate.Audio;
using MicMate.Core;
using MicMate.Dsp;
using NAudio.CoreAudioApi;

namespace MicMate;

/// <summary>
/// 音频链路诊断：不用界面即可逐级测量电平，用于定位「有输入频谱但没有输出」这类问题。
/// 用法：MicMate.exe --audiocheck [秒数] [输出设备名关键字] [输入设备名关键字]
/// </summary>
public static class AudioDiagnostics
{
    public static void Run(string[] args)
    {
        // 效果器自检：验证电音等模块是否真的改变了信号（而不是挂了个空效果）
        if (args.Contains("--fx", StringComparer.OrdinalIgnoreCase))
        {
            RunEffectCheck();
            return;
        }

        // 电音（硬调音）自检：确认音高真的被吸附到音阶
        if (args.Contains("--tune", StringComparer.OrdinalIgnoreCase))
        {
            RunTuneCheck();
            return;
        }

        var seconds = 6;
        string? outputFilter = null;
        string? inputFilter = null;

        var positional = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (positional.Count > 0 && int.TryParse(positional[0], out var parsed)) seconds = Math.Clamp(parsed, 2, 60);
        if (positional.Count > 1) outputFilter = positional[1];
        if (positional.Count > 2) inputFilter = positional[2];

        var store = new ConfigStore();
        var config = store.Load();

        using var devices = new DeviceService();
        DumpDevices(devices);

        if (outputFilter != null)
        {
            var match = devices.Enumerate(DataFlow.Render)
                .FirstOrDefault(d => d.Name.Contains(outputFilter, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                Log.Error($"[诊断] 没有匹配「{outputFilter}」的播放设备");
                return;
            }

            config.Devices.OutputDeviceId = match.Id;
            Log.Info($"[诊断] 输出设备覆盖为：{match.Name}");
        }

        if (inputFilter != null)
        {
            var match = devices.Enumerate(DataFlow.Capture)
                .FirstOrDefault(d => d.Name.Contains(inputFilter, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                Log.Error($"[诊断] 没有匹配「{inputFilter}」的录音设备");
                return;
            }

            config.Devices.InputDeviceId = match.Id;
            Log.Info($"[诊断] 输入设备覆盖为：{match.Name}");
        }

        // 关键：总开关必须是打开的，否则麦克风被静音，测不到任何东西
        config.AudioProcessingEnabled = true;
        config.MonitorEnabled = true;

        ReportEndpointLevels(devices, config);

        using var engine = new AudioEngine(devices, config);
        engine.Start();
        if (!engine.IsRunning)
        {
            Log.Error("[诊断] 引擎启动失败：" + engine.LastError);
            return;
        }

        Log.Info($"[诊断] 引擎已启动；处理链 {(engine.Chain.Effects.Count == 0 ? "为空（直通）" : string.Join(" → ", engine.Chain.Effects.Select(e => e.Name)))}" +
                 $"，MicEnabled={engine.MicMixer.MicEnabled}，PlayerEnabled={engine.MicMixer.PlayerEnabled}");

        var inputPeak = 0f;
        var outputPeak = 0f;
        var inputSamplesAtStart = engine.InputSpectrum.TotalSamples;
        var outputSamplesAtStart = engine.OutputSpectrum.TotalSamples;

        var steps = seconds * 10;
        for (var i = 0; i < steps; i++)
        {
            Thread.Sleep(100);
            engine.UpdateAnalysis();
            inputPeak = Math.Max(inputPeak, engine.InputSpectrum.Peak);
            outputPeak = Math.Max(outputPeak, engine.OutputSpectrum.Peak);

            if (i % 10 == 9)
            {
                Log.Info($"[诊断] {i / 10 + 1,2}s  输入峰值 {Db(engine.InputSpectrum.Peak),7} dBFS" +
                         $"  输入累计样本 {engine.InputSpectrum.TotalSamples,10}" +
                         $"  输出峰值 {Db(engine.OutputSpectrum.Peak),7} dBFS" +
                         $"  输出累计样本 {engine.OutputSpectrum.TotalSamples,10}");
            }
        }

        var inputSamples = engine.InputSpectrum.TotalSamples - inputSamplesAtStart;
        var outputSamples = engine.OutputSpectrum.TotalSamples - outputSamplesAtStart;

        Log.Info("================ 诊断结论 ================");
        Log.Info($"采集侧累计样本：{inputSamples}（约 {inputSamples / (double)AudioEngine.SampleRate:0.00} 秒音频）");
        Log.Info($"输出侧累计样本：{outputSamples}（约 {outputSamples / (double)AudioEngine.SampleRate:0.00} 秒音频）");
        Log.Info($"混音输出被拉取次数：{engine.MixReadCount}；混音总线被拉取次数：{engine.BusReadCount}");
        Log.Info($"输入峰值 {Db(inputPeak)} dBFS，输出峰值 {Db(outputPeak)} dBFS");

        if (inputSamples < AudioEngine.SampleRate / 2)
            Log.Error("[诊断] 采集侧几乎没有数据 → 录音设备没有在出数据（选错设备 / 该设备本身就静音）。");
        else if (inputPeak < 0.0005f)
            Log.Error("[诊断] 采集有数据但电平极低（<-66 dBFS）→ 麦克风本身没在拾音，或系统静音/输入音量过低。");
        else if (outputSamples < AudioEngine.SampleRate / 2)
            Log.Error("[诊断] 输出侧几乎没有数据 → 播放链路没有被拉动（播放设备不可用）。");
        else if (outputPeak < 0.0005f)
            Log.Error("[诊断] 输出侧有数据但电平极低 → 混音/处理把信号压没了。");
        else
            Log.Info("[诊断] 采集与输出两侧都有正常电平：链路工作正常，若仍听不到声音，请检查 MIXLINE 的路由与接收端。");
    }

    /// <summary>
    /// 效果器自检：对同一段合成语音信号分别跑一遍各效果，比较输出与输入的差异，
    /// 用来确认「电音」这类效果确实改变了信号，而不是挂了个空效果器。
    /// </summary>
    private static void RunEffectCheck()
    {
        const int sampleRate = AudioEngine.SampleRate;
        var format = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        Log.Info("[效果自检] 输入：440 Hz + 1.7 kHz 合成信号，共 48000 样本（1 秒）");

        foreach (var kind in Enum.GetValues<CreativeEffectKind>())
        {
            var settings = new CreativeEffectSettings { Enabled = true, Kind = kind, Amount = 55f };
            // 注意：Enabled 由引擎按配置赋值，直接构造时必须显式打开，否则 Read 会直接返回
            var effect = new CreativeEffect(format, settings) { Enabled = true };

            var input = BuildProbeSignal(sampleRate);
            var output = new float[input.Length];
            input.CopyTo(output, 0);

            for (var offset = 0; offset < output.Length; offset += 480)
            {
                var count = Math.Min(480, output.Length - offset);
                effect.Read(output.AsSpan(offset, count));
            }

            var difference = 0.0;
            var outputEnergy = 0.0;
            for (var i = 0; i < input.Length; i++)
            {
                difference += Math.Abs(output[i] - input[i]);
                outputEnergy += output[i] * output[i];
            }

            var averageDelta = difference / input.Length;
            var outputRms = Math.Sqrt(outputEnergy / input.Length);

            Log.Info($"[效果自检] {CreativeEffect.KindName(kind),-4} 平均改变量 {averageDelta:0.00000}" +
                     $"，输出 RMS {outputRms:0.00000}" +
                     $"{(averageDelta < 0.0001 ? "  ← 几乎没有改变，效果可能无效" : string.Empty)}");
        }
    }

    private static float[] BuildProbeSignal(int sampleRate)
    {
        var samples = new float[sampleRate];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (float)sampleRate;
            samples[i] = 0.25f * MathF.Sin(2f * MathF.PI * 440f * t)
                         + 0.15f * MathF.Sin(2f * MathF.PI * 1700f * t);
        }

        return samples;
    }

    /// <summary>
    /// 电音（硬调音）自检：输入若干个**明显走音**的频率，测量输出主频，
    /// 确认音高确实被吸附到 C 大调的音级上（否则就只是加了音染、没做调音）。
    /// </summary>
    private static void RunTuneCheck()
    {
        const int sampleRate = AudioEngine.SampleRate;
        var format = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        // C 大调音级频率，覆盖 A3–C5。
        // 必须包含 A3(220)/B3(246.9)：低音输入（如 228 Hz）会被正确吸附到 A3，
        // 早期参考表只有 C4–C5，于是 218 Hz 找不到 220 这个音级、被拿去和 C4 比，
        // 误报成 −314 音分"未吸附"。
        var scaleFrequencies = new[]
        {
            220.00f, 246.94f, 261.63f, 293.66f, 329.63f, 349.23f, 392.00f, 440.00f, 493.88f, 523.25f,
        };

        Log.Info("[调音自检] 输入故意走音的正弦，检查输出主频是否被吸到 C 大调音级");
        Log.Info("[调音自检] C 大调音级：" + string.Join(", ", scaleFrequencies.Select(f => f.ToString("0.0"))));

        foreach (var inputHz in new[] { 228f, 415f, 355f, 470f })
        {
            var settings = new CreativeEffectSettings { Enabled = true, Kind = CreativeEffectKind.Robot, Amount = 88f };
            var effect = new CreativeEffect(format, settings) { Enabled = true };

            var samples = new float[sampleRate];
            for (var i = 0; i < samples.Length; i++)
            {
                var t = i / (float)sampleRate;
                // 加一点泛音更像人声，便于基频检测
                samples[i] = 0.28f * MathF.Sin(2f * MathF.PI * inputHz * t)
                             + 0.10f * MathF.Sin(2f * MathF.PI * inputHz * 2f * t);
            }

            for (var offset = 0; offset < samples.Length; offset += 480)
            {
                var count = Math.Min(480, samples.Length - offset);
                effect.Read(samples.AsSpan(offset, count));
            }

            // 用自相关测后 0.5 秒的主频
            var tail = samples.AsSpan(samples.Length / 2);
            var measured = MeasureFrequency(tail, sampleRate);
            var nearest = scaleFrequencies.OrderBy(f => Math.Abs(f - measured)).First();
            var cents = measured > 0 ? 1200.0 * Math.Log2(measured / nearest) : double.NaN;

            // 吸附的正确定义：输出应当接近**某个音级**（允许 50 音分内）。
            // 早期版本只跟"最近的那个音级"比，而低频输入（如 228 Hz）经吸附后落在
            // A3=220 Hz 上——它确实是个音级，但测频函数在低频偶有半频误判，
            // 于是把 218 Hz 报成 109 Hz，再与 C4 相比得到 −314 音分，误判为"未吸附"。
            var bestCents = scaleFrequencies
                .Select(f => measured > 0 ? Math.Abs(1200.0 * Math.Log2(measured / f)) : double.MaxValue)
                .Min();
            var snapped = measured > 0 && bestCents < 50;

            Log.Info($"[调音自检] 输入 {inputHz,6:0.0} Hz → 输出 {measured,6:0.0} Hz" +
                     $"，最近音级 {nearest,6:0.0} Hz，偏差 {cents,6:0.0} 音分" +
                     $"，到最近音级的最小距离 {bestCents,5:0.0} 音分" +
                     $"{(snapped ? "  ✓ 已吸附" : "  ✗ 未吸附")}");
        }
    }

    /// <summary>自相关测主频（与 PitchDetector 独立实现，避免自我验证）。</summary>
    private static float MeasureFrequency(ReadOnlySpan<float> samples, int sampleRate)
    {
        const int size = 16384;
        if (samples.Length < size) return 0f;

        var buffer = new float[size];
        samples[..size].CopyTo(buffer);

        var minLag = sampleRate / 1500;
        var maxLag = sampleRate / 70;
        var correlations = new float[maxLag + 2];
        var peak = 0f;

        for (var lag = minLag; lag <= maxLag; lag++)
        {
            double sum = 0, ea = 0, eb = 0;
            for (var i = 0; i < size - lag; i++)
            {
                sum += buffer[i] * buffer[i + lag];
                ea += buffer[i] * buffer[i];
                eb += buffer[i + lag] * buffer[i + lag];
            }

            var denominator = Math.Sqrt(ea * eb);
            if (denominator < 1e-9) continue;
            var correlation = (float)(sum / denominator);
            correlations[lag] = correlation;
            if (correlation > peak) peak = correlation;
        }

        // 取第一个足够高的峰：周期信号在每个倍周期都有接近 1.0 的峰，
        // 直接取全局峰会选到 5 倍周期（实测把 355 Hz 测成 71 Hz）。
        var threshold = peak * 0.9f;
        for (var lag = minLag + 1; lag <= maxLag; lag++)
        {
            if (correlations[lag] < threshold) continue;
            if (correlations[lag] >= correlations[lag - 1] && correlations[lag] >= correlations[lag + 1])
                return (float)sampleRate / lag;
        }

        return 0f;
    }

    private static void DumpDevices(DeviceService devices)
    {
        Log.Info("[诊断] ---- 播放设备 ----");
        foreach (var d in devices.Enumerate(DataFlow.Render))
            Log.Info($"[诊断]   {(d.IsDefault ? "*" : " ")} {d.Name}");

        Log.Info("[诊断] ---- 录音设备 ----");
        foreach (var d in devices.Enumerate(DataFlow.Capture))
            Log.Info($"[诊断]   {(d.IsDefault ? "*" : " ")} {d.Name}");
    }

    /// <summary>读取端点自身的音量与静音状态——系统层静音常常是「没有声音」的真因。</summary>
    private static void ReportEndpointLevels(DeviceService devices, AppConfig config)
    {
        try
        {
            var capture = devices.GetDevice(config.Devices.InputDeviceId, DataFlow.Capture);
            if (capture != null)
            {
                using var volume = capture.AudioEndpointVolume;
                Log.Info($"[诊断] 输入端点「{capture.FriendlyName}」静音={volume.Mute}，音量={volume.MasterVolumeLevelScalar * 100:0}%");
                capture.Dispose();
            }

            var render = devices.GetDevice(config.Devices.OutputDeviceId, DataFlow.Render);
            if (render != null)
            {
                using var volume = render.AudioEndpointVolume;
                Log.Info($"[诊断] 输出端点「{render.FriendlyName}」静音={volume.Mute}，音量={volume.MasterVolumeLevelScalar * 100:0}%");
                render.Dispose();
            }

            var monitor = devices.GetDevice(config.Devices.MonitorDeviceId, DataFlow.Render);
            if (monitor != null)
            {
                using var volume = monitor.AudioEndpointVolume;
                Log.Info($"[诊断] 监听端点「{monitor.FriendlyName}」静音={volume.Mute}，音量={volume.MasterVolumeLevelScalar * 100:0}%");
                monitor.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[诊断] 读取端点音量失败：" + ex.Message);
        }
    }

    private static string Db(float linear)
        => linear <= 1e-7f ? "-inf" : (20f * MathF.Log10(linear)).ToString("0.0");
}
