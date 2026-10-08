using Microsoft.ML.OnnxRuntime;

namespace MateMic.Ai;

/// <summary>
/// 按**当前生效的运算后端**给 ONNX 会话挂执行提供器。
///
/// **为什么必须统一在这里做**：三个后端用的是三份不同的 <c>onnxruntime.dll</c> ——
/// CUDA 版自带 CUDA provider，DirectML 版内建 DML，CPU 版两者都没有。
/// 如果代码里无条件调 <c>AppendExecutionProvider_CUDA</c>，
/// 那么切到 CPU / DirectML 时**挂载会抛异常**（那个 runtime 里根本没有 CUDA），
/// 引擎创建直接失败 —— 表现为"切到 CPU 和 DirectML 都没声音"
///（2026-10-08 用户实测）。
///
/// 之前每个会话类各写一句 <c>if (useGpu) AppendExecutionProvider_CUDA(0)</c>，
/// 把"是否用 GPU"当成了布尔值；但真实的维度是"用哪个后端"，所以抽到这里按后端名分派。
/// </summary>
internal static class OrtProviders
{
    /// <summary>按 <see cref="AiComponent.ActiveProvider"/> 追加对应的执行提供器。</summary>
    public static void Append(SessionOptions options)
    {
        var provider = AiComponent.ActiveProvider;
        try
        {
            switch (provider)
            {
                case "cuda":
                    options.AppendExecutionProvider_CUDA(0);
                    break;

                case "directml":
                    options.AppendExecutionProvider_DML(0);
                    break;

                case "cpu":
                default:
                    // CPU 后端不需要追加任何东西（onnxruntime 默认就在 CPU 上跑）
                    break;
            }
        }
        catch (Exception ex)
        {
            // 挂不上就退回 CPU 跑，而不是让整个引擎建不起来 —— 有声音总比没声音好
            Core.Log.Warn($"[AI 变声] 挂载 {provider} 执行提供器失败，本会话退回 CPU：{ex.Message}");
        }
    }

    /// <summary>当前后端是否为 GPU（用于日志与界面显示）。</summary>
    public static bool IsGpu => AiComponent.ActiveProvider is "cuda" or "directml";
}
