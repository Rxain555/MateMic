using System.Runtime.CompilerServices;

namespace MicMate.Dsp;

/// <summary>
/// 把线性增益平滑成指数渐变，避免增益突变产生可闻的“抽吸”（zipper noise）。
/// 音频线程专用：无分配、无锁。
/// </summary>
public sealed class SmoothedGain
{
    private float _current = 1f;
    private float _target = 1f;
    private float _coefficient = 1f;

    public float Current => _current;

    /// <summary>时间常数（毫秒）→ 每采样平滑系数。</summary>
    public void SetTimeConstant(float milliseconds, int sampleRate)
    {
        if (milliseconds <= 0f)
        {
            _coefficient = 1f;
            return;
        }

        _coefficient = 1f - MathF.Exp(-1f / (milliseconds * 0.001f * sampleRate));
    }

    public void SetTarget(float target) => _target = target;

    public void Snap(float value)
    {
        _current = value;
        _target = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Next()
    {
        _current += (_target - _current) * _coefficient;
        return _current;
    }

    public void Process(Span<float> buffer)
    {
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] *= Next();
    }
}

/// <summary>通用的小工具。</summary>
public static class AudioMath
{
    public const float MinDb = -100f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DbToLinear(float db) => db <= MinDb ? 0f : MathF.Pow(10f, db / 20f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float LinearToDb(float linear)
        => linear <= 1e-6f ? MinDb : 20f * MathF.Log10(linear);
}
