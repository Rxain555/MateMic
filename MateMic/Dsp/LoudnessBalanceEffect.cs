using NAudio.Wave;
using MateMic.Core;

namespace MateMic.Dsp;

/// <summary>
/// 3. 响度平衡（RMS 简化版）：
/// 400 ms 积分窗口测 RMS，换算到目标响度区间后做指数渐变增益，
/// 峰值超过 −1 dBFS 时**立即**降低增益防削波（不走平滑器）。
///
/// 两个关键点（早期版本都没有，导致明显听感问题）：
///   · 峰值保护必须立刻生效：只改"目标值"再由 0.5–5 s 的平滑器慢慢爬过去，
///     等于没有保护（实测 0 dBFS 冲激会让输出冲到 +12.5 dBFS 才被后级限幅器按住）。
///   · 低于 <see cref="MinInputDb"/> 时**保持**增益而不是继续加大：否则一段静音/底噪
///     会被一路加到 +24 dB，说话时第一个字必然过响，且底噪被整体抬高。
/// </summary>
public sealed class LoudnessBalanceEffect : IAudioEffect
{
    private const int RmsWindowMs = 400;
    private const float PeakCeilingDb = -1f;
    private const float MaxGainDb = 24f;
    private const float MinGainDb = -24f;

    /// <summary>低于这个输入电平视为"没有人在说话"：增益保持不动（相当于 AGC 的噪声门）。</summary>
    private const float MinInputDb = -50f;

    private static readonly float PeakCeilingLinear = AudioMath.DbToLinear(PeakCeilingDb);

    private readonly LoudnessSettings _settings;
    private readonly int _sampleRate;
    private readonly int _windowSamples;
    private readonly float[] _window;
    private readonly SmoothedGain _gain = new();
    private readonly float[] _peakWindow;

    private int _windowPos;
    private int _windowFilled;
    private double _sumSquares;
    private int _peakPos;
    private float _peak;

    public LoudnessBalanceEffect(WaveFormat format, LoudnessSettings settings)
    {
        WaveFormat = format;
        _settings = settings;
        _sampleRate = format.SampleRate;
        _windowSamples = Math.Max(1, _sampleRate * RmsWindowMs / 1000);
        _window = new float[_windowSamples];
        _peakWindow = new float[Math.Max(1, _sampleRate / 100)];   // 10 ms 峰值窗
        _gain.Snap(1f);
        UpdateParameters();
    }

    public string Name => "响度平衡";

    public bool Enabled { get; set; } = true;

    public WaveFormat WaveFormat { get; }

    /// <summary>
    /// 当前增益，用于 UI 显示（dB）。界面上的「实时读数」用的就是它，
    /// 有了读数才能看出这个模块到底有没有在工作。
    /// </summary>
    public float CurrentGainDb { get; private set; }

    /// <summary>最近一次测量的 RMS 电平（dBFS，增益前）。</summary>
    public float CurrentRmsDb { get; private set; } = AudioMath.MinDb;

    /// <summary>最近一次输出的估算电平（dBFS = 输入 RMS + 当前增益）。</summary>
    public float CurrentOutputDb => CurrentRmsDb <= AudioMath.MinDb
        ? AudioMath.MinDb
        : CurrentRmsDb + CurrentGainDb;

    public void UpdateParameters()
    {
        // 响应速度 1–10 → 时间常数 2500 ms – 150 ms（指数映射）。
        // 早期用的是 5000 ms – 500 ms：滑块默认值 5 对应 1.58 s，指数平滑要 3 倍时间常数
        // （≈5 秒）才走完 95%，拖动"目标响度"后要等好几秒才听得出变化，
        // 用户自然会觉得"两个滑条没功能"。
        var speed = Math.Clamp(_settings.Speed, 1, 10);
        var timeConstantMs = 2500f * MathF.Pow(0.1f, (speed - 1) / 9f);
        _gain.SetTimeConstant(timeConstantMs, _sampleRate);
    }

    public int Read(Span<float> buffer)
    {
        if (!Enabled) return buffer.Length;

        var target = Math.Clamp(_settings.TargetLufs, -30f, -8f);

        for (var i = 0; i < buffer.Length; i++)
        {
            var sample = buffer[i];

            // 1) 用窗口里的历史**输入**样本估 RMS。这里必须量增益前的信号：
            //    增益是线性的，rms_out = rms_in × g，所以 g = target / rms_in 直接命中目标；
            //    若改成量增益后的输出再套同一个式子，环路增益变成 2，
            //    稳态会停在"只加到一半"的位置（实测目标 −20 dBFS 只到 −26.8 dBFS）。
            var rms = MathF.Sqrt((float)(_sumSquares / Math.Max(1, _windowFilled)));
            CurrentRmsDb = AudioMath.LinearToDb(rms);

            // 2) 目标增益。低于噪声门限时保持当前增益，不继续往上加。
            if (CurrentRmsDb <= MinInputDb)
            {
                _gain.SetTarget(_gain.Current);
            }
            else
            {
                var desiredDb = Math.Clamp(target - CurrentRmsDb, MinGainDb, MaxGainDb);
                _gain.SetTarget(AudioMath.DbToLinear(desiredDb));
            }

            // 3) 峰值保护：用输入峰值算出"当前允许的最大增益"，超过就立刻压下来。
            //    本轮样本一定满足 |sample| * g ≤ −1 dBFS。
            _peakWindow[_peakPos] = sample;
            _peakPos++;
            if (_peakPos >= _peakWindow.Length)
            {
                _peakPos = 0;
                var peak = 0f;
                for (var p = 0; p < _peakWindow.Length; p++)
                {
                    var v = MathF.Abs(_peakWindow[p]);
                    if (v > peak) peak = v;
                }

                _peak = peak;
            }

            var observed = MathF.Max(_peak, MathF.Abs(sample));
            var limit = observed > 1e-6f ? PeakCeilingLinear / observed : float.MaxValue;

            var g = _gain.Next();
            if (g > limit)
            {
                g = limit;
                _gain.Snap(g);   // 立即生效，不经过平滑器
            }

            CurrentGainDb = AudioMath.LinearToDb(g);
            var output = sample * g;
            buffer[i] = output;

            // 4) 把**增益前**的样本写进窗口（见上面第 1 步的理由）
            if (_windowFilled >= _windowSamples)
                _sumSquares -= (double)_window[_windowPos] * _window[_windowPos];
            else
                _windowFilled++;

            _window[_windowPos] = sample;
            _sumSquares += (double)sample * sample;
            _windowPos++;
            if (_windowPos >= _windowSamples) _windowPos = 0;
        }

        return buffer.Length;
    }
}
