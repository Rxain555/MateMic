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
