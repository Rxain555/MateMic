using System.Diagnostics;
using System.IO;
using NAudio.Dsp;
using MateMic.Core;

namespace MateMic.Ai;

/// <summary>
/// AI 变声的推理流水线（把三个模型串起来），逐块转换：
///
///   48 kHz 输入
///     → 降采样到 16 kHz
///     → 48 Hz 高通（RVC 的前置滤波，用两级二阶巴特沃斯近似它的五阶）
///     → 内容编码器（50 fps，768 维）
///     → 基频（CREPE-tiny，10 ms 一帧）+ 无声插值 + 变调 + 粗量化（1..255）
///     → 特征 2 倍线性插值到 100 fps（RVC 的 window=160 就是这个意思）
///     → 音色模型合成（模型自带采样率，如 40 kHz）
///     → 重采样回 48 kHz
///
/// 说明：模型输出的长度会**略短于**输入（编码器有感受野、帧数取整），
/// 这个差额由调用方按实测比例动态补偿（见 <see cref="SamplesPerOutputSample"/>），
/// 而不是靠加静音padding——否则输出会越跑越空。
/// </summary>
public sealed class AiVoiceEngine : IDisposable
{
    private const int EncoderRate = 16000;
    private const float F0Min = 50f;
    private const float F0Max = 1100f;

    private static readonly float F0MelMin = (float)(1127.0 * Math.Log(1 + F0Min / 700.0));
    private static readonly float F0MelMax = (float)(1127.0 * Math.Log(1 + F0Max / 700.0));

    private readonly ContentEncoder _encoder;
    private readonly F0Extractor _f0;
    private readonly RvcSynthesizer _synthesizer;
    private readonly AudioResampler _down;
    private readonly AudioResampler _up;
    private readonly BiQuadFilter _highPass1;
    private readonly BiQuadFilter _highPass2;

    private readonly float[] _audio16;
    private readonly float[] _features;
    private readonly float[] _featuresUp;
    private readonly float[] _f0Track;
    private readonly float[] _pitchf;
    private readonly long[] _pitch;
    private readonly float[] _output;

    private int _lastInputSamples;
    private int _lastOutputSamples = 1;

    private AiVoiceEngine(string name, ContentEncoder encoder, F0Extractor f0, RvcSynthesizer synthesizer, int maxChunkSamples)
    {
        Name = name;
        _encoder = encoder;
        _f0 = f0;
        _synthesizer = synthesizer;

        _down = new AudioResampler(48000, EncoderRate);
        _up = new AudioResampler(synthesizer.SampleRate, 48000);
        _highPass1 = BiQuadFilter.HighPassFilter(EncoderRate, 48f, 0.707f);
        _highPass2 = BiQuadFilter.HighPassFilter(EncoderRate, 48f, 0.707f);

        var samples16 = maxChunkSamples / 3 + 64;
        _audio16 = new float[samples16];
        _features = new float[(samples16 / 320 + 4) * ContentEncoder.FeatureDim];
        _featuresUp = new float[(samples16 / 160 + 8) * ContentEncoder.FeatureDim];
        _f0Track = new float[samples16 / F0Extractor.FrameHop + 4];
        _f0Scratch = new float[samples16 / F0Extractor.FrameHop + 4];
        _pitchf = new float[samples16 / 160 + 8];
        _pitch = new long[samples16 / 160 + 8];
        _output = new float[maxChunkSamples * 2 + 4096];
    }

    private readonly float[] _f0Scratch;
    private readonly Stopwatch _watch = new();

    /// <summary>最近一块的分级耗时（毫秒），供自检与诊断使用。</summary>
    public double LastEncoderMs { get; private set; }
    public double LastF0Ms { get; private set; }
    public double LastSynthMs { get; private set; }

    public string Name { get; }

    /// <summary>音色模型的采样率。</summary>
    public int ModelSampleRate => _synthesizer.SampleRate;

