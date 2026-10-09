using System.Runtime.InteropServices;

namespace MateMic.Core;

/// <summary>
/// 问 Windows："现在屏幕上是不是有全屏应用（游戏 / 演示）在跑？"
///
/// 为什么要问它：MateMic 的界面刷新（30Hz 频谱 + 电平条）是靠 <c>DispatcherTimer</c> 驱动的。
/// 窗口**被别的全屏应用盖住**时，WPF 的 <c>IsVisible</c> 仍然是 true（它只表示"窗口没被隐藏"，
/// 不表示"没被遮挡"）—— 于是游戏全屏时我们还在一帧一帧刷看不见的东西。
///
/// 判断手段用官方的 <c>SHQueryUserNotificationState</c>：它本来就是给"要不要弹通知"用的，
/// 会告诉我们当前是不是 D3D 全屏 / 普通全屏 / 演示模式 / 用户不在。
/// 比自己拿 GetWindowRect 去猜有没有被盖住可靠得多（多显示器、置顶窗口、UAC 桌面都能正确区分）。
///
/// ⚠ 只影响**界面渲染**：音频处理跑在音频线程上，与这里无关（见 MainWindow.OnRenderTick 的注释）。
/// </summary>
public static class ShellState
{
    // SHQueryUserNotificationState 的返回值（shellapi.h）
    private const int QunsNotPresent = 1;             // 用户不在：锁屏 / 屏保
    private const int QunsBusy = 2;                   // 全屏应用在跑（非 D3D）
    private const int QunsRunningD3DFullScreen = 3;   // D3D 全屏应用在跑（游戏）
    private const int QunsPresentationMode = 4;       // 演示模式
    // 5 = QUNS_ACCEPTS_NOTIFICATIONS（正常）、6 = QUNS_QUIET_TIME（新装机器的静默时段）—— 都照常渲染

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>
    /// true = 现在不该刷界面（有全屏应用 / 演示 / 锁屏）。
    /// 查询失败时一律返回 false（宁可多刷，也不要因为一个 API 失败把界面冻住）。
    /// </summary>
    public static bool ShouldSkipRendering()
    {
        try
        {
            if (SHQueryUserNotificationState(out var state) != 0) return false;
            return state is QunsNotPresent or QunsBusy or QunsRunningD3DFullScreen or QunsPresentationMode;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
