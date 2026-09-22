using System;

namespace MicMate.Dsp;

public enum TuneScale
{
    /// <summary>自然大调（最常用，适合大多数歌曲）。</summary>
    Major = 0,

    /// <summary>自然小调（伤感/流行常见）。</summary>
    Minor = 1,

    /// <summary>五声音阶（中式/民谣，吸附感更平顺）。</summary>
    Pentatonic = 2,

    /// <summary>半音阶 = 不吸附（仅做电子化音染）。</summary>
    Chromatic = 3,
}

/// <summary>
/// 硬调音（Hard-tune / 电音）：唱歌时那种"电子化人声"。
///
/// ⚠️ 当前状态：**未完成，默认不启用变调**。
/// 基频检测（<see cref="PitchDetector"/>）与音阶吸附已正确工作（自检可验证），
/// 但相位声码器变调内核（<see cref="Denoise.PhaseVocoderStretcher"/>）尚未调通，
/// 因此这里只在检测/吸附层工作，不施加音高移动——避免产出错误音高。
/// 详见 README「频谱域与电音模块进展」。
/// </summary>
public sealed class HardTuneEffect
{
    private readonly int _sampleRate;
    private readonly PitchDetector _detector;
    private readonly float[] _analysisRing;
    private readonly float[] _analysisWindow;
    private int _analysisWrite;
    private readonly float[] _dryBuffer;

    private readonly PitchShifter? _shifter;
    private NAudio.Effects.Equalizer? _timbre;
    private int _pitchNumber = -1;
    private double _appliedSemitones;

    /// <summary>调音目标音高（MIDI 编号）。由**未变调的参考信号**测得，
    /// 因此不会出现"输出又被重新检测 → 继续移动"的累积漂移。</summary>
    private double _targetPitch;

    /// <summary>最近一次检测到的基频（Hz，0 = 未检测到）。诊断用。</summary>
    public float LastDetectedFrequency { get; private set; }

    /// <summary>最近一次实际施加的变调量（半音）。诊断用。</summary>
    public float LastAppliedSemitones => (float)_appliedSemitones;

    /// <summary>最近一次的目标音高（MIDI）。诊断用。</summary>
    public double LastTargetMidi => _targetPitch;

    public HardTuneEffect(int sampleRate, int frameSize = 480)
    {
        _sampleRate = sampleRate;
        _detector = new PitchDetector(sampleRate, windowSize: 1024);
        _analysisRing = new float[_detector.WindowSize];
        _analysisWindow = new float[_detector.WindowSize];
        _dryBuffer = new float[frameSize * 8];
        _shifter = new PitchShifter(sampleRate);
    }

    /// <summary>是否施加音高移动。内核未调通前保持 false（只做检测与音染）。</summary>
    public bool EnablePitchShift { get; set; } = true;

    /// <summary>诊断用：是否施加电子化音染（EQ）。</summary>
    public bool EnableTimbre { get; set; } = true;

    /// <summary>调式根音（MIDI 音高，60 = 中央 C）。</summary>
    public int RootMidiNote { get; set; } = 60;

    public TuneScale Scale { get; set; } = TuneScale.Major;

    /// <summary>吸附速度 0–100：越大越"电"（越小越像自然修音）。</summary>
    public float RetuneSpeed { get; set; } = 90f;

    /// <summary>八度/五度共鸣强度 0–1。</summary>
    public float HarmonyAmount { get; set; } = 0.25f;

    public void Configure(NAudio.Wave.WaveFormat format)
    {
        _timbre = new NAudio.Effects.Equalizer(new[]
        {
            NAudio.Effects.EqualizerBand.Peaking(2600f, 1.2f, 4f),
            NAudio.Effects.EqualizerBand.HighShelf(6000f, 3f),
        });
        _timbre.Configure(format);
    }

    /// <summary>
    /// 就地处理一块音频。
    ///
    /// 内部按 <see cref="_dryBuffer"/> 的容量分块：设备周期可能大于该容量（例如 4096），
    /// 早期实现遇到这种块直接 `return`，于是整个电音效果**静默失效**且没有任何日志。
    /// </summary>
    public void ProcessInPlace(Span<float> buffer, int sampleRate)
    {
        for (var offset = 0; offset < buffer.Length; offset += _dryBuffer.Length)
        {
            var count = Math.Min(_dryBuffer.Length, buffer.Length - offset);
            ProcessChunk(buffer.Slice(offset, count), sampleRate);
        }
    }

