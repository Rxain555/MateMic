using System;

namespace MicMate.Dsp;

/// <summary>
/// 炸麦（劣质对讲机）。
///
/// 按使用需求设计的听感链条：
///   1. **强压缩**（高比率 + 快攻击）——响度大时被"压扁"，
///      增益骤降产生**呼呼的风声/抽气感**，这正是劣质对讲机的特征
///   2. **硬削波**——过载失真
///   3. **带通**（300 Hz 高通 + 3 kHz 低通）——对讲机的窄频响，"闷且薄"
///   4. **量化降位**——廉价 ADC 毛刺
///   5. **嘶嘶背景噪音，只在有人声时出现**——没人声时完全安静
///
/// 压缩器递推式（避免逐样本算 pow，效率高且数值稳定）：
///   设输入电平 x ≥ 0，压缩曲线 y = x^(1/ratio)，
///   则增益 g = y/x = x^(1/ratio − 1)，可写成 g = exp(k·ln x)，k = 1/ratio − 1。
///   对 ln x 做快攻慢放平滑，再取指数得到增益。
/// </summary>
public sealed class MegaphoneDistortionEffect
{
    /// <summary>压缩比率：越大压得越狠（劣质对讲机取 8–20）。</summary>
    public float CompressorRatio { get; set; } = 12f;

    /// <summary>压缩门限（dBFS）：超过它才进入压缩。</summary>
    public float CompressorThresholdDb { get; set; } = -24f;

    /// <summary>硬削波前的推动增益（dB）。</summary>
    public float DriveDb { get; set; } = 14f;

    /// <summary>量化位深，越低越脏。</summary>
    public int BitDepth { get; set; } = 7;

    /// <summary>补偿增益（dB）：抵消压缩带来的整体电平下降。</summary>
    public float MakeupGainDb { get; set; } = 8f;

    /// <summary>干湿混合 0–1。</summary>
    public float Mix { get; set; } = 1f;

    /// <summary>背景嘶嘶噪音量 0–1（仅在有声时出现）。</summary>
    public float NoiseAmount { get; set; } = 0.5f;

    /// <summary>
    /// "火箭发射"呼呼风声的强度 0–1。
    /// 说话时才出现：带通噪声 + 慢速 LFO 调制，模拟因响度过大被压制的风声。
    /// </summary>
    public float RoarAmount { get; set; } = 0.5f;

    /// <summary>带通下限（Hz）：对讲机的低频截止。</summary>
    public float HighPassHz { get; set; } = 300f;

    /// <summary>带通上限（Hz）：对讲机的高频截止。</summary>
    public float LowPassHz { get; set; } = 3000f;

    // 压缩器状态（在 ln 域平滑）
    private float _logEnvelope = -30f;
    private float _attackCoefficient = 0.6f;
    private float _releaseCoefficient = 0.002f;

    // 带通滤波状态
    private float _highPassState;
    private float _lowPassState1;
    private float _lowPassState2;

    // 有人声检测（用于噪音门控）
    private float _voiceEnvelope;
    private float _noiseGate;

    // "火箭发射"风声的滤波与调制状态
    private float _roarBand1;
    private float _roarBand2;
    private float _roarPhase;

    private readonly Random _random = new(20260922);

