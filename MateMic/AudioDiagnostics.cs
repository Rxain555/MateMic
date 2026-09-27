using System.Diagnostics;
using MateMic.Audio;
using MateMic.Core;
using MateMic.Dsp;
using NAudio.CoreAudioApi;

namespace MateMic;

/// <summary>
/// 音频链路诊断：不用界面即可逐级测量电平，用于定位「有输入频谱但没有输出」这类问题。
/// 用法：MateMic.exe --audiocheck [秒数] [输出设备名关键字] [输入设备名关键字]
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

        // 「同步按住键」自检：MateMic.exe --audiocheck --key [按键]
        if (args.Contains("--key", StringComparer.OrdinalIgnoreCase))
        {
            var key = args.SkipWhile(a => !string.Equals(a, "--key", StringComparison.OrdinalIgnoreCase))
                          .Skip(1)
                          .FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "F24";
            RunKeyCheck(key);
            return;
        }

        // 「同步按住键」松开时机自检：MateMic.exe --audiocheck --hold [音频文件]
        if (args.Contains("--hold", StringComparer.OrdinalIgnoreCase))
        {
            var file = args.SkipWhile(a => !string.Equals(a, "--hold", StringComparison.OrdinalIgnoreCase))
                           .Skip(1)
                           .FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
                       ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                           "Media", "Alarm01.wav");
            RunHoldTimingCheck(file);
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
                 $"，MicEnabled={engine.MicMixer.MicEnabled}，播放器进监听={engine.MicMixer.PlayerMonitorEnabled}" +
                 $"，监听设备={engine.MonitorEnabled}，监听已写入样本={engine.MonitorSamples}");

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
    /// 「同步按住键」自检（<c>MateMic.exe --keycheck [按键]</c>，默认 F24）。
    /// 按键卡住（游戏里一直按着说话键）是这个功能最严重的故障，因此这里做一次真实的
    /// 端到端验证：注入按下 → 用 GetAsyncKeyState 确认系统真的认为它按着 → 松开 → 再确认已松开。
    /// F24 在标准键盘上不存在，不会打扰任何正在运行的软件。
    /// </summary>
    private static void RunKeyCheck(string gesture)
    {
        Environment.ExitCode = RunKeyCheckCore(gesture) ? 0 : 1;
    }

    private static bool RunKeyCheckCore(string gesture)
    {
        Log.Info($"[按键自检] 目标按键：{gesture}（用 SendInput 注入扫描码，随后读取系统键状态验证）");

        if (!HotkeyService.TryParse(gesture, out _, out var virtualKey, out var parseError))
        {
            Log.Error("[按键自检] 无法解析按键：" + parseError);
            return false;
        }

        bool IsDown() => (GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;

        using var service = new KeyboardHoldService();

        if (IsDown())
        {
            Log.Error("[按键自检] 该按键在开始前就已经处于按下状态，无法验证。请换一个按键（建议 F24）。");
            return false;
        }

        var pressed = service.Press(gesture, skipIfAlreadyDown: false);
        if (!pressed)
        {
            Log.Error("[按键自检] 注入按下失败：" + (service.LastError ?? "未知原因"));
            return false;
        }

        var downSeen = WaitFor(IsDown, true, 500);
        Log.Info($"[按键自检] 按下后系统键状态 = {(downSeen ? "已按下" : "仍未按下")}");

        service.Release();
        var upSeen = WaitFor(IsDown, false, 500);
        Log.Info($"[按键自检] 松开后系统键状态 = {(upSeen ? "已松开" : "仍按着")}");

        if (downSeen && upSeen)
        {
            Log.Info("[按键自检] 结论：按下与松开都生效 ✅（该功能可用）");
            return true;
        }

        if (!downSeen)
            Log.Error("[按键自检] 结论：注入的按键没有被系统识别 ❌ —— 目标程序多半也收不到。" +
                      "常见原因：本程序权限低于目标程序，或被安全软件拦截。");
        else
            Log.Error("[按键自检] 结论：按键被按下后没有松开 ❌ —— 这个功能在游戏里会把按键卡住，请勿使用。");

        return false;
    }

    /// <summary>轮询等待某个布尔状态变成期望值。</summary>
    private static bool WaitFor(Func<bool> probe, bool expected, int timeoutMs)
    {
        var deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            if (probe() == expected) return true;
            Thread.Sleep(10);
        }

        return probe() == expected;
    }

    /// <summary>
    /// 「同步按住键」松开时机自检（<c>MateMic.exe --audiocheck --hold [文件]</c>）。
    ///
    /// 要验证的是：按住键松开的那一刻，音频是不是**真的播完了**。
    /// 解码到文件末尾 ≠ 声音放完——解码线程一次会把缓冲填到 ~400 ms 的水位，
    /// 所以"解码结束"可能比"声音结束"早几百毫秒。
    /// 这里直接播一个文件、记下 Started/Finished 的时间差，并与文件时长对比。
    /// </summary>
    private static void RunHoldTimingCheck(string file)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine($"[松开时机自检] 文件：{file}");
        if (!File.Exists(file))
        {
            Console.WriteLine("[松开时机自检] 文件不存在。");
            Environment.ExitCode = 1;
            return;
        }

        // 诊断模式没有主窗口，Media Foundation 的输入源读取器会以 0x80070005 失败；
        // 正常界面路径下 WPF 已经把它初始化好了，这里显式补一次。
        try
        {
            NAudio.MediaFoundation.MediaFoundationApi.Startup();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[松开时机自检] Media Foundation 初始化失败：" + ex.Message);
        }

        var player = new FilePlayerService();
        var finished = new ManualResetEventSlim(false);
        var stateLog = new List<string>();
        var startedAt = 0L;

        player.StateChanged += (_, state) =>
        {
            var now = Stopwatch.GetTimestamp();
            stateLog.Add(state.ToString());
            if (state == PlaybackState.Started) startedAt = now;
            if (state == PlaybackState.Finished) finished.Set();
            if (state == PlaybackState.Interrupted) finished.Set();   // 播放失败也要收工，别干等超时
        };

        var t0 = Stopwatch.GetTimestamp();
        player.Play(file);

        if (!finished.Wait(TimeSpan.FromSeconds(60)))
        {
            Console.WriteLine($"[松开时机自检] 等待播放结束超时（已记录状态：{string.Join("/", stateLog)}）");
            player.Dispose();
            Environment.ExitCode = 1;
            return;
        }

        if (!stateLog.Contains(PlaybackState.Finished.ToString()))
        {
            // 常见原因：文件无法被 Media Foundation 解码（诊断模式下没有主窗口时尤其容易触发），
            // 或者文件根本不存在。此时测不到时序，如实说明而不是给个假结论。
            Console.WriteLine($"[松开时机自检] 播放未能正常结束，状态：{string.Join(" → ", stateLog)}");
            Console.WriteLine("[松开时机自检] 详细原因见日志（logs\\app_*.log 里的「播放 ... 失败」）。");
            Console.WriteLine("[松开时机自检] 提示：这个自检请用**在界面里能正常播放的文件**，" +
                              "且最好从正常启动的程序所在目录运行。");
            player.Dispose();
            Environment.ExitCode = 1;
            return;
        }

        var finishedMs = Ticks(Stopwatch.GetTimestamp() - t0);
        var fromStartedMs = Ticks(Stopwatch.GetTimestamp() - startedAt);
        Console.WriteLine($"[松开时机自检] 状态序列：{string.Join(" → ", stateLog)}");
        Console.WriteLine($"[松开时机自检] 从调用播放到判定结束：{finishedMs:0} ms；从 Started 到 Finished：{fromStartedMs:0} ms");
        Console.WriteLine($"[松开时机自检] 收尾诊断：{player.LastReleaseDiagnostics}");
        Console.WriteLine("[松开时机自检] 结论：松开按住键发生在 Finished 事件上；" +
                          "若「结束时缓冲还剩」只有几十毫秒，说明按键松开与声音结束基本重合。");
        Console.WriteLine("[松开时机自检] 想更进一步核对，可同时用键盘测试网站观察按键松开时刻。");

        player.Dispose();
    }

    private static double Ticks(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

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
