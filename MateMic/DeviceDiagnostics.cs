using MateMic.Core;
using NAudio.CoreAudioApi;

namespace MateMic;

/// <summary>
/// 设备热插拔自检：不用界面、也不用真的开音频流，只订阅设备变更通知并打印事件。
///
/// 用法：MateMic.exe --devicecheck [秒数]
///   默认监视 30 秒。运行期间把麦克风/耳机拔下再插上，控制台会打出
///   [设备事件] 变化 以及在每个事件前后"哪台设备在、哪台不在"的对比，
///   用来确认"拔掉能立刻发现、插回能被识别"这条通路是通的。
///
/// 退出码：0 = 至少收到过一次设备变更事件（说明热插拔通知链路正常）；
///         1 = 超时未收到任何事件（通知未注册成功，或期间确实没有插拔）。
/// </summary>
public static class DeviceDiagnostics
{
    /// <summary>
    /// 同时把结果写到日志文件。
    /// 原因：本程序是 WinExe（没有控制台子系统），从脚本里启动时它的 Console 输出
    /// 不会被父进程捕获（实测 exit=0 但一个字都拿不到）。写文件才能被自动化读到。
    /// </summary>
    private static void Say(string text)
    {
        Console.WriteLine(text);
        Log.Info("[设备自检] " + text);
    }

    /// <summary>输出一个空行（只进控制台，日志里不必留空行）。</summary>
    private static void Say() => Console.WriteLine();

    public static int Run(string[] args)
    {
        var seconds = 30;
        var positional = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (positional.Count > 0 && int.TryParse(positional[0], out var parsed))
            seconds = Math.Clamp(parsed, 5, 300);

        // MTA 初始化：**这是设备变更通知能否收到的前提**。
        // 实测：不初始化 MTA 时，即使主动切换默认设备（接口返回成功），
        // IMMNotificationClient 也一条回调都收不到 —— 热插拔失效的根因就在这里。
        // --sta 用于对比验证这个结论。
        if (!args.Contains("--sta", StringComparer.OrdinalIgnoreCase))
            ComApartment.InitializeMta();

        // --selftrigger：不依赖用户拔插，自己切换一次系统默认录音设备来验证通知链路。
        // 拔插类问题必须先证明"通知真的能收到"，否则用户拔了十次也只是白费力气。
        if (args.Contains("--selftrigger", StringComparer.OrdinalIgnoreCase))
            return RunSelfTrigger();

        using var devices = new DeviceService();

        var events = 0;
        var pending = false;
        using var signal = new AutoResetEvent(false);

        devices.DevicesChanged += (_, _) =>
        {
            Interlocked.Increment(ref events);
            pending = true;
            signal.Set();
        };

        // 读取配置里记的设备 ID —— 判断"重插后 ID 有没有变"就靠它
        var config = new ConfigStore().Load();

        Say("==== MateMic 设备热插拔自检 ====");
        Say($"监视时长：{seconds} 秒。请在此期间把麦克风拔下再插上。");
        Say();
        Say("配置里当前记着的设备 ID（只显示后 14 位，够看清有没有变）：");
        Say($"  输入 = {ShortId(config.Devices.InputDeviceId)}");
        Say($"  输出 = {ShortId(config.Devices.OutputDeviceId)}");
        Say($"  监听 = {ShortId(config.Devices.MonitorDeviceId)}");
        Say();
        Say("⚠ 判断方法：拔掉再插回之后，如果下面同一支麦克风的后 14 位 ID 变了，");
        Say("  说明系统给它分配了新 ID —— 这正是「必须手动重选一次设备」的根因。");
        Say();
        DumpSnapshot(devices, "起始状态");

        var lastRevision = devices.Revision;
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            signal.WaitOne(250);
            if (!pending) continue;

            pending = false;
            // 去抖：一次插拔会连发多条通知，等它们安静下来再看最终结果
            do
            {
                signal.WaitOne(350);
                pending = false;
            }
            while (pending);

            var revision = devices.Revision;
            Say($"[{DateTime.Now:HH:mm:ss.fff}] [设备事件] 代次 {lastRevision} → {revision}" +
                              $"（累计 {events} 条通知，已合并为 1 次处理）");
            lastRevision = revision;
            DumpSnapshot(devices, "事件后状态");

            // 事件后立刻核对：配置里记的那支输入设备还在不在
            var present = devices.IsPresent(config.Devices.InputDeviceId, DataFlow.Capture);
            Say($"  → 配置里的输入设备当前是否可用：{(present ? "是" : "否（ID 对不上或未插回）")}");
            Say();
        }

