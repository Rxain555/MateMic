using System;
using System.Linq;
using MateMic.Dsp;
using NAudio.Wave;

// 用应用里的 PitchShifter（NWaves 组合）做分块验证，并打印硬调音的内部状态，
// 定位为何 App 内输出恒定错误频率。
var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
const int sampleRate = 48000;
const float inputHz = 355f;

foreach (var semitones in new[] { 0f, 5f, -5f })
{
    var shifter = new PitchShifter(sampleRate) { Semitones = semitones };
    var input = new float[sampleRate];
    for (var i = 0; i < input.Length; i++)
        input[i] = 0.3f * MathF.Sin(2f * MathF.PI * inputHz * i / sampleRate);

    var output = new float[input.Length];
    for (var offset = 0; offset + 480 <= input.Length; offset += 480)
    {
        var block = new float[480];
        Array.Copy(input, offset, block, 0, 480);
        shifter.Process(block);
        Array.Copy(block, 0, output, offset, 480);
    }

    var measured = Measure(output.AsSpan(input.Length / 2).ToArray(), sampleRate);
    var expected = inputHz * MathF.Pow(2f, semitones / 12f);
    Console.WriteLine($"[PitchShifter] {semitones,5:0.0} 半音 → {measured,7:0.0} Hz（期望 {expected,7:0.0}）");
}

{
    var effect = new HardTuneEffect(sampleRate) { RetuneSpeed = 90f, HarmonyAmount = 0f };
    effect.Configure(format);

    var input = new float[sampleRate];
    for (var i = 0; i < input.Length; i++)
        input[i] = 0.28f * MathF.Sin(2f * MathF.PI * inputHz * i / sampleRate)
                   + 0.10f * MathF.Sin(2f * MathF.PI * inputHz * 2f * i / sampleRate);

    for (var offset = 0; offset + 480 <= input.Length; offset += 480)
    {
        effect.ProcessInPlace(input.AsSpan(offset, 480), sampleRate);
        if (offset % 48000 == 0)
            Console.WriteLine($"    t={offset / 48000.0:0.0}s 检测={effect.LastDetectedFrequency:0.0} Hz " +
                              $"目标MIDI={effect.LastTargetMidi:0.00} 施加={effect.LastAppliedSemitones:0.00} 半音");
    }

    var measured = Measure(input.AsSpan(input.Length / 2).ToArray(), sampleRate);
    Console.WriteLine($"[HardTune] 输出 {measured:0.0} Hz（输入 {inputHz}）");
}

