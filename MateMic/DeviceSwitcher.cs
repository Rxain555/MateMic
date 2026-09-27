using System.Runtime.InteropServices;
using MateMic.Core;
using NAudio.CoreAudioApi;

namespace MateMic;

/// <summary>
/// 切换系统默认音频设备。
///
/// 用途：**验证设备变更通知链路**。拔插硬件才能验证通知有个致命缺点——
/// 收不到通知时无法区分"链路坏了"还是"用户没拔/拔了但没触发端点变更"。
/// 这里用系统接口主动制造一次真实的默认设备变更，链路好坏立刻可判。
///
/// 实现走 Windows 未公开的 <c>IPolicyConfig</c>（音效面板自己用的就是它）。
/// NAudio 3.1 没有提供等价 API，所以这里自己声明接口。
/// 接口**不能**改方法顺序：COM 虚表是按声明顺序排的，顺序错了会调到别的函数上。
/// </summary>
public static class DeviceSwitcher
{
    /// <summary>把指定设备设为默认设备。</summary>
    /// <param name="deviceId">设备 ID。方向由 ID 自身决定，不需要额外参数。</param>
    /// <param name="flow">仅用于日志与调用方语义，接口本身不区分方向。</param>
    public static bool SetDefault(string deviceId, DataFlow flow)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;

        try
        {
            var policy = (IPolicyConfig)new CPolicyConfigClient();

            // Role 0/1/2 = eConsole / eMultimedia / eCommunications。
            // 三个都设一遍，行为才与"在声音设置里设为默认设备"一致。
            var ok = policy.SetDefaultEndpoint(deviceId, ERole.eConsole) == 0;
            ok &= policy.SetDefaultEndpoint(deviceId, ERole.eMultimedia) == 0;
            ok &= policy.SetDefaultEndpoint(deviceId, ERole.eCommunications) == 0;

            Marshal.ReleaseComObject(policy);
            Log.Info($"已把默认{(flow == DataFlow.Capture ? "录音" : "播放")}设备切到 " + deviceId);
            return ok;
        }
        catch (Exception ex)
        {
            Log.Warn("切换默认音频设备失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>IPolicyConfig 的角色枚举（三种"默认设备"用途）。</summary>
    private enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2,
    }

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class CPolicyConfigClient
    {
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        // 方法顺序必须与真实虚表一致，只为用到最后那个 SetDefaultEndpoint
        [PreserveSig] int GetMixFormat(string pszDeviceName, IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat(string pszDeviceName, bool bDefault, IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat(string pszDeviceName);
        [PreserveSig] int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string pszDeviceName, bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string pszDeviceName, IntPtr pmftPeriod);
        [PreserveSig] int GetShareMode(string pszDeviceName, IntPtr pMode);
        [PreserveSig] int SetShareMode(string pszDeviceName, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ERole role);
        [PreserveSig] int SetEndpointVisibility(string pszDeviceName, bool bVisible);
    }
}
