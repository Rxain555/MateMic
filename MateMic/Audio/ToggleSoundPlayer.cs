using System.IO;
using MateMic.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MateMic.Audio;

/// <summary>
/// 「音频处理」总开关的反馈音效。
///
/// 三条开关路径（界面开关 / 全局快捷键 / 托盘菜单）最终都汇到主窗口的
/// <c>OnProcessingToggled</c>，因此播放点只有一处。
///
/// **播放目标**（按优先级）：
///   1. 调用方给的「监听设备」——那正是用户实际在听的设备；
///   2. 系统默认输出设备（<see cref="WaveOut"/> 默认 <c>DeviceNumber = -1</c>，已实测）。
/// 为什么优先监听设备：用户反馈"构建版能听到、安装版听不到"，而两版的代码、配置、
/// 设备路由实测完全一致，说明差异在系统层面（按应用路由 / 会话）。把目标钉在
/// "用户自己选的设备"上最可控，也最符合直觉。
///
/// **播放前按文件峰值归一化音量**：内置素材峰值只有满刻度的 15%（约 −16 dB），
/// 原样播放几乎听不见。这里先量峰值再抬到 <see cref="TargetPeak"/>：
/// **只放大不衰减**，上限 <see cref="MaxGain"/> 倍以免把底噪一起放大。
///
/// 每次播放都写一条 INFO 日志，带上目标设备名与增益——万一还有"听不到"的情况，
/// 日志能直接告诉我们它播到哪台设备去了。
///
/// 连续快速切换时先停掉上一条，避免两声重叠。
/// </summary>
public static class ToggleSoundPlayer
{
    /// <summary>
    /// 归一化的目标峰值（占满刻度比例）。留一点余量，避免重采样时削顶。
    /// 取值经过实测调整：内置素材原始峰值仅 0.153（听不见）→ 0.85（用户反馈"有点大"）
    /// → 定为 0.4（约 −8 dBFS，仍是原素材的约 2.6 倍）。
    /// </summary>
    private const float TargetPeak = 0.4f;

    /// <summary>最大放大倍数：素材近乎静音时不至于把底噪一起放大。</summary>
    private const float MaxGain = 10f;

    private static readonly object Gate = new();
    private static IWavePlayer? _output;
    private static AudioFileReader? _reader;

    /// <summary>本类当前持有（需要负责释放）的监听设备。</summary>
    private static MMDevice? _ownedDevice;

    /// <summary>
    /// 播放开启或关闭的音效。
    /// </summary>
    /// <param name="enabling">true 播开启音，false 播关闭音。</param>
    /// <param name="preferredDevice">
    /// 优先播放到的设备（调用方传「监听设备」）。**所有权移交给本方法**：
    /// 播放结束或下一次播放时由本类释放，调用方不要再动它。
    /// 为 null、或在该设备上播放失败时，自动退回系统默认输出设备。
    /// </param>
    public static void Play(bool enabling, MMDevice? preferredDevice = null)
    {
        var name = enabling ? "toggle-on.wav" : "toggle-off.wav";
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "sounds", name);

        if (!File.Exists(path))
        {
            // 文件缺失只影响音效，绝不能影响开关本身
            Log.Warn("开关音效文件不存在，本次不播放：" + path);
            DisposeDevice(preferredDevice);
            return;
        }

