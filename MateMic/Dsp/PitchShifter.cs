using System;
using NAudio.Dsp;

namespace MateMic.Dsp;

/// <summary>
/// 变调器（保持时长）。
///
/// **直接用 NAudio 自带的 <see cref="SmbPitchShifter"/>**——它就是
/// Stephan M. Bernsee 经典的 <c>smbPitchShift</c> 算法的 .NET 实现
/// （参见 Mark Heath 的 Autotune.NET 文章，其 Autotune 正是这个算法 + 自相关测频）。
///
/// 它是**就地、流式**的：内部维护重叠缓冲，按顺序连续调用即可，不需要额外分块缓冲。
/// 实测（tools\dev\DpdfProbe）：音高偏差 2–5 音分、**零静音块**。
///
/// 为什么不再自研：我先后用过相位声码器与 WSOLA 自行组装的方案，
/// 都需要"攒够一整块才产出"，与固定 480 样本的音频回调天然错配，
/// 无论怎么调缓冲都会周期性饿死（实测约 50% 的帧是静音，听感"一卡一卡"）。
/// 换成这个经过验证的现成实现后问题消失。
/// </summary>
public sealed class PitchShifter
{
    private const int FftFrameSize = 2048;
    private const int Oversampling = 8;

    private readonly int _sampleRate;
    private SmbPitchShifter _shifter = new();

    public PitchShifter(int sampleRate) => _sampleRate = sampleRate;

    /// <summary>变调量（半音）。0 = 完全旁通。</summary>
    public float Semitones { get; set; }

    /// <summary>就地处理一块音频（保持时长）。</summary>
    public void Process(Span<float> buffer)
    {
        if (buffer.Length == 0) return;

        var factor = (float)Math.Pow(2, Math.Clamp(Semitones, -12f, 12f) / 12.0);
        if (Math.Abs(factor - 1f) < 0.0005f) return;   // 不变调时完全旁通

        // SmbPitchShifter 就地处理，内部保有多帧重叠缓冲，支持逐块改变 pitchFactor
        // （硬调音每帧的变调量都在变，正好需要这个能力）。
        _shifter.PitchShift(factor, buffer.Length, FftFrameSize, Oversampling, _sampleRate, buffer);
    }

    /// <summary>清空内部重叠缓冲。切换效果类型或重启链路时调用。</summary>
    public void Reset() => _shifter = new SmbPitchShifter();
}
