using System;
using MateMic.Core;

namespace MateMic.Dsp;

/// <summary>
/// 保底降噪（无外部模型时使用）。
///
/// **定位**：保证"总有可用的降噪"，且**绝不破坏音频**。
/// 这是"没有任何可用 ONNX 模型时"的兜底；要更好的抑噪效果请用 GTCRN / DPDFNet。
///
/// 只有两级，都是标准且数值稳定的结构：
///   1. **二阶 Butterworth 高通（120 Hz）**，标准 RBJ 双二阶系数
///   2. **全频带向下扩展器**（dB 域）：低于门限才按 ratio 衰减，高于门限必然直通
///
/// **为什么没有做多频带**：试过 16/32 频带门控，但在低采样率域里
/// 低频带（如 150 Hz）的双二阶极点极度靠近单位圆，数值不稳定，
/// 会把增益算成 NaN 并污染全部输出，同时 CPU 占用高达 80%（单核）。
/// 与其留一个会毁音频的实现，不如只保留能证明稳定的部分。
/// </summary>
public sealed class SpectralDenoiseModel : IDenoiseModel
{
    public const int SampleRate = 48000;
    public const int FrameSize = 480;

    /// <summary>保留旧接口：画像/频点数约定（供训练器与模型目录使用）。</summary>
    public const int FftSize = 960;

    public const int Bins = FftSize / 2 + 1;

    // RBJ 二阶 Butterworth 高通（48 kHz / 120 Hz, Q = 1/√2）
    private const float B0 = 0.987443f;
    private const float B1 = -1.974886f;
    private const float B2 = 0.987443f;
    private const float A1 = -1.974772f;
    private const float A2 = 0.975001f;

    private float _x1;
    private float _x2;
    private float _y1;
    private float _y2;
    private float _envelopeValue;
    private float _gain = 1f;

    public SpectralDenoiseModel(string name = "内置降噪（保底模型）")
    {
        Name = name;
    }

    public string Name { get; }

    public string TensorInfo => "48 kHz / 单声道 / 二阶高通 + 向下扩展（无外部模型）";

    /// <summary>降噪强度 0–100：越大压得越狠。</summary>
    public float Strength { get; set; } = 60f;

    /// <summary>诊断用：当前生效的增益（1 = 完全直通）。</summary>
    public float LastGain => _gain;

    /// <summary>诊断用：当前包络电平（dBFS）。</summary>
    public float LastEnvelopeDb { get; private set; } = -120f;

    /// <summary>诊断用：已处理的帧数。</summary>
    public long Frames { get; private set; }

    public bool ProfileReady => Frames > 20;

    public void Process(Span<float> frame)
    {
        var strength = Math.Clamp(Strength, 0f, 100f) / 100f;

        // 强度 0 → 完全直通
        if (strength <= 0.001f)
        {
            Frames++;
            return;
        }

        // 向下扩展器参数（dB 域）。取向：**语音必须完整通过、底噪才被压**。
        // 门限取在"说话声级"与"环境底噪"之间：典型底噪约 −37 dBFS、说话约 −14 dBFS，
        // 因此门限设在 −42…−28 dBFS 区间。高于门限增益恒为 1，保证不压到人声。
        var thresholdDb = -42f + strength * 14f;   // -42 → -28 dBFS
        var ratio = 1f + strength * 1.5f;          // 1 → 2.5
        var rangeDb = strength * 22f;              // 0 → 22 dB
        var release = 0.0008f + strength * 0.0016f;

        var envelope = _envelopeValue;
        var gain = _gain;

        for (var i = 0; i < frame.Length; i++)
        {
            // 1) 二阶 Butterworth 高通（Direct Form I）
            var x = frame[i];
            var y = B0 * x + B1 * _x1 + B2 * _x2 - A1 * _y1 - A2 * _y2;
            _x2 = _x1;
            _x1 = x;
            _y2 = _y1;
            _y1 = y;

            // 2) 包络跟随（整流 + 快攻慢放）
            var magnitude = MathF.Abs(y);
            envelope += (magnitude - envelope) * (magnitude > envelope ? 0.4f : 0.0015f);

            // 3) dB 域门控
            var envelopeDb = 20f * MathF.Log10(MathF.Max(envelope, 1e-9f));
            var overDb = envelopeDb - thresholdDb;
            var targetGain = overDb >= 0f
                ? 1.0
                : Math.Pow(10.0, Math.Max(overDb * (ratio - 1f), -rangeDb) / 20.0);

            var coefficient = targetGain > gain ? 0.6f : release;
            gain += ((float)targetGain - gain) * coefficient;

            // 防御：任何异常值都不能进入音频
            if (!float.IsFinite(gain)) gain = 1f;

            frame[i] = y * gain;
        }

        _envelopeValue = envelope;
        _gain = gain;
        LastEnvelopeDb = 20f * MathF.Log10(MathF.Max(envelope, 1e-9f));
        Frames++;
    }

    public void Reset()
    {
        _x1 = _x2 = _y1 = _y2 = 0f;
        _envelopeValue = 0f;
        _gain = 1f;
        Frames = 0;
        _profileGains = null;
        LastEnvelopeDb = -120f;
    }

    /// <summary>
    /// 保留旧接口：载入训练产出的噪声画像。
    /// 当前实现不使用逐频点画像，只保存下来供诊断。
    /// </summary>
    public void LoadProfile(float[] gains, string name)
    {
        if (gains == null || gains.Length == 0) return;

        _profileGains = new float[gains.Length];
        Array.Copy(gains, _profileGains, gains.Length);

        var sum = 0f;
        foreach (var g in gains) sum += g;
        Log.Info($"已载入降噪画像「{name}」：{gains.Length} 个频点，均值 {sum / gains.Length:0.000}" +
                 "（保底模型不使用逐频点画像）");
    }

    /// <summary>诊断用：已载入的画像（可能为 null）。</summary>
    public float[]? ProfileGains => _profileGains;

    private float[]? _profileGains;

    public void Dispose()
    {
    }
}
