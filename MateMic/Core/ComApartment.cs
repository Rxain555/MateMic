using System.Runtime.InteropServices;
using MateMic.Core;

namespace MateMic;

/// <summary>
/// COM 单元状态初始化。
///
/// **这是设备热插拔能否工作的前提**，不是可选项。
/// 实测结论（本机 Windows 11 25H2 / .NET 9）：
/// 不把线程初始化成 MTA 时，<c>MMDeviceEnumerator.CreateNotificationClient()</c>
/// 看起来注册成功（不抛异常），但 <c>IMMNotificationClient</c> 的回调
/// **一条都不会到达** —— 连主动切换系统默认设备都收不到通知。
/// 结果就是"拔掉麦克风 → 插回来"永远不会触发任何自动恢复动作，
/// 用户只能手动重选一次设备。
///
/// 副作用与安全性：MTA 一旦设定就不能改回 STA，而 WPF 的 UI 线程**必须**是 STA。
/// 因此这里**只在当前线程还不是 STA 时才初始化**，绝不把 UI 线程改成 MTA。
/// 需要通知功能的非 UI 线程（例如诊断工具的主线程）会因此正确初始化。
/// </summary>
public static class ComApartment
{
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    private const uint CoInitMultithreaded = 0x0;   // COINIT_MULTITHREADED
    private const uint CoInitApartmentThreaded = 0x2; // COINIT_APARTMENTTHREADED

    /// <summary>把当前线程初始化为 MTA（已经是 STA 的线程会被跳过）。</summary>
    public static void InitializeMta()
    {
        try
        {
            var hr = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
            if (hr == RpcEChangedMode)
            {
                // 线程已经是 STA（例如 WPF UI 线程）：绝不强行改，改了会让界面出问题
                Log.Info("COM：当前线程已是 STA，保持不动（设备通知由其它线程负责）。");
                return;
            }

            Log.Info(hr == 0
                ? "COM：已把当前线程初始化为 MTA，设备变更通知可正常接收。"
                : $"COM：CoInitializeEx(MTA) 返回 0x{hr:X8}（已初始化过或无需初始化）。");
        }
        catch (Exception ex)
        {
            Log.Warn("COM 初始化失败（可能影响设备热插拔通知）：" + ex.Message);
        }
    }

    /// <summary>把当前线程初始化为 STA（仅诊断对比用）。</summary>
    public static void InitializeSta()
    {
        try
        {
            var hr = CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);
            Log.Info($"COM：CoInitializeEx(STA) 返回 0x{hr:X8}。");
        }
        catch (Exception ex)
        {
            Log.Warn("COM(STA) 初始化失败：" + ex.Message);
        }
    }
}
