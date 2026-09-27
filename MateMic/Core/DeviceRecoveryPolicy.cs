namespace MateMic.Core;

/// <summary>
/// 收到设备变更后，音频流该怎么处理。
/// </summary>
public enum DeviceRecoveryAction
{
    /// <summary>设备集合没有任何变化，什么都不用做。</summary>
    None = 0,

    /// <summary>设备集合变了：重建音频流，让它对齐到"现在插着的设备"。</summary>
    RestartStream = 1,
}

/// <summary>
/// 设备热插拔的恢复决策。
///
/// 单独抽成一个纯函数，是为了让"什么时候该重启音频流"这条规则可以被回归探针确定性地覆盖：
/// 它只依赖布尔量，不碰 WASAPI、不需要真的拔插硬件。
/// 拔插类问题的现场验证成本极高（要有硬件、要有人真的去拔，而且系统通知在本机还收不到），
/// 把决策从 IO 里剥离出来，是把这类 bug 拦在提交前的唯一现实办法。
///
/// 演进过程（保留了结论，避免以后再绕回去）：
///   ① 最初是"流停了才重建、首选设备插回才夺回"——太保守。
///   ② 实测本机 <c>IMMNotificationClient</c> 回调完全收不到，于是加了轮询兜底；
///      但轮询只报"设备集合变了"，而保守策略会判定"设备没变（首选还在）→ 不重建"，
///      实际已经死掉的流就永远不被重建 → 用户仍要手动重选设备。
///   ③ 现在改为：**只要设备集合真的变了，就重建音频流**。
///      这与用户手动"选一个别的设备再选回来"所做的是同一件事，因此必定有效；
///      去抖（350ms）保证一次插拔只重建一次，代价是几百毫秒的短暂中断。
/// </summary>
public static class DeviceRecoveryPolicy
{
    /// <param name="deviceSetChanged">本次触发是否真的带来了设备集合变化。</param>
    /// <param name="streamRunning">音频流当前是否在运行。</param>
    /// <param name="onFallbackInput">当前是否临时跑在系统默认录音设备上（首选设备缺失）。</param>
    /// <param name="preferredInputPresent">配置里的首选输入设备当前是否插着。</param>
    public static DeviceRecoveryAction Decide(
        bool deviceSetChanged, bool streamRunning, bool onFallbackInput, bool preferredInputPresent)
    {
        // 设备集合变了就重建：无论流是否还在跑、首选设备是否插回。
        // 设备重插后底层端点往往已经不是原来那一个，只有重新打开流才是可靠的。
        if (deviceSetChanged) return DeviceRecoveryAction.RestartStream;

        // 集合没变，但流已经不在跑了（例如流因为设备错误自行停止）：也要重建。
        if (!streamRunning && preferredInputPresent) return DeviceRecoveryAction.RestartStream;

        // 集合没变、流在跑、但正跑在降级设备上而首选设备已经可用：夺回首选。
        if (streamRunning && onFallbackInput && preferredInputPresent)
            return DeviceRecoveryAction.RestartStream;

        return DeviceRecoveryAction.None;
    }
}
