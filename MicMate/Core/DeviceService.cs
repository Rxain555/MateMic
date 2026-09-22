using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicMate.Core;

public sealed class AudioDevice
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DataFlow Flow { get; init; }
    public bool IsDefault { get; init; }

    public override string ToString() => Name;
}

/// <summary>
/// WASAPI 设备枚举（输入 = Capture，输出/监听 = Render）+ 设备热插拔通知 + MIXLINE 检测。
/// </summary>
public sealed class DeviceService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDeviceNotificationClient? _notifications;
    private bool _disposed;

    /// <summary>设备列表发生变化（热插拔 / 默认设备切换）时触发，已在 UI 同步上下文上。</summary>
    public event EventHandler? DevicesChanged;

    public DeviceService()
    {
        try
        {
            _notifications = _enumerator.CreateNotificationClient();
            _notifications.DeviceStateChanged += (_, _) => RaiseChanged();
            _notifications.DefaultDeviceChanged += (_, _) => RaiseChanged();
            _notifications.DeviceAdded += (_, _) => RaiseChanged();
            _notifications.DeviceRemoved += (_, _) => RaiseChanged();
        }
        catch (Exception ex)
        {
            Log.Warn("无法注册设备变更通知：" + ex.Message);
        }
    }

    private void RaiseChanged()
    {
        try
        {
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Debug("设备变更通知处理失败：" + ex.Message);
        }
    }

    public IReadOnlyList<AudioDevice> Enumerate(DataFlow flow)
    {
        var list = new List<AudioDevice>();
        string defaultId = string.Empty;
        try
        {
            if (_enumerator.TryGetDefaultAudioEndpoint(flow, Role.Multimedia, out var def) && def != null)
            {
                defaultId = def.ID;
                def.Dispose();
            }
        }
        catch
        {
            // 没有默认端点时忽略
        }

        try
        {
            using var collection = _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
            for (var i = 0; i < collection.Count; i++)
            {
                var device = collection[i];
                try
                {
                    list.Add(new AudioDevice
                    {
                        Id = device.ID,
                        Name = device.FriendlyName,
                        Flow = flow,
                        IsDefault = string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase),
                    });
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("设备枚举失败", ex);
        }

        return list;
    }

    public MMDevice? GetDevice(string? id, DataFlow flow)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                var match = Enumerate(flow).FirstOrDefault(d =>
                    string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
                if (match != null) return _enumerator.GetDevice(match.Id);
            }

            return _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
        }
        catch (Exception ex)
        {
            Log.Warn($"打开设备失败（{flow} / {id}）：{ex.Message}");
            return null;
        }
    }

    public static bool IsMixLine(string name)
        => name.Contains("MIXLINE", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 启动检测：是否存在名称包含 MIXLINE 的播放设备。不阻止软件运行，仅用于显示黄色提示。
    /// </summary>
    public bool HasMixLineDevice()
    {
        try
        {
            return Enumerate(DataFlow.Render).Any(d => IsMixLine(d.Name));
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifications?.Dispose();
        _enumerator.Dispose();
    }
}
