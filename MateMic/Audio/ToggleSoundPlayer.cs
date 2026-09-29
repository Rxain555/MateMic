using System.IO;
using MateMic.Core;
using NAudio.Wave;

namespace MateMic.Audio;

/// <summary>
/// 「音频处理」总开关的反馈音效。
///
/// 三条开关路径（界面开关 / 全局快捷键 / 托盘菜单）最终都汇到主窗口的
/// <c>OnProcessingToggled</c>，因此播放点只有一处。
///
/// 播放到**系统默认输出设备**（通常是扬声器或耳机）：这是"给用户自己的反馈"，
/// 刻意不送进 MIXLINE 那条处理链——否则关掉处理时反而还会往链路里灌一声。
///
/// 连续快速切换时先停掉上一条，避免两声重叠。
/// </summary>
public static class ToggleSoundPlayer
{
    private static readonly object Gate = new();
    private static WaveOut? _output;
    private static AudioFileReader? _reader;

    /// <summary>播放开启或关闭的音效。<paramref name="enabling"/> 决定用哪一个文件。</summary>
    public static void Play(bool enabling)
    {
        var name = enabling ? "toggle-on.wav" : "toggle-off.wav";
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "sounds", name);

        if (!File.Exists(path))
        {
            // 文件缺失只影响音效，绝不能影响开关本身
            Log.Warn("开关音效文件不存在，本次不播放：" + path);
            return;
        }

        try
        {
            lock (Gate)
            {
                StopCurrent();

                var reader = new AudioFileReader(path);
                // NAudio 3.0 起 WaveOutEvent 已更名为 WaveOut；延迟用默认值
                // （BufferMilliseconds 默认 100ms，对一声约 1 秒的反馈音足够跟手）
                var output = new WaveOut();
                output.Init(reader);

                // 回调里只清理"自己这一对"，避免把后一次播放的实例误关（连续切换时会发生）
                output.PlaybackStopped += (_, _) =>
                {
                    try { output.Dispose(); } catch { /* 忽略 */ }
                    try { reader.Dispose(); } catch { /* 忽略 */ }
                };

                _output = output;
                _reader = reader;
                output.Play();
            }
        }
        catch (Exception ex)
        {
            // 没有可用输出设备、设备被独占等情况都走这里：只记日志
            Log.Warn("播放开关音效失败（不影响开关本身）：" + ex.Message);
        }
    }

    /// <summary>停掉正在播放的一条（并释放）。</summary>
    private static void StopCurrent()
    {
        try { _output?.Stop(); } catch { /* 忽略 */ }
        try { _output?.Dispose(); } catch { /* 忽略 */ }
        try { _reader?.Dispose(); } catch { /* 忽略 */ }
        _output = null;
        _reader = null;
    }
}
