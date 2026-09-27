using System;
using MateMic.Core;
using MateMic.Dsp;
using NAudio.Wave;

var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
const int total = 48000;

Console.WriteLine("=== 1) 对齐与连续性（直通模型，Wet=50）===");
foreach (var blockSize in new[] { 480, 144, 256, 192, 64, 1000, 4096, 97 })
{
    var settings = new DenoiseSettings { Enabled = true, Model = "内置", Strength = 0, Wet = 50 };
    var effect = new DenoiseEffect(format, settings, new SpectralDenoiseModel { Strength = 0 }) { Enabled = true };
    var input = new float[total];
    var output = new float[total];
    for (var i = 0; i < total; i++) input[i] = 0.3f * MathF.Sin(2f * MathF.PI * 220f * i / 48000f);

    for (var offset = 0; offset + blockSize <= total; offset += blockSize)
    {
        var buffer = new float[blockSize];
        Array.Copy(input, offset, buffer, 0, blockSize);
        effect.Read(buffer);
        Array.Copy(buffer, 0, output, offset, blockSize);
    }

    double err = 0; var n = 0;
    for (var i = 4800; i < total; i++) { err += Math.Abs(output[i] - input[i - 480]); n++; }
    var meanErr = err / n;
    var maxStep = 0.0;
    for (var i = 4801; i < total; i++) { var s = Math.Abs(output[i] - output[i - 1]); if (s > maxStep) maxStep = s; }

    Console.WriteLine($"块长 {blockSize,5}：误差 {meanErr:0.0000000}  最大跳变 {maxStep:0.00000}" +
                      $"{(meanErr < 0.0001 && maxStep < 0.02 ? "  ✓" : "  ✗")}  帧 {effect.ProcessedFrames}");
}

Console.WriteLine();
Console.WriteLine("=== 2) 降噪效果（内置模型，强度 100）===");
foreach (var blockSize in new[] { 480, 144, 256 })
{
    var settings = new DenoiseSettings { Enabled = true, Model = "内置", Strength = 100, Wet = 100 };
    var effect = new DenoiseEffect(format, settings, new SpectralDenoiseModel { Strength = 100 }) { Enabled = true };
    var random = new Random(4242);
    var input = new float[total * 2];
    var output = new float[total * 2];
    for (var i = 0; i < input.Length; i++) input[i] = (float)(random.NextDouble() - 0.5) * 0.08f;

    for (var offset = 0; offset + blockSize <= input.Length; offset += blockSize)
    {
        var buffer = new float[blockSize];
        Array.Copy(input, offset, buffer, 0, blockSize);
        effect.Read(buffer);
        Array.Copy(buffer, 0, output, offset, blockSize);
    }

    double i2 = 0, o2 = 0;
    for (var i = 24000; i < input.Length; i++) { i2 += input[i] * input[i]; o2 += output[i] * output[i]; }
    var change = 20 * Math.Log10(Math.Sqrt(o2 / i2));
    Console.WriteLine($"块长 {blockSize,5}：纯噪声 {change:+0.0;-0.0;0} dB  帧 {effect.ProcessedFrames}  跳过 {effect.SkippedFrames}");
}
