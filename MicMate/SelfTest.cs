using MicMate.Core;
using MicMate.Denoise;
using MicMate.Dsp;

namespace MicMate;

/// <summary>
/// 离线自检：不需要麦克风与界面即可验证「底噪 → 内置训练 → ONNX 校验 → 曲线提取」整条链路。
/// 用法：MicMate.exe --selftrain &lt;输出目录&gt;
/// </summary>
public static class SelfTest
{
    private const int SampleRate = 48000;


    private static double Rms(float[] data)
    {
        double sum = 0;
        foreach (var value in data) sum += value * value;
        return Math.Sqrt(sum / Math.Max(1, data.Length));
    }

    /// <summary>生成 10 秒 48 kHz 单声道 32-bit float 合成底噪（低频嗡声 + 宽带噪声）。</summary>
    private static void WriteSyntheticNoise(string path)
    {
        const int seconds = 10;
        var frames = SampleRate * seconds;
        var random = new Random(12345);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        var dataBytes = frames * sizeof(float);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)3);          // IEEE float
        writer.Write((short)1);          // 单声道
        writer.Write(SampleRate);
        writer.Write(SampleRate * sizeof(float));
        writer.Write((short)sizeof(float));
        writer.Write((short)32);
        writer.Write("data"u8.ToArray());
        writer.Write(dataBytes);

        for (var i = 0; i < frames; i++)
        {
            var t = i / (float)SampleRate;
            var hum = 0.03f * MathF.Sin(2f * MathF.PI * 50f * t)
                      + 0.012f * MathF.Sin(2f * MathF.PI * 150f * t);
            var hiss = (float)(random.NextDouble() - 0.5) * 0.02f;
            writer.Write(hum + hiss);
        }
    }
}