        Say("==== 监视结束 ====");
        DumpSnapshot(devices, "最终状态");
        Say($"共收到 {events} 条设备变更通知。");

        if (events == 0)
        {
            Say("⚠ 未收到任何变更通知：要么期间没有插拔，要么设备通知未能注册。");
            return 1;
        }

        Say("✔ 已收到设备变更通知，热插拔检测链路工作正常。");
        return 0;
    }

    /// <summary>
    /// 验证"设备变更通知能不能收到"：自己把默认录音设备切到另一台再切回来。
    /// 这会产生真实的 OnDefaultDeviceChanged 回调，不需要用户拔任何硬件。
    /// </summary>
    private static int RunSelfTrigger()
    {
        Say("==== 通知链路自测（不需要拔插硬件）====");

        using var devices = new DeviceService();
        var events = 0;
        using var signal = new AutoResetEvent(false);
        devices.DevicesChanged += (_, _) =>
        {
            Interlocked.Increment(ref events);
            signal.Set();
        };

        var capture = devices.Enumerate(DataFlow.Capture);
        var candidates = capture.Where(d => !d.IsDefault).ToList();
        var current = capture.FirstOrDefault(d => d.IsDefault);

        if (current == null || candidates.Count == 0)
        {
            Say("✗ 需要至少两台录音设备（一台当前默认 + 一台其它）才能做这个自测。");
            Say($"  当前录音设备 {capture.Count} 台，其中非默认 {candidates.Count} 台。");
            return 1;
        }

        Say($"当前默认录音设备：{current.Name}");
        Say($"将临时切换到：{candidates[0].Name}");

        var ok = DeviceSwitcher.SetDefault(candidates[0].Id, DataFlow.Capture);
        Say($"  切换调用结果：{ok}");

        var got = signal.WaitOne(3000);
        Say($"  3 秒内收到通知：{(got ? "是" : "否")}（累计 {events} 条）");

        // 无论成功与否都把默认设备切回去，别把用户的环境搞乱
        var restored = DeviceSwitcher.SetDefault(current.Id, DataFlow.Capture);
        signal.WaitOne(1500);
        Say($"  已恢复原默认设备（结果 {restored}）");

        Say();
        if (events > 0)
        {
            Say($"✔ 通知链路正常：收到 {events} 条设备变更通知。");
            Say("  那么拔插时收不到通知，就不是链路问题，而是拔插本身没有触发端点变更。");
            return 0;
        }

        Say("✗ 通知链路**没有**收到任何回调 —— 这才是真正的问题所在。");
        return 1;
    }

    /// <summary>只显示设备 ID 末尾一段：USB 设备的后半段含实例路径，重插后变了就是新设备。</summary>
    private static string ShortId(string? id)
        => string.IsNullOrWhiteSpace(id)
            ? "(未设置)"
            : id.Length <= 14 ? id : "…" + id[^14..];

    /// <summary>打印当前设备快照。</summary>
    private static void DumpSnapshot(DeviceService devices, string title)
    {
        Say($"---- {title} ----");
        Dump(devices, DataFlow.Capture, "录音设备");
        Dump(devices, DataFlow.Render, "播放设备");
    }

    private static void Dump(DeviceService devices, DataFlow flow, string label)
    {
        var list = devices.Enumerate(flow);
        Say($"  {label}（{list.Count}）：");
        foreach (var device in list)
        {
            Say($"    · {device.Name}{(device.IsDefault ? "  [系统默认]" : string.Empty)}" +
                              $"  {ShortId(device.Id)}");
        }

        if (list.Count == 0) Console.WriteLine("    （无）");
    }
}