        lock (Gate)
        {
            StopCurrent();
            var gain = MeasureGain(path);

            if (preferredDevice != null)
            {
                if (TryStart(path, gain, preferredDevice, out var preferredError))
                {
                    Log.Info($"开关音效：{name} →「{preferredDevice.FriendlyName}」（音量增益 ×{gain:0.0}）");
                    return;
                }

                // 监听设备可能刚被拔掉、或被别的程序独占：退回默认设备，别让开关本身失败
                Log.Warn($"开关音效：在监听设备「{preferredDevice.FriendlyName}」上播放失败，改用系统默认设备。" +
                         $"原因：{preferredError}");
                DisposeDevice(preferredDevice);
            }

            if (TryStart(path, gain, null, out var fallbackError))
            {
                Log.Info($"开关音效：{name} → 系统默认输出设备（音量增益 ×{gain:0.0}）");
                return;
            }

            Log.Warn("播放开关音效失败（不影响开关本身）：" + fallbackError);
        }
    }

    /// <summary>
    /// 把音效播到指定设备（<paramref name="device"/> 为 null 表示系统默认输出设备）。
    /// 成功时把播放器与读取器记到字段上，供下一次播放或收尾时释放。
    /// </summary>
    private static bool TryStart(string path, float gain, MMDevice? device, out string? error)
    {
        error = null;
        AudioFileReader? reader = null;
        IWavePlayer? output = null;
        try
        {
            reader = new AudioFileReader(path) { Volume = gain };

            // 指定了设备就走 WASAPI（能精确指定端点）；否则用 winmm 的默认设备
            output = device != null
                ? new WasapiOut(device, AudioClientShareMode.Shared, true, 100)
                : new WaveOut();
            output.Init(reader);

            // 回调里只清理"自己这一对"，避免把后一次播放的实例误关（连续切换时会发生）；
            // 设备也只在"仍然是当前那一个"时才释放，同样是为了不误伤后一次播放。
            var thisReader = reader;
            var thisOutput = output;
            var thisDevice = device;
            output.PlaybackStopped += (_, _) =>
            {
                try { thisOutput.Dispose(); } catch { /* 忽略 */ }
                try { thisReader.Dispose(); } catch { /* 忽略 */ }
                if (ReferenceEquals(_ownedDevice, thisDevice))
                {
                    DisposeDevice(thisDevice);
                    _ownedDevice = null;
                }
            };

            _output = output;
            _reader = reader;
            _ownedDevice = device;
            output.Play();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try { output?.Dispose(); } catch { /* 忽略 */ }
            try { reader?.Dispose(); } catch { /* 忽略 */ }
            return false;
        }
    }

    /// <summary>
    /// 扫一遍音频求峰值，算出"抬到 <see cref="TargetPeak"/>"所需的增益。
    /// 只放大不衰减；素材本身就是满量程时返回 1.0；近乎静音时也返回 1.0（不放大底噪）。
    /// </summary>
    private static float MeasureGain(string path)
    {
        // 反馈音只有 1 秒，上限纯属防御异常素材，避免在开关这种热路径上长时间读盘
        const int maxSecondsToScan = 5;

        using var reader = new AudioFileReader(path);
        // AudioFileReader 同时实现 IWaveProvider(byte[]) 与 ISampleProvider(float[])：
        // 直接写 reader.Read(float[]) 会解析到 byte[] 重载，必须转成接口；
        // 而 NAudio 3.x 的 ISampleProvider.Read 签名已从 (float[],int,int) 改为 Read(Span<float>)。
        var samples = (ISampleProvider)reader;
        var format = reader.WaveFormat;
        var buffer = new float[Math.Max(format.SampleRate * format.Channels, 1024)];
        var maxSamples = (long)format.SampleRate * format.Channels * maxSecondsToScan;

        float peak = 0f;
        long scanned = 0;
        int read;
        while (scanned < maxSamples && (read = samples.Read(buffer.AsSpan())) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var value = Math.Abs(buffer[i]);
                if (value > peak) peak = value;
            }
            scanned += read;
        }

        if (peak <= 0.001f) return 1f;                       // 近乎静音：不动它
        return Math.Clamp(TargetPeak / peak, 1f, MaxGain);   // 只放大，不衰减
    }

    /// <summary>停掉正在播放的一条（并释放，含持有的设备）。</summary>
    private static void StopCurrent()
    {
        try { _output?.Stop(); } catch { /* 忽略 */ }
        try { _output?.Dispose(); } catch { /* 忽略 */ }
        try { _reader?.Dispose(); } catch { /* 忽略 */ }
        _output = null;
        _reader = null;

        DisposeDevice(_ownedDevice);
        _ownedDevice = null;
    }

    private static void DisposeDevice(MMDevice? device)
    {
        try { device?.Dispose(); } catch { /* 忽略 */ }
    }
}