// ---------------------------------------------------------------- 实时性能测量
//
// 目的：把"电音卡卡的"从猜测变成数字。
// 判据：real-time factor（RTF）= 处理耗时 / 音频时长。
//   RTF ≥ 1  → 单线程根本跟不上，必然丢帧（听感就是卡顿）
//   经验值：单个模块 RTF ≤ 0.2、整链 ≤ 0.5 才安全（还要给降噪等模块留余量）
{
    const int blockSize = 480;          // 与音频回调一致
    const double seconds = 10.0;
    var total = (int)(sampleRate * seconds);
    var blocks = total / blockSize;

    // 语音样信号：基频 180 Hz + 谐波 + 轻微颤音（比纯正弦更接近真实负载）
    var voice = new float[total];
    for (var i = 0; i < total; i++)
    {
        var t = i / (double)sampleRate;
        var f0 = 180.0 * (1.0 + 0.02 * Math.Sin(2 * Math.PI * 5 * t));
        double v = 0;
        for (var h = 1; h <= 12; h++) v += Math.Sin(2 * Math.PI * f0 * h * t) / h;
        voice[i] = (float)(0.075 * v);
    }

    // (a) 只测基频检测（每块都跑 —— 这是 App 当前的行为）
    {
        var detector = new PitchDetector(sampleRate, windowSize: 1024);
        var window = new float[1024];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var b = 0; b < blocks; b++)
        {
            var off = b * blockSize;
            for (var i = 0; i < 1024; i++) window[i] = voice[(off + i) % total];
            detector.Detect(window);
        }
        sw.Stop();
        var rtf = sw.Elapsed.TotalSeconds / seconds;
        Console.WriteLine($"[性能] 基频检测（每块）：RTF = {rtf:0.000}  占实时预算 {rtf * 100:0.0}%");
    }

    // (b) 只测变调器（SmbPitchShifter）
    {
        var shifter = new PitchShifter(sampleRate) { Semitones = 5f };
        var block = new float[blockSize];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var b = 0; b < blocks; b++)
        {
            Array.Copy(voice, b * blockSize, block, 0, blockSize);
            shifter.Process(block);
        }
        sw.Stop();
        var rtf = sw.Elapsed.TotalSeconds / seconds;
        Console.WriteLine($"[性能] 变调器（SmbPitchShifter）：RTF = {rtf:0.000}  占实时预算 {rtf * 100:0.0}%");
    }

    // (c) 整个电音模块（含检测 + 变调 + EQ + 厚度）
    {
        var probeFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        var effect = new HardTuneEffect(sampleRate) { RetuneSpeed = 90f, HarmonyAmount = 0.25f };
        effect.Configure(probeFormat);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var b = 0; b < blocks; b++)
            effect.ProcessInPlace(voice.AsSpan(b * blockSize, blockSize), sampleRate);
        sw.Stop();
        var rtf = sw.Elapsed.TotalSeconds / seconds;
        Console.WriteLine($"[性能] 电音整模块（变调开）：RTF = {rtf:0.000}  占实时预算 {rtf * 100:0.0}%");
    }

    // (d) 电音模块关掉变调（隔离出检测 + 音染的成本）
    {
        var probeFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        var effect = new HardTuneEffect(sampleRate) { RetuneSpeed = 90f, HarmonyAmount = 0.25f };
        effect.EnablePitchShift = false;
        effect.Configure(probeFormat);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var b = 0; b < blocks; b++)
            effect.ProcessInPlace(voice.AsSpan(b * blockSize, blockSize), sampleRate);
        sw.Stop();
        var rtf = sw.Elapsed.TotalSeconds / seconds;
        Console.WriteLine($"[性能] 电音整模块（变调关）：RTF = {rtf:0.000}  占实时预算 {rtf * 100:0.0}%");
    }
}
// ---------------------------------------------------------------- 渲染成 WAV，便于离线试听
//
// 为什么要输出文件：AI 无法"听"。把原始信号与处理后信号各写一个 wav，
// 用户/AI 都能用播放器反复对比，定位"卡卡的"到底是哪一类问题
// （相位声码器音染 / 吸附跳变 / 延迟感 / 音量突变）。
{

    // 一段更像人声的测试信号：基频 180 Hz、带颤音、12 个谐波、每 1.5 秒换一个音高
    const double seconds2 = 6.0;
    var n = (int)(sampleRate * seconds2);
    var dry = new float[n];
    for (var i = 0; i < n; i++)
    {
        var t = i / (double)sampleRate;
        var step = (int)(t / 1.5);
        var baseHz = step switch { 0 => 180.0, 1 => 220.0, 2 => 165.0, _ => 196.0 };
        var f0 = baseHz * (1.0 + 0.015 * Math.Sin(2 * Math.PI * 5.5 * t));
        double v = 0;
        for (var h = 1; h <= 12; h++) v += Math.Sin(2 * Math.PI * f0 * h * t) / h;
        dry[i] = (float)(0.075 * v);
    }

    var wet = (float[])dry.Clone();
    var format2 = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
    var tune = new HardTuneEffect(sampleRate) { RetuneSpeed = 90f, HarmonyAmount = 0.25f };
    tune.Configure(format2);
    for (var offset = 0; offset + 480 <= wet.Length; offset += 480)
        tune.ProcessInPlace(wet.AsSpan(offset, 480), sampleRate);

    WriteWav("tune-dry.wav", dry, sampleRate);
    WriteWav("tune-tune.wav", wet, sampleRate);
    Console.WriteLine("[渲染] 已写出 tune-dry.wav（原声）与 tune-tune.wav（电音处理后），可直接播放对比");
}

