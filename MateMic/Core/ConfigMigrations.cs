namespace MateMic.Core;

/// <summary>
/// 配置迁移：把老版本的 <c>config.json</c> 补成当前语义。
///
/// **只做一次性、不可逆的语义切换**（"旧默认值"变成"新默认值"这一类），
/// 不做"顺手改用户设置"——凡是用户能主动改、且已经能明确表达意图的项，迁移一律不碰。
///
/// 每加一条迁移都必须在注释里写清三件事：
///   ① 改了什么；② 为什么可以认定老值是"旧默认值"而不是用户的选择；③ 为什么只做一次。
/// </summary>
public static class ConfigMigrations
{
    /// <summary>
    /// 当前配置结构版本。**改变 <see cref="AppConfig"/> 中任何字段的语义时 +1**，
    /// 并在 <see cref="Apply"/> 里补一条对应迁移。
    /// </summary>
    public const int Current = 3;

    /// <summary>
    /// 就地把配置补到 <see cref="Current"/>。幂等：已经是当前版本的配置原样返回。
    /// 由 <c>ConfigStore.Load</c> 在反序列化之后立即调用，因此后续逻辑拿到的都是最新语义。
    /// </summary>
    public static void Apply(AppConfig config)
    {
        if (config.Version >= Current)
        {
            config.Version = Current;
            return;
        }

        // ---- v1 → v2：深色配色由「默认关闭」改为「默认开启」 ----
        //
        // 可以认定老值是默认值而不是用户选择：v1 时期界面上没有任何引导，浅色就是开箱状态，
        // 绝大多数配置里的 false 从来没被用户碰过。用户 2026-10-05 明确要求「默认打开深色模式」，
        // 若只改字段默认值，老配置里的 false 会继续生效、用户看不到任何变化——
        // 那等于这个需求根本没实现。
        //
        // 只做一次：迁移后 Version 变成 2，此后用户若主动关掉深色，不会再被改写。
        if (config.Version < 2) config.DarkMode = true;

        // ---- v2 → v3：均衡器由"只能选预设"改为"10 段可手动调" ----
        //
        // v3 之前根本没有 Gains 字段，所以老配置里的全 0 dB **一定是"字段不存在"**
        // 而不是"用户把均衡器调平了"（那时没有任何手动调节入口，调不出全 0）。
        // 因此按当时选中的预设把曲线补回去，用户升级后听到的仍是同一个音色。
        // 之后 Style 可能为 null（手动模式），这条迁移也不会再碰它（Version 已是 3）。
        if (config.Version < 3)
        {
            if (config.Tone.Style.HasValue && EqPreset.IsFlat(config.Tone.Gains))
                config.Tone.Gains = EqPreset.GainsOf(config.Tone.Style.Value);
        }

        config.Version = Current;
    }
}
