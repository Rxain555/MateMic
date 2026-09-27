using Microsoft.Win32;

namespace MateMic.Core;

/// <summary>开机自启：写入 HKCU\...\Run，无需管理员权限。</summary>
public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MateMic";

    /// <summary>改名前的自启项名（MicMate）。用于升级时平滑接续，顺手清掉旧值。</summary>
    private const string LegacyValueName = "MicMate";

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

    /// <summary>当前是否残留着旧名字（MicMate）的自启项。</summary>
    public static bool HasLegacyEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(LegacyValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 改名迁移：老版本开过自启的用户，这里把自启项改写成新名字与新 exe 路径，
    /// 并删除旧值，避免开机时去启动一个已经不存在（或已被删掉）的旧 exe。
    /// 只在"旧值存在且新值不存在"时动作，绝不覆盖用户当前设置。
    /// </summary>
    public static void MigrateLegacyEntry()
    {
        try
        {
            // 先只读检查：绝大多数用户没有旧的自启项，这种情况下连写权限都不该去碰，
            // 否则每次启动都会在日志里留下一条"注册表访问被拒"的假警告。
            using var readable = Registry.CurrentUser.OpenSubKey(RunKey, false);
            if (readable?.GetValue(LegacyValueName) == null) return;
            if (readable.GetValue(ValueName) != null) return;

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            using var writable = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (writable == null) return;

            writable.SetValue(ValueName, $"\"{exe}\" --autostart");
            writable.DeleteValue(LegacyValueName, false);
            Log.Info("已把旧版（MicMate）的开机自启项迁移到 MateMic。");
        }
        catch (Exception ex)
        {
            Log.Warn("迁移旧版开机自启项失败：" + ex.Message);
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
                // 顺手清理旧名字残留，否则关掉自启后旧项还会在开机时拉起旧 exe
                key.DeleteValue(LegacyValueName, false);
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