static void WriteWav(string path, float[] samples, int sampleRate)
{
    using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));
    writer.WriteSamples(samples, 0, samples.Length);
}
// ---------------------------------------------------------------- PSOLA：音高与共振峰独立
//
// 判据：① 变调量准确（测输出基频）；② **只动共振峰因子时，基频不变而频谱包络峰移动**
//       —— 后者正是"真共振峰"与"频谱整体搬移（花栗鼠）"的分水岭。
{
    Console.WriteLine("[PSOLA] 音高因子：");
    const int fs = 48000;
    foreach (var semitones in new[] { 7.0, -5.0 })
    {
        var shifter = new PsolaPitchShifter(fs) { PitchFactor = Math.Pow(2, semitones / 12) };
        var input = new float[fs];
        for (var i = 0; i < input.Length; i++)
        {
            double v = 0;
            for (var h = 1; h <= 10; h++) v += Math.Sin(2 * Math.PI * 220 * h * i / (double)fs) / h;
            input[i] = (float)(0.3 * v);
        }

        var output = new float[input.Length];
        for (var off = 0; off + 480 <= input.Length; off += 480)
        {
            Array.Copy(input, off, output, off, 480);
            shifter.Process(output.AsSpan(off, 480));
        }

        var expected = 220.0 * Math.Pow(2, semitones / 12);
        var measured = Measure(output.AsSpan(fs / 2, fs / 2 - 2000).ToArray(), fs);
        Console.WriteLine($"    {semitones,5:0.0} 半音 → {measured,7:0.0} Hz（期望 {expected,7:0.0}）");
    }

    Console.WriteLine("[PSOLA] 共振峰因子（基频应保持 120 Hz 不动）：");
    foreach (var formant in new[] { 1.0, 1.5 })
    {
        var shifter = new PsolaPitchShifter(fs) { PitchFactor = 1.0, FormantFactor = formant };

        // 120 Hz 脉冲串过一个 700 Hz 双极点谐振器 → 频谱包络在 700 Hz 处有明显共振峰
        var input = new float[fs];
        const double r = 0.985;
        var w = 2 * Math.PI * 700 / fs;
        var a1 = 2 * r * Math.Cos(w);
        var a2 = -r * r;
        double y1 = 0, y2 = 0;
        for (var i = 0; i < input.Length; i++)
        {
            var pulse = i % 400 == 0 ? 1.0 : 0.0;
            var y = pulse + a1 * y1 + a2 * y2;
            y2 = y1;
            y1 = y;
            input[i] = (float)(0.05 * y);
        }

        var output = new float[input.Length];
        for (var off = 0; off + 480 <= input.Length; off += 480)
        {
            Array.Copy(input, off, output, off, 480);
            shifter.Process(output.AsSpan(off, 480));
        }

        var slice = output.AsSpan(fs / 2, 20480).ToArray();
        var peak = EnvelopePeak(slice, fs);
        var pitch = Measure(slice, fs);
        Console.WriteLine($"    因子 {formant:0.0} → 包络峰 {peak,6:0} Hz（期望约 {700 * formant,6:0}）"
                          + $"  基频 {pitch,6:0} Hz（期望 120）  周期={shifter.LastPeriod:0.0} 样本");
    }
}

// 在 200–4000 Hz 之间扫一遍，用"±3 点平滑后取峰"的方式找频谱包络峰（避开单个谐波的尖峰）
static float EnvelopePeak(float[] samples, int sampleRate)
{
    const int step = 20;
    var mags = new double[4000 / step + 1];
    for (var hz = 200; hz <= 4000; hz += step)
    {
        double re = 0, im = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var phase = 2 * Math.PI * hz * i / sampleRate;
            re += samples[i] * Math.Cos(phase);
            im += samples[i] * Math.Sin(phase);
        }
        mags[hz / step] = Math.Sqrt(re * re + im * im);
    }

    var best = 0.0;
    var bestHz = 0;
    for (var k = 3; k < mags.Length - 3; k++)
    {
        double sum = 0;
        for (var j = -3; j <= 3; j++) sum += mags[k + j];
        if (sum > best) { best = sum; bestHz = k * step; }
    }

    return bestHz;
}


static float Measure(float[] samples, int sampleRate)
{
    var size = Math.Min(16384, samples.Length);
    var buffer = new float[size];
    Array.Copy(samples, buffer, size);
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
        var den = Math.Sqrt(ea * eb);
        if (den < 1e-9) continue;
        var c = (float)(sum / den);
        correlations[lag] = c;
        if (c > peak) peak = c;
    }

    var threshold = peak * 0.9f;
    for (var lag = minLag + 1; lag <= maxLag; lag++)
    {
        if (correlations[lag] < threshold) continue;
        if (correlations[lag] >= correlations[lag - 1] && correlations[lag] >= correlations[lag + 1])
            return (float)sampleRate / lag;
    }

    return 0f;
}