    /// <summary>处理一个不超过内部缓冲区容量的块。</summary>
    private void ProcessChunk(Span<float> buffer, int sampleRate)
    {
        if (buffer.Length == 0) return;

        var length = buffer.Length;

        // 0) 先留一份未变调的参考信号：基频检测必须基于它，
        //    否则下一帧测到的是已经移动过的音高，会一帧帧累积漂移
        buffer.CopyTo(_dryBuffer);

        // 1) 在参考信号上做基频检测
        PushAnalysis(_dryBuffer.AsSpan(0, length));
        var detected = _detector.Detect(_analysisWindow);

        // 2) 吸附到音阶，得到目标音高（以 MIDI 编号表示）
        if (detected > 0)
        {
            var midi = 69.0 + 12.0 * Math.Log2(detected / 440.0);
            var snapped = SnapToMidi(midi);

            if (_pitchNumber < 0 || Math.Abs(snapped - _pitchNumber) >= 0.5)
                _pitchNumber = (int)Math.Round(snapped);

            _targetPitch = snapped;
        }
        else
        {
            _pitchNumber = -1;
        }

        // 3) 参考音高（同一份参考信号测得）→ 需要的变调量
        var referenceMidi = detected > 0 ? 69.0 + 12.0 * Math.Log2(detected / 440.0) : 0;
        var wanted = detected > 0 ? _targetPitch - referenceMidi : 0.0;

        // 4) 吸附速度：RetuneSpeed 越大逼近越快（越大越"电"）
        var speed = Math.Clamp(RetuneSpeed, 0f, 100f) / 100f;
        var coefficient = 0.015f + speed * 0.42f;
        _appliedSemitones += (wanted - _appliedSemitones) * coefficient;
        _appliedSemitones = Math.Clamp(_appliedSemitones, -12, 12);

        // 5) 变调（相位声码器，见 Dsp/PitchShifter.cs）
        if (EnablePitchShift && _shifter != null && Math.Abs(_appliedSemitones) > 0.01)
        {
            _shifter.Semitones = (float)_appliedSemitones;
            _shifter.Process(buffer);
        }

        LastDetectedFrequency = detected;

        // 6) 电子化音染
        if (EnableTimbre) _timbre?.Process(buffer);

        // 7) 极短延迟叠加，做出厚度（近似八度共鸣的听感）
        if (HarmonyAmount > 0.01f)
        {
            // 极短延迟叠加做厚度。系数收小：这是梳状滤波，
            // 过大时会产生金属感/电流声般的音染。
            var amount = Math.Clamp(HarmonyAmount, 0f, 1f) * 0.28f;
            for (var i = length - 1; i >= 2; i--)
                buffer[i] += buffer[i - 2] * amount;
        }
    }

    /// <summary>
    /// 把 MIDI 音高吸附到当前调式的最近音级。
    /// **带迟滞**：当前音级到输入音高的距离比最近音级近 0.12 半音以上时保持不动。
    /// 否则输入在音级中点附近抖动时，目标音级会来回跳变，
    /// 变调量随之反复跳——听感就是"卡顿感 + 电流声"。
    /// </summary>
    private double SnapToMidi(double midi)
    {
        var intervals = ScaleIntervals(Scale);
        var best = midi;
        var bestDistance = double.MaxValue;

        for (var octave = -1; octave <= 1; octave++)
        {
            foreach (var interval in intervals)
            {
                var note = RootMidiNote + octave * 12 + interval;
                var distance = Math.Abs(midi - note);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = note;
            }
        }

        // 迟滞：仍偏向当前音级，除非新音级明显更近
        const double hysteresis = 0.12;
        if (_pitchNumber >= 0)
        {
            var currentDistance = Math.Abs(midi - _pitchNumber);
            if (currentDistance <= bestDistance + hysteresis) return _pitchNumber;
        }

        return best;
    }


    private static int[] ScaleIntervals(TuneScale scale) => scale switch
    {
        TuneScale.Minor => new[] { 0, 2, 3, 5, 7, 8, 10 },
        TuneScale.Pentatonic => new[] { 0, 2, 4, 7, 9 },
        TuneScale.Chromatic => new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 },
        _ => new[] { 0, 2, 4, 5, 7, 9, 11 },
    };

    /// <summary>
    /// 把新数据写入分析环形缓冲，并在需要时拷出供检测用的连续分析窗。
    /// 早期用 `Array.Copy(window, take, window, 0, keep)` 做原地滑动，
    /// 当 take &lt; keep 时**同数组重叠拷贝会破坏尚未读取的部分**，
    /// 导致分析窗内容错乱、基频检测几乎总是失败（电音因此几乎不生效）。
    /// </summary>
    private void PushAnalysis(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            _analysisRing[_analysisWrite] = sample;
            _analysisWrite = (_analysisWrite + 1) % _analysisRing.Length;
        }

        // 拷出一份连续的、以最新样本结尾的分析窗
        for (var i = 0; i < _analysisWindow.Length; i++)
        {
            var index = (_analysisWrite + i) % _analysisRing.Length;
            _analysisWindow[i] = _analysisRing[index];
        }
    }

    public void Reset()
    {
        _detector.Reset();
        _appliedSemitones = 0;
        _targetPitch = 0;
        _pitchNumber = -1;
        _shifter?.Reset();
        Array.Clear(_analysisRing);
        Array.Clear(_analysisWindow);
        _analysisWrite = 0;
    }
}