    /// <summary>每产出 1 个输出样本需要吃进多少输入样本（实测比例，用于动态补偿长度差）。</summary>
    public double SamplesPerOutputSample
        => _lastOutputSamples >= 64 ? Math.Clamp((double)_lastInputSamples / _lastOutputSamples, 0.5, 4.0) : 1.0;

    /// <summary>
    /// 从一个组件目录加载引擎：目录里的 ONNX 按"输入输出形状"自动认领角色，
    /// 这样组件的文件组织方式可以变，不需要额外的配置文件。
    /// </summary>
    public static AiVoiceEngine? TryLoad(string directory, bool useGpu, out string message)
    {
        ContentEncoder? encoder = null;
        F0Extractor? f0 = null;
        RvcSynthesizer? synthesizer = null;

        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.onnx", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(path);
                if (synthesizer == null && LooksLikeSynthesizer(path))
                {
                    synthesizer = new RvcSynthesizer(path, useGpu);
                    continue;
                }

                if (f0 == null && LooksLikeF0(path))
                {
                    f0 = new F0Extractor(path, 0.5f, useGpu);
                    continue;
                }

                if (encoder == null && LooksLikeEncoder(path))
                {
                    encoder = new ContentEncoder(path, useGpu);
                    continue;
                }

                Log.Info($"AI：{name} 不是引擎需要的模型，跳过");
            }
        }
        catch (Exception ex)
        {
            Log.Error("AI：加载引擎模型失败", ex);
            message = ex.Message;
            encoder?.Dispose();
            f0?.Dispose();
            synthesizer?.Dispose();
            return null;
        }

        if (encoder == null || f0 == null || synthesizer == null)
        {
            message = $"组件里缺少模型（编码器 {encoder != null}，基频 {f0 != null}，音色 {synthesizer != null}）";
            encoder?.Dispose();
            f0?.Dispose();
            synthesizer?.Dispose();
            return null;
        }

        message = "引擎就绪";
        return new AiVoiceEngine(Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)), encoder, f0, synthesizer, 48000);
    }

    private static bool LooksLikeEncoder(string path)
    {
        using var session = OnnxSessionFactory.Create(path, false);
        return session.OutputMetadata.Values.Any(o =>
            o.Dimensions.Length == 3 && (o.Dimensions[^1] == 768 || o.Dimensions[^1] == 256));
    }

    private static bool LooksLikeF0(string path)
    {
        using var session = OnnxSessionFactory.Create(path, false);
        return session.InputMetadata.Values.Any(i =>
                   i.Dimensions.Length == 2 && i.Dimensions[^1] == 1024)
               || session.OutputMetadata.Values.Any(o =>
                   o.Dimensions.Length == 2 && o.Dimensions[^1] == 360);
    }

    private static bool LooksLikeSynthesizer(string path)
    {
        using var session = OnnxSessionFactory.Create(path, false);
        return session.InputMetadata.Keys.Any(k => k.Equals("feats", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 转换一块 48 kHz 单声道音频。返回内部缓冲（调用方需立即拷贝），
    /// 实际样本数由 <paramref name="produced"/> 给出。
    /// </summary>
    public float[] Convert(ReadOnlySpan<float> audio48, float semitones, out int produced)
    {
        // 注意：比例只在"真正产出"之后更新，否则产出为 0 的块会让比例退化成天文数字

        // 1) 48k → 16k
        var count16 = _down.Process(audio48, _audio16);
        if (count16 < 1024)
        {
            produced = 0;
            _lastOutputSamples = 1;
            return _output;
        }

        // 2) 前置高通
        for (var i = 0; i < count16; i++)
        {
            var value = _audio16[i];
            value = _highPass1.Transform(value);
            value = _highPass2.Transform(value);
            _audio16[i] = value;
        }

        // 3) 内容特征（50 fps）
        _watch.Restart();
        var frames = _encoder.Encode(_audio16.AsSpan(0, count16), _features);
        LastEncoderMs = _watch.Elapsed.TotalMilliseconds;
        if (frames <= 0)
        {
            produced = 0;
            _lastOutputSamples = 1;
            return _output;
        }

        // 4) 基频（100 fps）
        _watch.Restart();
        var f0Frames = _f0.Extract(_audio16.AsSpan(0, count16), _f0Track);
        LastF0Ms = _watch.Elapsed.TotalMilliseconds;

        // 5) 无声插值（RVC 的做法）后变调，再粗量化
        FillUnvoiced(_f0Track, f0Frames);
        var transpose = (float)Math.Pow(2.0, semitones / 12.0);
        for (var i = 0; i < f0Frames; i++)
        {
            var hz = _f0Track[i] * transpose;
            _pitchf[i] = hz;
            _pitch[i] = CoarsePitch(hz);
        }

        // 6) 特征 2 倍线性插值（50 → 100 fps）
        var framesUp = 0;
        for (var t = 0; t < frames; t++)
        {
            var next = t + 1 < frames ? t + 1 : t;
            for (var d = 0; d < ContentEncoder.FeatureDim; d++)
            {
                var a = _features[t * ContentEncoder.FeatureDim + d];
                var b = _features[next * ContentEncoder.FeatureDim + d];
                _featuresUp[framesUp * ContentEncoder.FeatureDim + d] = a;
                _featuresUp[(framesUp + 1) * ContentEncoder.FeatureDim + d] = (a + b) * 0.5f;
            }
            framesUp += 2;
        }

        // 7) 帧数取两者较小值（RVC 也是这么做）
        var total = Math.Min(framesUp, f0Frames);
        if (total <= 4)
        {
            produced = 0;
            _lastOutputSamples = 1;
            return _output;
        }

        // 8) 合成（模型采样率）
        _watch.Restart();
        var audio = _synthesizer.Synthesize(_featuresUp, total, _pitch, _pitchf);
        LastSynthMs = _watch.Elapsed.TotalMilliseconds;

        // 9) 模型采样率 → 48k
        var count48 = _up.Process(audio, _output);
        produced = count48;
        _lastOutputSamples = Math.Max(1, count48);
        return _output;
    }

    /// <summary>把无声处（0）用邻近有声值线性插值补上——RVC 在 pitch 处理前就是这么做的。</summary>
    private static void FillUnvoiced(float[] f0, int count)
    {
        var firstVoiced = -1;
        for (var i = 0; i < count; i++)
        {
            if (f0[i] > 0)
            {
                firstVoiced = i;
                break;
            }
        }

        if (firstVoiced < 0)
        {
            Array.Clear(f0, 0, count);
            return;
        }

        for (var i = 0; i < firstVoiced; i++) f0[i] = f0[firstVoiced];

        var previous = firstVoiced;
        for (var i = firstVoiced + 1; i < count; i++)
        {
            if (f0[i] > 0)
            {
                if (i - previous > 1)
                {
                    var step = (f0[i] - f0[previous]) / (i - previous);
                    for (var k = previous + 1; k < i; k++) f0[k] = f0[previous] + step * (k - previous);
                }
                previous = i;
            }
        }

        for (var i = previous + 1; i < count; i++) f0[i] = f0[previous];
    }

    /// <summary>RVC 的粗量化：mel 域线性映射到 1..255。</summary>
    private static long CoarsePitch(float hz)
    {
        if (hz <= 0) return 1;
        var mel = (float)(1127.0 * Math.Log(1 + hz / 700.0));
        var scaled = (mel - F0MelMin) * 254f / (F0MelMax - F0MelMin) + 1f;
        if (scaled <= 1f) return 1;
        if (scaled > 255f) return 255;
        return (long)Math.Round(scaled);
    }

    public void Reset()
    {
        _down.Reset();
        _up.Reset();
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _f0.Dispose();
        _synthesizer.Dispose();
    }
}
