using NAudio.Wave;
using NAudio.Wave.SampleProviders;

// 音频格式转换小工具（VCClient 的文件转换接口要的是"原始 Float32 PCM"）。
// 本机没有 ffmpeg，所以走 Windows 自带的 Media Foundation 解码（支持 m4a / mp3 / wav 等）。
//
//   VcFileProbe to-f32 <输入音频> <输出.f32> [采样率=48000]
//   VcFileProbe to-wav <输入.f32> <输出.wav> [采样率=48000]

if (args.Length < 3)
{
    Console.WriteLine("用法：");
    Console.WriteLine("  VcFileProbe to-f32 <输入音频> <输出.f32> [采样率]");
    Console.WriteLine("  VcFileProbe to-wav <输入.f32> <输出.wav> [采样率]");
    return 1;
}

var mode = args[0].ToLowerInvariant();
var inputPath = args[1];
var outputPath = args[2];
var sampleRate = args.Length > 3 ? int.Parse(args[3]) : 48000;

if (mode == "to-f32")
{
    using var reader = new MediaFoundationReader(inputPath);
    ISampleProvider provider = reader.ToSampleProvider();

    if (provider.WaveFormat.Channels > 1)
        provider = new StereoToMonoSampleProvider(provider);   // 下混为单声道（NAudio 3 里叫这个）

    if (provider.WaveFormat.SampleRate != sampleRate)
        provider = new WdlResamplingSampleProvider(provider, sampleRate);

    var buffer = new float[8192];
    var all = new List<float>();
    int read;
    while ((read = provider.Read(buffer.AsSpan())) > 0)
        all.AddRange(buffer.AsSpan(0, read).ToArray());

    var bytes = new byte[all.Count * 4];
    Buffer.BlockCopy(all.ToArray(), 0, bytes, 0, bytes.Length);
    File.WriteAllBytes(outputPath, bytes);

    Console.WriteLine($"源文件：{reader.WaveFormat.SampleRate} Hz / {reader.WaveFormat.Channels} 声道");
    Console.WriteLine($"to-f32：{all.Count} 个样本（{all.Count / (double)sampleRate:0.00} 秒）→ {outputPath}");
    Console.WriteLine($"        输出格式：{sampleRate} Hz 单声道 Float32，{bytes.Length / 1024.0 / 1024.0:0.00} MB");
    return 0;
}

if (mode == "to-wav")
{
    var bytes = File.ReadAllBytes(inputPath);
    var count = bytes.Length / 4;
    var samples = new float[count];
    Buffer.BlockCopy(bytes, 0, samples, 0, count * 4);

    using (var writer = new WaveFileWriter(outputPath, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1)))
        writer.WriteSamples(samples, 0, samples.Length);

    Console.WriteLine($"to-wav：{count} 个样本（{count / (double)sampleRate:0.00} 秒）→ {outputPath}");
    return 0;
}

Console.WriteLine("未知模式：" + mode);
return 1;
