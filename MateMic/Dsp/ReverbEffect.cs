using MateMic.Core;
using NAudio.Wave;

namespace MateMic.Dsp;

/// <summary>
/// 「混响」模块 —— 从「效果器」里拆出来的独立卡片。
///
/// 为什么要拆：效果器的七个预设是**单选**的（延迟 / 和声 / 电话 / 颤音 / 电音 / 炸麦），
/// 同一时间只有一个生效。而混响最常见的用法恰恰是"**和别的效果叠加**、再把空间感单独调"，
/// 塞在单选里做不到（用户 2026-11-03 提出）。
///
/// ## 干湿混合由本类自己完成（不用 <c>ReverbEffect.Mix</c>）
///
/// 原因是**预延迟**：预延迟的语义是"湿信号延迟、干信号不延迟"。
/// 而 NAudio 的 <c>ReverbEffect</c> 内部就把干湿混在一起了（`Mix`），
/// 若直接在它的输入端延迟，干声也会被一起延迟 —— 那就变成"整体延迟"，
/// 不是混响的预延迟。所以这里：
///   · 让内部的 <c>ReverbEffect</c> 只输出**纯湿声**（`Mix = 1`）；
///   · 送进去的输入**先过一条 N 毫秒的延迟线**（N = 预延迟）；
///   · 输出 = 干声 ×(1−mix) + 湿声 ×mix，干声走的是**未延迟**的那一份。
///
/// 参数语义（界面是 0~100 的百分比，NAudio 要 0~1）：
///   · <b>干湿比</b> Mix —— 混响声占多少；
///   · <b>房间大小</b> RoomSize —— 尾音能拖多久（Freeverb 里它就是反馈系数，
///     也就是"衰减时间"，所以**不另设衰减时间参数**，否则两个滑条控制同一件事）；
///   · <b>阻尼</b> Damping —— 高频衰减快慢，越大越"闷"；
///   · <b>预延迟</b> PreDelayMs —— 湿信号相对干声的延迟（见上）；
///   · Width 固定 1（单声道链路里没有左右宽度可言）。
/// </summary>
public sealed class ReverbEffect : IAudioEffect
{
    /// <summary>预延迟的上限（毫秒）。再长就会明显拖慢"说话与回声的对应关系"。</summary>
    public const int MaxPreDelayMs = 100;

    private readonly NAudio.Effects.ReverbEffect _reverb;
    private readonly ReverbSettings _settings;

    /// <summary>预延迟用的环形缓冲（干声副本，供混响消费）。按最大预延迟一次性分配。</summary>
    private readonly float[] _delayLine;
    private int _delayWrite;

    /// <summary>干声暂存（本类的干湿混合要用未延迟的干声）。</summary>
    private readonly float[] _dry = new float[8192];

    /// <summary>构造时按最大 100 ms 分配，运行时只改读写距离，不做任何分配。</summary>
    public ReverbEffect(WaveFormat format, ReverbSettings settings)
    {
        WaveFormat = format;
        _settings = settings;

        // ReverbEffect 只当"纯湿声发生器"：干湿混合在下面自己做。
        _reverb = new NAudio.Effects.ReverbEffect { Width = 1f, Mix = 1f };
        _reverb.Configure(format);

        var maxDelaySamples = Math.Max(1, format.SampleRate * MaxPreDelayMs / 1000);
        _delayLine = new float[maxDelaySamples];

        UpdateParameters();
    }

    public string Name => "混响";

    public bool Enabled { get; set; }

    public WaveFormat WaveFormat { get; }

    public void UpdateParameters()
    {
        var mix = Math.Clamp(_settings.Mix, 0f, 100f) / 100f;
        var room = Math.Clamp(_settings.RoomSize, 0f, 100f) / 100f;
        var damping = Math.Clamp(_settings.Damping, 0f, 100f) / 100f;

        _mix = mix;

        // RoomSize 取 0.2~1.0：太小会变成"短促的金属声"，没有混响该有的空间感。
        _reverb.RoomSize = 0.2f + room * 0.8f;
        _reverb.Damping = damping;
        _reverb.Width = 1f;
        _reverb.Mix = 1f;   // 始终纯湿声，干湿在 Read 里按 _mix 混

        var preDelayMs = Math.Clamp(_settings.PreDelayMs, 0f, MaxPreDelayMs);
        _preDelaySamples = (int)MathF.Round(preDelayMs * WaveFormat.SampleRate / 1000f);
        if (_preDelaySamples > _delayLine.Length) _preDelaySamples = _delayLine.Length;
    }

    /// <summary>干湿比（0~1），由 UpdateParameters 从设置里算出。</summary>
    private float _mix = 0.3f;

    /// <summary>预延迟的样本数（运行时可变，读写据此保持距离）。</summary>
    private int _preDelaySamples;

    public int Read(Span<float> buffer)
    {
        if (!Enabled) return buffer.Length;

        var count = Math.Min(buffer.Length, _dry.Length);

        // 1) 干声留一份（混响会就地改写传入的缓冲，必须留原样给干声用）
        buffer[..count].CopyTo(_dry);

        // 2) 写延迟线并取出"预延迟之后"的副本，作为混响的输入
        var delayLength = _delayLine.Length;
        for (var i = 0; i < count; i++)
        {
            _delayLine[_delayWrite] = _dry[i];
            _delayWrite = (_delayWrite + 1) % delayLength;
        }

        // 读指针 = 写入指针 - 预延迟（回绕）
        var readIndex = _delayWrite - _preDelaySamples;
        if (readIndex < 0) readIndex += delayLength;

        for (var i = 0; i < count; i++)
        {
            buffer[i] = _delayLine[readIndex];
            readIndex++;
            if (readIndex >= delayLength) readIndex = 0;
        }

        // 3) 把"延迟后的输入"变成纯湿声
        _reverb.Process(buffer[..count]);

        // 4) 干湿混合：干声用未延迟的那一份
        var mix = _mix;
        var dryGain = 1f - mix;
        for (var i = 0; i < count; i++)
            buffer[i] = _dry[i] * dryGain + buffer[i] * mix;

        return buffer.Length;
    }
}
