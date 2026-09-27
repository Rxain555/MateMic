using NAudio.Wave;

namespace MateMic.Dsp;

/// <summary>
/// 处理链中的单个模块。所有模块在处理链中均为单声道 32-bit float / 48 kHz。
/// 未启用的模块会被引擎从处理链中物理移除，而不是简单跳过。
/// </summary>
public interface IAudioEffect : ISampleProvider
{
    string Name { get; }

    bool Enabled { get; set; }

    /// <summary>把 UI 上的参数写回 DSP 状态。调用点通常在 UI 线程。</summary>
    void UpdateParameters();
}

/// <summary>
/// 可在线重建的处理链。Add/Remove 在控制线程发布一个新的不可变数组（单次原子写），
/// 音频线程的 Read 永远看到“完整的旧链”或“完整的新链”，且本身不加锁、不分配。
/// </summary>
public sealed class DynamicChain : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly float[] _scratch;
    private volatile IAudioEffect[] _effects = Array.Empty<IAudioEffect>();

    public DynamicChain(ISampleProvider source, int maxBlockSamples = 8192)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
        _scratch = new float[maxBlockSamples];
    }

    public WaveFormat WaveFormat { get; }

    public IReadOnlyList<IAudioEffect> Effects => _effects;

    public void SetEffects(IEnumerable<IAudioEffect> effects)
        => _effects = effects.ToArray();

    public int Read(Span<float> buffer)
    {
        var read = _source.Read(buffer);
        if (read <= 0) return read;

        var effects = _effects;
        if (effects.Length == 0) return read;

        var current = buffer[..read];
        var limit = Math.Min(current.Length, _scratch.Length);
        current[..limit].CopyTo(_scratch);

        foreach (var effect in effects)
        {
            try
            {
                effect.Read(_scratch.AsSpan(0, limit));
            }
            catch (Exception ex)
            {
                // 单个模块异常不得中断整条链路：跳过该模块，其余照常输出
                Core.Log.Error($"效果模块 {effect.Name} 处理失败，已跳过", ex);
            }
        }

        _scratch.AsSpan(0, limit).CopyTo(current);
        return read;
    }

    public void Reset()
    {
        foreach (var effect in _effects)
        {
            switch (effect)
            {
                case NAudio.Effects.AudioEffect native:
                    native.Reset();
                    break;
                case LoudnessBalanceEffect _:
                    break;
            }
        }
    }
}
