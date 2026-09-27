using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MateMic.Core;

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

    /// <summary>
    /// 设备变更代次：每收到一次 WASAPI 回调就自增。
    /// 用途有两个：① 去重——同一个"插拔"动作会连发多条通知
    /// （DeviceRemoved + DeviceStateChanged + DefaultDeviceChanged），上层据此只处理一次；
    /// ② UI 线程用它判断"我这次重扫期间设备又变了没有"。
    /// </summary>
    private int _revision;

    /// <summary>设备列表发生变化（热插拔 / 默认设备切换）时触发，已在 UI 同步上下文上。</summary>
    public event EventHandler? DevicesChanged;

    /// <summary>设备变更代次，见 <see cref="_revision"/>。读取是原子的。</summary>
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>
    /// 本次运行期间见过的设备名（设备 ID → 友好名）。
    /// 设备被拔掉后就枚举不到了，但界面仍要告诉用户"掉的是哪一个"，
    /// 因此把见过的名字缓存下来，而不是只能显示一个陌生的设备 ID。
    /// </summary>
    private readonly Dictionary<string, string> _knownNames = new(StringComparer.OrdinalIgnoreCase);

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
        Interlocked.Increment(ref _revision);
        try
        {
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Debug("设备变更通知处理失败：" + ex.Message);
        }
    }

    /// <summary>某个设备 ID 当前是否可用（Active）。热插拔恢复的核心判断。</summary>
    public bool IsPresent(string? id, DataFlow flow)
        => !string.IsNullOrWhiteSpace(id) &&
           Enumerate(flow).Any(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 按设备 ID 找；ID 对不上时退回**按名字**找。
    ///
    /// 为什么需要按名字兜底：部分 USB 音频设备重新插入后会被系统重新枚举，
    /// 拿到一个**全新的设备 ID**（实测过某些 USB 麦克风与蓝牙设备）。
    /// 此时配置里记的旧 ID 永远匹配不上，表现就是
    /// "下拉框能选回那支麦克风，但音频流还是建不起来"——
    /// 而用户手动换一个设备再换回来之所以有效，是因为那次会按**当前枚举到的 ID** 写入配置。
    /// 按名字兜底就能自动完成这件事。
    /// </summary>
    /// <returns>匹配到的设备；没有再返回 null。</returns>
    public AudioDevice? ResolvePreferred(string? id, DataFlow flow)
    {
        var list = Enumerate(flow);
        if (list.Count == 0) return null;

        var byId = list.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
        if (byId != null) return byId;

        if (string.IsNullOrWhiteSpace(id)) return null;

        // ID 变了：用"本次运行见过的名字"当指纹去找同名的设备
        if (!_knownNames.TryGetValue(id, out var rememberedName) ||
            string.IsNullOrWhiteSpace(rememberedName))
        {
            return null;
        }

        var byName = list.Where(d => string.Equals(d.Name, rememberedName, StringComparison.Ordinal))
                         .ToList();

        // 同名设备可能不止一台（例如两支同型号麦克风）。这种情况不做猜测：
        // 猜错会把音频接到别人的设备上，比"不自动恢复"更糟。
        if (byName.Count != 1) return null;

        Log.Warn($"设备 ID 已变化，按名称匹配到同一支设备：" +
                 $"「{rememberedName}」{Short(id)} → {Short(byName[0].Id)}");
        return byName[0];
    }

    /// <summary>设备 ID 的后 12 位（用于日志，够区分设备又不会刷屏）。</summary>
    private static string Short(string? id)
        => string.IsNullOrWhiteSpace(id) ? "(空)" : id.Length <= 12 ? id : "…" + id[^12..];

    /// <summary>取设备友好名：优先当前枚举结果，退化到本次运行见过并缓存的名字。</summary>
    public string DescribeName(string? id, DataFlow flow)
    {
        if (string.IsNullOrWhiteSpace(id)) return "(未选择)";

        var present = Enumerate(flow).FirstOrDefault(d =>
            string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
        if (present != null) return present.Name;

        return _knownNames.TryGetValue(id, out var remembered)
            ? remembered + "（已拔出）"
            : "（已拔出，ID " + id + "）";
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
                    var name = device.FriendlyName;
                    var id = device.ID;
                    // 顺手记下见过的名字，拔掉之后还能告诉用户掉的是哪一个
                    _knownNames[id] = name;

                    list.Add(new AudioDevice
                    {
                        Id = id,
                        Name = name,
                        Flow = flow,
                        IsDefault = string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase),
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

    public MMDevice? GetDevice(string? id, DataFlow flow) => GetDevice(id, flow, out _);

    /// <summary>
    /// 按配置里的首选设备打开；ID 变了就按名字兜底并在 <paramref name="resolvedId"/> 里回传真实 ID。
    /// 调用方应把回传的 ID 写回配置，这样"重插后 ID 变化"只需自动纠正一次，之后就一直对上。
    /// </summary>
    public MMDevice? GetDevice(string? id, DataFlow flow, out string? resolvedId)
    {
        resolvedId = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                var match = ResolvePreferred(id, flow);
                if (match != null)
                {
                    resolvedId = match.Id;
                    return _enumerator.GetDevice(match.Id);
                }
            }

            // 首选设备不可用时退回系统默认端点（保持"拔掉期间软件仍可用"的行为）
            var fallback = _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            resolvedId = null;
            return fallback;
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
