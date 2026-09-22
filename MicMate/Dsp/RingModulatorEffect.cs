using NAudio.Wave;

namespace MicMate.Dsp;

/// <summary>
/// 环形调制（Ring Modulation）：把输入乘以一个正弦载波，产生经典的"电音 / 机器人"金属音色。
/// 这是电音效果的核心——单纯变调听起来只是音高变了，缺少那种非人声的机械感。
/// 纯 C# 实现，音频线程无分配。
/// </summary>
public sealed class RingModulatorEffect
{
    /// <summary>载波频率（Hz）。人声用 50–150 Hz 效果最明显。</summary>
    public float Frequency { get; set; } = 85f;

    /// <summary>调制深度 0–1。</summary>
    public float Depth { get; set; } = 0.85f;

    /// <summary>干湿混合 0–1。</summary>
    public float Mix { get; set; } = 0.8f;

    /// <summary>载波频率是否跟随输入幅度轻微漂移，避免听起来过于死板。</summary>
    public bool TrackInput { get; set; } = true;

    private double _phase;

    /// <summary>就地处理（不需要上游源：调用方已经把数据放在 buffer 里）。</summary>
    public void ProcessInPlace(Span<float> buffer, int sampleRate)
    {
        var mix = Math.Clamp(Mix, 0f, 1f);
        var dry = 1f - mix;
        var depth = Math.Clamp(Depth, 0f, 1f);

        for (var i = 0; i < buffer.Length; i++)
        {
            var input = buffer[i];

            // 载波随输入幅度轻微漂移，机械感更自然、也更容易听出来
            var frequency = Frequency;
            if (TrackInput) frequency *= 1f + Math.Clamp(MathF.Abs(input) * 4f, 0f, 0.35f);

            _phase += frequency / sampleRate;
            if (_phase >= 1.0) _phase -= 1.0;

            var carrier = MathF.Sin((float)(_phase * 2.0 * Math.PI));
            var wet = input * ((1f - depth) + depth * carrier);
            buffer[i] = input * dry + wet * mix;
        }
    }

    public void Reset() => _phase = 0;
}
