using MicMate.Core;
using MicMate.Dsp;
using NAudio.Wave;

namespace MicMate.Audio;

/// <summary>
/// 输出总线：混音结果的唯一取样点。
///
/// 设计要点（这里踩过坑）：主输出与监听输出**共用同一个取样点**，
/// 采样点本身不再是任何一条输出链的一部分（无源），因此不会出现
/// 「把取样点的上游换成监听链的限幅器 → 主输出跟着监听链一起没人拉取 → 整条链路断流」。
///
/// 数据流：
///   麦克风+播放器 → MicMixer ─┬→ 输出频谱取样
///                            ├→ 软限幅 ─→ 单声道转立体声 ─→ 主输出播放器（主动拉取，驱动整条链）
///                            └→ 复制到监听环形缓冲 ─→ 监听播放器（独立拉取）
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

    /// <summary>监听缓冲区；为 null 表示监听关闭（此时不再复制，避免缓冲堆积）。</summary>
    public volatile BufferedWaveProvider? MonitorBuffer;

    /// <summary>被拉取的次数（诊断用）。</summary>
    public long ReadCount => Interlocked.Read(ref _readCount);

    private long _readCount;

    /// <summary>
    /// 由主输出链调用：真正读取混音器并取样。
    /// 监听输出不直接读这里，而是读它写好的环形缓冲。
    /// </summary>
    public int Read(Span<float> buffer)
    {
        Interlocked.Increment(ref _readCount);

        var read = _mixer.Read(buffer);
        if (read <= 0) return read;

        var slice = buffer[..read];
        _analyzer.Feed(slice);

        var monitor = MonitorBuffer;
        if (monitor != null)
        {
            try
            {
                monitor.AddSamples(System.Runtime.InteropServices.MemoryMarshal.AsBytes(slice));
            }
            catch (Exception ex)
            {
                Log.Debug("写入监听缓冲失败：" + ex.Message);
            }
        }

        return read;
    }
}
