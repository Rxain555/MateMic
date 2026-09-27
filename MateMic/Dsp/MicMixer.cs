using MateMic.Core;
using NAudio.Wave;

namespace MateMic.Dsp;

/// <summary>
/// 麦克风与播放器混合总线。各来源与其开关如下：
///
/// | 来源 | 主输出（MIXLINE，播给别人听） | 监听（物理扬声器，播给自己听） |
/// |---|---|---|
/// | 麦克风 | 由 <see cref="MicEnabled"/>（工具栏「音频处理」）决定 | 再与 <see cref="MonitorMicEnabled"/>（工具栏「监听」）相与 |
/// | 播放器（伴奏 / 语音包） | **始终参与** | 由 <see cref="PlayerMonitorEnabled"/>（播放器面板「音频监听」）决定 |
///
/// 播放器**始终**进主输出，是"播放给别人听"的主路径：早期版本让它也受「音频监听」控制，
/// 结果变成了"想播给队友听还得先打开监听"，这显然不对。
/// 那个开关因此只管一件事：**我自己的扬声器里要不要也听到它**。
///
/// 监听设备开不开在 <c>AudioEngine</c>（<see cref="ShouldRunPlayer"/> 那一套逻辑）：
/// 它按"要听麦克风 / 要听播放器"的并集决定要不要建监听播放器，并把写入口交给
/// <see cref="SetMonitorSink"/>；sink 为 null 时这里根本不写监听缓冲。
/// <see cref="MonitorMicEnabled"/> 必须单独存在：否则"监听着、但只开了音频监听"这一档
/// 会因为监听设备被音频监听拉起来而连自己的麦克风一起听到。
///
/// 因此四种组合的行为是（"播放器送主输出"在所有组合里都是"是"）：
///
/// | 总监听 | 音频监听 | 监听里听到 |
/// |---|---|---|
/// | 关 | 关 | （没有监听设备，也没有监听输出） |
/// | 关 | 开 | 播放器的音频（不放麦克风） |
/// | 开 | 关 | 麦克风 |
/// | 开 | 开 | 麦克风 + 播放器音频 |
///
/// 实现要点（两个都重要）：
///   · 播放器环形缓冲只能被消费一次，因此监听侧**不是**另开一条拉取链，
///     而是复用主输出这次拉取已经读到的样本，同步写进监听缓冲——两条输出永远对齐。
///   · 监听存在时**即使关掉麦克风也仍然拉取麦克风源**：这次拉取是整条链的节拍，
///     跳过它会让采集侧数据堆积、后续重启时错位。
///
/// 线程：音频线程持读锁拉取；替换播放器/监听缓冲只在用户操作时短暂持写锁。
/// </summary>
public sealed class MicMixer : NAudio.Wave.ISampleProvider
{
    private const float MicWeight = 0.5f;
    private const float PlayerWeight = 0.5f;

    /// <summary>监听缓冲的写入口（音频线程调用，实现必须无锁、无分配）。</summary>
    public interface IMonitorSink
    {
        void Write(ReadOnlySpan<float> samples);
    }

    private readonly ReaderWriterLockSlim _lock = new();
    private readonly float[] _micScratch = new float[8192];
    private readonly float[] _playerScratch = new float[8192];
    private readonly float[] _monitorScratch = new float[8192];
    private readonly NAudio.Wave.ISampleProvider _micSource;

    private ISampleProvider? _player;
    private IMonitorSink? _monitorSink;

    /// <summary>总旁通：true = 麦克风正常处理并送出；false = 麦克风静音（一键闭麦）。</summary>
    public volatile bool MicEnabled = true;

    /// <summary>
    /// 麦克风是否进监听（界面上的工具栏「监听」开关）。
    /// 由 <c>AudioEngine</c> 在监听设备开关变化时同步；监听设备关闭时它一定是 false。
    /// </summary>
    public volatile bool MonitorMicEnabled;

    /// <summary>
    /// 播放器音频要不要**额外**送进监听设备（界面上的「音频监听」开关）。
    /// 注意：播放器音频**始终**参与主输出（MIXLINE），否则"播放给别人听"就得靠打开监听才能出声，
    /// 这显然说不通；这个开关只决定"我自己的扬声器里要不要也听到它"。
    /// </summary>
    public volatile bool PlayerMonitorEnabled;

    public MicMixer(NAudio.Wave.WaveFormat format, NAudio.Wave.ISampleProvider micSource)
    {
        WaveFormat = format;
        _micSource = micSource;
    }

    public NAudio.Wave.WaveFormat WaveFormat { get; }

    /// <summary>被音频线程拉取的次数（诊断用）。</summary>
    public long ReadCount => Interlocked.Read(ref _readCount);

    private long _readCount;

    /// <summary>监听侧实际写入的样本数（诊断用：确认"单独监听"真的在送数据）。</summary>
    public long MonitorSamples => Interlocked.Read(ref _monitorSamples);

    private long _monitorSamples;

    /// <summary>设置播放器源（null = 不接入）。</summary>
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

    /// <summary>设置监听缓冲（null = 监听设备关闭，此时不写监听）。</summary>
    public void SetMonitorSink(IMonitorSink? sink)
    {
        _lock.EnterWriteLock();
        try
        {
            _monitorSink = sink;
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
            var length = buffer.Length;
            var micInMonitor = MicEnabled && MonitorMicEnabled;
            var playerInMonitor = PlayerMonitorEnabled && _monitorSink != null;
            var monitor = _monitorSink;

            // 1) 麦克风：主输出与监听共用同一次读取，避免两路各拉一次而不同步。
            //    监听存在时即使闭麦也要拉取，否则这次拉取不消耗采集数据，整条链会失步。
            var micRead = 0;
            if (MicEnabled || monitor != null)
                micRead = _micSource.Read(_micScratch.AsSpan(0, Math.Min(_micScratch.Length, length)));

            // 2) 播放器：主输出**始终**要它（这是"播给别人听"的主路径），只读一次，同一个样本再给监听用
            var playerRead = 0;
            if (_player != null)
                playerRead = _player.Read(_playerScratch.AsSpan(0, Math.Min(_playerScratch.Length, length)));

            // 3) 主输出混音：麦克风受总开关控制，播放器始终参与
            for (var i = 0; i < length; i++)
            {
                var mic = MicEnabled && i < micRead ? _micScratch[i] * MicWeight : 0f;
                var play = i < playerRead ? _playerScratch[i] * PlayerWeight : 0f;
                buffer[i] = mic + play;
            }

            // 4) 监听混音（独立规则，见类注释）
            if (monitor != null)
            {
                var monitorLength = Math.Min(length, _monitorScratch.Length);
                for (var i = 0; i < monitorLength; i++)
                {
                    var mic = micInMonitor && i < micRead ? _micScratch[i] * MicWeight : 0f;
                    var play = playerInMonitor && i < playerRead ? _playerScratch[i] * PlayerWeight : 0f;
                    _monitorScratch[i] = mic + play;
                }

                monitor.Write(_monitorScratch.AsSpan(0, monitorLength));
                Interlocked.Add(ref _monitorSamples, monitorLength);
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }

        return buffer.Length;
    }
}
