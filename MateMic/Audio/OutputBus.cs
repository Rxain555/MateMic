using MateMic.Core;
using MateMic.Dsp;
using NAudio.Wave;

namespace MateMic.Audio;

/// <summary>
/// 输出总线：混音结果的唯一取样点。
///
/// 设计要点（这里踩过坑）：主输出与监听输出**共用同一个取样点**，
/// 采样点本身不再是任何一条输出链的一部分（无源），因此不会出现
/// 「把取样点的上游换成监听链的限幅器 → 主输出跟着监听链一起没人拉取 → 整条链路断流」。
///
/// 数据流：
///   麦克风+播放器 → MicMixer ─┬→ 输出频谱取样（本类）
///                            ├→ 软限幅 ─→ 单声道转立体声 ─→ 主输出播放器（主动拉取，驱动整条链）
///                            └→ 监听混音（独立比例，由 MicMixer 的 MonitorSink 复制）
///                                 → 监听环形缓冲 → 监听播放器（独立拉取）
///
/// 注意：监听缓冲的写入**不在**这里，而在 <see cref="Dsp.MicMixer"/>：
/// 监听需要自己的一套混音比例（麦克风受总开关控制、播放器只受「送主输出」控制），
/// 而播放器环形缓冲只能被消费一次，所以监听必须复用主输出这次拉取读到的播放器样本。
/// </summary>
public sealed class OutputBus : ISampleProvider
{
    private readonly MicMixer _mixer;
    private readonly SpectrumAnalyzer _analyzer;

    public OutputBus(MicMixer mixer, SpectrumAnalyzer analyzer)
    {
        _mixer = mixer;
        _analyzer = analyzer;
        WaveFormat = mixer.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>被拉取的次数（诊断用）。</summary>
    public long ReadCount => Interlocked.Read(ref _readCount);

    private long _readCount;

    /// <summary>
    /// 由主输出链调用：真正读取混音器并取样。
    /// 监听输出不直接读这里，而是读 MicMixer 写好的环形缓冲。
    /// </summary>
    public int Read(Span<float> buffer)
    {
        Interlocked.Increment(ref _readCount);

        var read = _mixer.Read(buffer);
        if (read <= 0) return read;

        _analyzer.Feed(buffer[..read]);
        return read;
    }
}

/// <summary>
/// 把 <see cref="Dsp.MicMixer"/> 送出的监听混音写进监听环形缓冲。
/// 缓冲区满时丢弃最旧的数据（<c>DiscardOnBufferOverflow</c>），只影响监听这一路，
/// 主输出不受任何影响。
/// </summary>
public sealed class MonitorBufferSink : MicMixer.IMonitorSink
{
    private readonly BufferedWaveProvider _buffer;

    public MonitorBufferSink(BufferedWaveProvider buffer) => _buffer = buffer;

    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return;

        try
        {
            _buffer.AddSamples(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples));
        }
        catch (Exception ex)
        {
            Log.Debug("写入监听缓冲失败：" + ex.Message);
        }
    }
}
