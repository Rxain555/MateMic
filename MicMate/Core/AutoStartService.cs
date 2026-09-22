using Microsoft.Win32;

namespace MicMate.Core;

/// <summary>开机自启：写入 HKCU\...\Run，无需管理员权限。</summary>
public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MicMate";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) != null;
        }
        catch (Exception ex)
        {
            Log.Warn("读取开机自启状态失败：" + ex.Message);
            return false;
        }
    }

    public static bool Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return false;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;
                key.SetValue(ValueName, $"\"{exe}\" --autostart");
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }

            Log.Info("开机自启已" + (enabled ? "开启" : "关闭"));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("设置开机自启失败", ex);
            return false;
        }
    }
}