    public void ProcessInPlace(Span<float> buffer, int sampleRate)
    {
        var ratio = Math.Clamp(CompressorRatio, 1.5f, 40f);
        var threshold = MathF.Pow(10f, Math.Clamp(CompressorThresholdDb, -70f, 0f) / 20f);
        var drive = MathF.Pow(10f, Math.Clamp(DriveDb, 0f, 30f) / 20f);
        var makeup = MathF.Pow(10f, Math.Clamp(MakeupGainDb, -10f, 24f) / 20f);
        var mix = Math.Clamp(Mix, 0f, 1f);
        var dry = 1f - mix;
        var levels = Math.Max(2, Math.Min(32, BitDepth));
        var step = 2f / (1 << (levels - 1));
        var noise = Math.Clamp(NoiseAmount, 0f, 1f);

        // 压缩曲线指数：g = x^k（x 为门限归一化后的电平，<1）
        var k = 1f / ratio - 1f;   // 负值

        var lowPassCoefficient = 1f - MathF.Exp(-2f * MathF.PI * Math.Clamp(LowPassHz, 400f, 12000f) / sampleRate);
        var highPassCoefficient = 1f - MathF.Exp(-2f * MathF.PI * Math.Clamp(HighPassHz, 30f, 2000f) / sampleRate);

        // 风声用的带通（中心约 260 Hz）与 LFO（约 0.7 Hz）
        var roarLowCoefficient = 1f - MathF.Exp(-2f * MathF.PI * 260f / sampleRate);
        var roarLfoIncrement = 2f * MathF.PI * 0.7f / sampleRate;

        for (var i = 0; i < buffer.Length; i++)
        {
            var input = buffer[i];

            // ---------- 1) 压缩器 ----------
            // 输入电平（加小偏置避免 log(0)）
            var level = MathF.Abs(input) + 1e-9f;
            var logLevel = MathF.Log(level);

            // 快攻慢放：增益下降快（压住峰值）、恢复慢（产生"呼呼"的风声感）
            var coefficient = logLevel < _logEnvelope ? _attackCoefficient : _releaseCoefficient;
            _logEnvelope += (logLevel - _logEnvelope) * coefficient;

            var envelope = MathF.Exp(_logEnvelope);
            var gain = 1f;
            if (envelope > threshold)
            {
                // 门限归一化后的超额部分按比率压缩
                var overshoot = envelope / threshold;
                gain = MathF.Pow(overshoot, k);
            }

            // 防御：任何异常值都不能进入音频
            if (!float.IsFinite(gain)) gain = 1f;

            var compressed = input * gain * makeup;

            // ---------- 2) 硬削波 ----------
            var driven = Math.Clamp(compressed * drive, -1f, 1f);

            // ---------- 3) 带通（高通 → 二阶低通）----------
            _highPassState += (driven - _highPassState) * highPassCoefficient;
            var highPassed = driven - _highPassState;

            _lowPassState1 += (highPassed - _lowPassState1) * lowPassCoefficient;
            _lowPassState2 += (_lowPassState1 - _lowPassState2) * lowPassCoefficient;
            var banded = _lowPassState2;

            // ---------- 4) 量化降位 ----------
            banded = MathF.Round(banded / step) * step;

            // ---------- 5) 人声门控的背景噪音 ----------
            // 用**输入**电平判断有没有人说话：没人声时完全不加噪音。
            var voiceMagnitude = MathF.Abs(input);
            _voiceEnvelope += (voiceMagnitude - _voiceEnvelope) * (voiceMagnitude > _voiceEnvelope ? 0.05f : 0.0015f);

            // 有人声 → 门开（约 6 dB 信噪比以上）；安静 → 门关
            var voiceOpen = _voiceEnvelope > 0.008f ? 1f : 0f;
            _noiseGate += (voiceOpen - _noiseGate) * (voiceOpen > _noiseGate ? 0.08f : 0.01f);

            if (noise > 0.001f && _noiseGate > 0.001f)
            {
                // 带通后的"对讲机底噪"：以高频为主的窄带嘶声
                var hiss = (float)(_random.NextDouble() - 0.5) * 2f;
                banded += hiss * 0.06f * noise * _noiseGate;
            }

            // ---------- 6) 说话时的"火箭发射"呼呼风声 ----------
            // 做法：白噪声 → 带通（约 260 Hz，高 Q）→ 慢速 LFO 调制幅度，
            // 再按人声包络驱动。低频轰鸣 + 缓慢起伏 = 火箭发射的呼呼声。
            var roar = 0f;
            if (RoarAmount > 0.001f && _noiseGate > 0.001f)
            {
                var white = (float)(_random.NextDouble() - 0.5) * 2f;

                // 两级带通（串联提高 Q），中心约 260 Hz，产生有音高的轰鸣
                _roarBand1 += (white - _roarBand1) * roarLowCoefficient;
                var roarHigh1 = white - _roarBand1;
                _roarBand2 += (roarHigh1 - _roarBand2) * roarLowCoefficient;
                var roarBand = roarHigh1 - _roarBand2;

                // 慢速 LFO（约 0.7 Hz）+ 二次谐波，做出"火焰忽强忽弱"的起伏
                _roarPhase += roarLfoIncrement;
                if (_roarPhase > MathF.PI * 2f) _roarPhase -= MathF.PI * 2f;
                var lfo = 0.65f + 0.35f * MathF.Sin(_roarPhase)
                                + 0.15f * MathF.Sin(_roarPhase * 2.37f);

                // 人声越响，风声越大；没人声时 _noiseGate 为 0，完全安静
                var roarDrive = MathF.Min(1f, _voiceEnvelope * 6f);
                roar = roarBand * lfo * RoarAmount * roarDrive * _noiseGate;
            }

            buffer[i] = input * dry + Math.Clamp(banded + roar, -1f, 1f) * mix;
        }
    }

    public void Reset()
    {
        _logEnvelope = -30f;
        _highPassState = 0f;
        _lowPassState1 = 0f;
        _lowPassState2 = 0f;
        _voiceEnvelope = 0f;
        _noiseGate = 0f;
        _roarBand1 = 0f;
        _roarBand2 = 0f;
        _roarPhase = 0f;
    }
}
