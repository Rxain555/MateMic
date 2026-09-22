using NAudio.Wave;
using MicMate.Core;

namespace MicMate.Dsp;

/// <summary>
/// 3. 响度平衡（RMS 简化版）：
/// 400 ms 积分窗口测 RMS，换算到目标响度区间后做指数渐变增益，
/// 峰值超过 −1 dBFS 时立即降低增益防削波。
/// </summary>
public sealed class LoudnessBalanceEffect : IAudioEffect
{
    private const int RmsWindowMs = 400;
    private const float PeakCeilingDb = -1f;
    private const float MaxGainDb = 24f;
    private const float MinGainDb = -24f;

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

    /// <summary>当前增益，用于 UI 显示（dB）。</summary>
    public float CurrentGainDb { get; private set; }

    /// <summary>最近一次测量的 RMS 电平（dBFS）。</summary>
    public float CurrentRmsDb { get; private set; } = AudioMath.MinDb;

    public void UpdateParameters()
    {
        // 响应速度 1–10 → 时间常数 5 s – 0.5 s（指数映射）
        var speed = Math.Clamp(_settings.Speed, 1, 10);
        var timeConstantMs = 5000f * MathF.Pow(0.1f, (speed - 1) / 9f);
        _gain.SetTimeConstant(timeConstantMs, _sampleRate);
    }

    public int Read(Span<float> buffer)
    {
        if (!Enabled) return buffer.Length;

        var target = Math.Clamp(_settings.TargetLufs, -30f, -8f);

        for (var i = 0; i < buffer.Length; i++)
        {
            var sample = buffer[i];

            // RMS 滑动窗口
            if (_windowFilled >= _windowSamples)
                _sumSquares -= _window[_windowPos] * _window[_windowPos];
            else
                _windowFilled++;

            _window[_windowPos] = sample;
            _sumSquares += sample * sample;
            _windowPos++;
            if (_windowPos >= _windowSamples) _windowPos = 0;

            var rms = MathF.Sqrt((float)(_sumSquares / Math.Max(1, _windowFilled)));
            CurrentRmsDb = AudioMath.LinearToDb(rms);

            var desiredDb = Math.Clamp(target - CurrentRmsDb, MinGainDb, MaxGainDb);
            _gain.SetTarget(AudioMath.DbToLinear(desiredDb));

            // 峰值保护：10 ms 窗内维护滑动峰值
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

            var peakDb = AudioMath.LinearToDb(MathF.Max(_peak, MathF.Abs(sample)));
            if (peakDb > PeakCeilingDb)
                _gain.SetTarget(MathF.Min(_gain.Current, _gain.Current * AudioMath.DbToLinear(PeakCeilingDb - peakDb)));

            var g = _gain.Next();
            CurrentGainDb = AudioMath.LinearToDb(g);
            buffer[i] = sample * g;
        }

        return buffer.Length;
    }
}
