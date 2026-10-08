using NAudio.Wave;
using MateMic.Dsp;

namespace MateMic.Audio;

/// <summary>
/// 分析采样点：透传数据并把样本交给 <see cref="SpectrumAnalyzer"/>。
/// 只做定长拷贝，无分配、无锁，可安全用于音频线程。
/// </summary>
public sealed class InputTapSource : ISampleProvider
{
    private readonly SpectrumAnalyzer _analyzer;
    private ISampleProvider? _source;

    public InputTapSource(ISampleProvider? source, SpectrumAnalyzer analyzer)
        : this(source?.WaveFormat ?? WaveFormat.CreateIeeeFloatWaveFormat(DefaultSampleRate, 1), analyzer)
    {
        _source = source;
    }

    /// <summary>内部链路固定 48 kHz（与 AudioEngine.SampleRate 相同，这里写常量以免这里反向依赖音频引擎）。</summary>
    private const int DefaultSampleRate = 48000;

    /// <summary>创建不带上游的采样点（由外部把数据直接 Feed 进分析器，例如采集回调）。</summary>
    public InputTapSource(WaveFormat format, SpectrumAnalyzer analyzer)
    {
        WaveFormat = format;
        _analyzer = analyzer;
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>音频流重启时替换上游。</summary>
    public void SetSource(ISampleProvider? source) => _source = source;

    /// <summary>直接把数据送入分析器（采集回调路径）。</summary>
    public void Feed(ReadOnlySpan<float> samples) => _analyzer.Feed(samples);

    public int Read(Span<float> buffer)
    {
        var read = _source?.Read(buffer) ?? 0;
        if (read > 0) _analyzer.Feed(buffer[..read]);
        return read;
    }
}

/// <summary>
/// 最终混音点的采样点：把混音结果复制给监听缓冲区。
/// 输出频谱直接在混音器上取样（<see cref="MicMixer"/> 上游已接分析采样点）。
/// <summary>
/// 立体声（float）→ 单声道（float）。用于把任意声道数的播放器输出统一到处理格式。
/// </summary>
public sealed class StereoToMonoFloatProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _inputChannels;
    private readonly float[] _scratch;

    public StereoToMonoFloatProvider(ISampleProvider source)
    {
        _source = source;
        _inputChannels = Math.Max(1, source.WaveFormat.Channels);
        _scratch = new float[4096 * _inputChannels];
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> buffer)
    {
        if (_inputChannels == 1) return _source.Read(buffer);

        var frames = Math.Min(buffer.Length, _scratch.Length / _inputChannels);
        var read = _source.Read(_scratch.AsSpan(0, frames * _inputChannels));
        var inputFrames = read / _inputChannels;
        for (var f = 0; f < inputFrames; f++)
        {
            var sum = 0f;
            for (var c = 0; c < _inputChannels; c++)
                sum += _scratch[f * _inputChannels + c];
            buffer[f] = sum / _inputChannels;
        }

        return inputFrames;
    }
}
