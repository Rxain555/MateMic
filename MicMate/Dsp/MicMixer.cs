using MicMate.Core;
using NAudio.Wave;

namespace MicMate.Dsp;

/// <summary>
/// 麦克风与播放器混合总线。
/// 处理后的麦克风信号与播放器音频以固定 50:50 比例混合（项目书 3.2.2）。
/// 当总开关关闭时，麦克风侧输入静音，但播放器（语音包）仍然送出。
/// 使用 ReaderWriterLockSlim 保护播放器缓冲区的替换：音频线程持读锁，
/// 热切换播放器只在用户操作时短暂持写锁（正常播放路径无锁开销）。
/// </summary>
public sealed class MicMixer : NAudio.Wave.ISampleProvider
{
    private const float MicWeight = 0.5f;
    private const float PlayerWeight = 0.5f;

    private readonly ReaderWriterLockSlim _lock = new();
    private readonly float[] _micScratch = new float[8192];
    private readonly float[] _playerScratch = new float[8192];
    private readonly NAudio.Wave.ISampleProvider _micSource;

    private ISampleProvider? _player;

    /// <summary>总旁通：true = 正常处理；false = 麦克风静音（一键闭麦）。</summary>
    public volatile bool MicEnabled = true;

    /// <summary>播放器是否接入混合总线（配合“同时监听”）。</summary>
    public volatile bool PlayerEnabled;

    public MicMixer(NAudio.Wave.WaveFormat format, NAudio.Wave.ISampleProvider micSource)
    {
        WaveFormat = format;
        _micSource = micSource;
    }

    public NAudio.Wave.WaveFormat WaveFormat { get; }

    /// <summary>被音频线程拉取的次数（诊断用）。</summary>
    public long ReadCount => Interlocked.Read(ref _readCount);

    private long _readCount;

    public void SetPlayer(NAudio.Wave.ISampleProvider? player)
    {
        _lock.EnterWriteLock();
        try
        {
            _player = player;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public int Read(Span<float> buffer)
    {
        Interlocked.Increment(ref _readCount);
        _lock.EnterReadLock();
        try
        {
            var micRead = 0;
            if (MicEnabled)
                micRead = _micSource.Read(_micScratch.AsSpan(0, Math.Min(_micScratch.Length, buffer.Length)));

            var player = _player;
            var playerRead = 0;
            if (PlayerEnabled && player != null)
                playerRead = player.Read(_playerScratch.AsSpan(0, Math.Min(_playerScratch.Length, buffer.Length)));
            for (var i = 0; i < buffer.Length; i++)
            {
                var mic = i < micRead ? _micScratch[i] * MicWeight : 0f;
                var play = i < playerRead ? _playerScratch[i] * PlayerWeight : 0f;
                buffer[i] = mic + play;
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }

        return buffer.Length;
    }
}
