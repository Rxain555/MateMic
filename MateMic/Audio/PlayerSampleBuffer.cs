using NAudio.Wave;

namespace MateMic.Audio;

/// <summary>
/// 播放器音频缓冲：后台解码线程写入 48 kHz / 单声道 / float 样本，
/// 音频线程（混音器）以 ISampleProvider 读出。内部使用 BufferedWaveProvider
/// （环形缓冲 + 溢出丢弃），写入路径不加锁。
/// </summary>
public sealed class PlayerSampleBuffer
{
    private readonly BufferedWaveProvider _buffer;
    private readonly ISampleProvider _reader;

    public PlayerSampleBuffer(TimeSpan duration)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(AudioEngine.SampleRate, 1);
        _buffer = new BufferedWaveProvider(format, duration)
        {
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };
        _reader = _buffer.ToSampleProvider();
    }

    public WaveFormat WaveFormat => _buffer.WaveFormat;

    public ISampleProvider Reader => _reader;

    public TimeSpan BufferedDuration => _buffer.BufferedDuration;

    /// <summary>解码线程写入（float 样本）。</summary>
    public void AddSamples(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return;
        _buffer.AddSamples(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples));
    }

    public void Clear() => _buffer.ClearBuffer();
}
