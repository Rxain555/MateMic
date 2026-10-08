using Microsoft.ML.OnnxRuntime;

namespace MateMic.Ai;

/// <summary>
/// 给 ONNX 会话挂执行提供器。
///
/// **目前只支持 CUDA**（2026-10-08 用户决定）：实测 DirectML 跑不了 RVC
/// （合成器的 ConvTranspose 节点报 E_INVALIDARG），CPU 慢到无法实用
/// （三个会话十秒都建不完），所以那两个后端连同界面上的"运算方式"下拉一起下掉了。
///
/// 保留这一层而不是把 AppendExecutionProvider_CUDA 直接写进三个会话类，是因为：
/// 后端文件在 providers\&lt;名字&gt;\ 下，将来要再支持别的后端时只改这里。
/// 挂载失败也不再让引擎建不起来 —— 记一条警告继续（有声音总比没声音好）。
/// </summary>
internal static class OrtProviders
{
    public static void Append(SessionOptions options)
    {
        try
        {
            options.AppendExecutionProvider_CUDA(0);
        }
        catch (Exception ex)
        {
            Core.Log.Warn("[AI 变声] 挂载 CUDA 执行提供器失败，本会话退回 CPU：" + ex.Message);
        }
    }
}