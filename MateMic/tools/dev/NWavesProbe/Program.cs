using System;
using System.Linq;
using NWaves.Filters.Base;
using NWaves.Operations.Tsm;
using NWaves.Signals;
using NWaves.Transforms;
using NWaves.Windows;

// 按"NWaves 现成模块组合"的思路验证变调：
//   NWaves.Transforms.Stft（已实测完美重建）+ NWaves.Operations.Tsm.PhaseVocoder + NWaves.Operations.Resampler
// 只写组合逻辑，不自己实现 FFT/STFT/声码器。
const int sampleRate = 48000;
const float testHz = 355f;

float[] signal = new float[sampleRate];
for (var i = 0; i < signal.Length; i++)
    signal[i] = 0.3f * MathF.Sin(2f * MathF.PI * testHz * i / sampleRate);

foreach (var semitones in new[] { 2.0, -2.0, 5.0, -5.0 })
{
    var ratio = Math.Pow(2, semitones / 12.0);

    // 1) 时间伸缩：拉长 ratio 倍（音高不变）
    var stretched = TimeStretch(signal, ratio, sampleRate);
    if (stretched == null)
    {
        Console.WriteLine($"  {semitones,5:0.0} 半音 → 时间伸缩失败");
        continue;
    }

    // 2) 重采样：按 ratio 倍加快读取 → 音高升高，时长恢复
    var resampled = ResampleBy(signal.Length, stretched, ratio, sampleRate);

    var measured = MeasureFrequency(resampled, sampleRate);
    var expected = testHz * ratio;
    var cents = measured > 0 ? 1200.0 * Math.Log2(measured / expected) : double.NaN;
    Console.WriteLine($"  {semitones,5:0.0} 半音 → {measured,7:0.0} Hz（期望 {expected,7:0.0}）" +
                      $"偏差 {cents,7:0.0} 音分{(Math.Abs(cents) < 40 ? "  ✓" : "  ✗")}");
}

static float[] TimeStretch(float[] input, double ratio, int sampleRate)
{
    try
    {
        var vocoder = new PhaseVocoder(ratio, 512, 2048);
        var stretched = vocoder.ApplyTo(new DiscreteSignal(sampleRate, input, true), FilteringMethod.OverlapAdd);
        return stretched.Samples;
    }
    catch (Exception ex)
    {
        Console.WriteLine("      [时间伸缩异常] " + ex.GetType().Name + ": " + ex.Message);
        return null;
    }
}

/// <summary>按目标长度对伸缩后的信号做线性插值重采样（NWaves.Resampler 只支持整数倍率，这里用插值）。</summary>
static float[] ResampleBy(int targetLength, float[] input, double ratio, int sampleRate)
{
    var output = new float[targetLength];
    for (var i = 0; i < targetLength; i++)
    {
        var position = i * ratio;
        var index = (int)position;
        if (index + 1 >= input.Length)
        {
            output[i] = index < input.Length ? input[index] : 0f;
            continue;
        }

        var fraction = (float)(position - index);
        output[i] = input[index] + (input[index + 1] - input[index]) * fraction;
    }

    return output;
}

static float MeasureFrequency(float[] samples, int sampleRate)
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

// 追加：列出 NWaves 里与"变调 / 变声"相关的公开类型，
// 用来判断能否直接复用（能复用就不必自研，也不引入新依赖）。
{
    var asm = typeof(NWaves.Signals.DiscreteSignal).Assembly;
    Console.WriteLine();
    Console.WriteLine("[NWaves] 与音高/变声相关的公开类型：");
    var hits = 0;
    foreach (var type in asm.GetExportedTypes().OrderBy(x => x.FullName))
    {
        var n = type.Name;
        if (n.Contains("Pitch", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Psola", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Vocoder", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Formant", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Resampler", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Stretch", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("   " + type.FullName);
            hits++;
        }
    }

    Console.WriteLine($"   共 {hits} 个");
}