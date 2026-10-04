using System.IO;
using Microsoft.ML.OnnxRuntime;
using MateMic.Core;

namespace MateMic.Ai;

/// <summary>
/// AI 推理会话的统一创建入口：默认优先 **DirectML（GPU）**，不可用时自动退回 CPU。
///
/// 为什么必须用 GPU：本地实测（CPU）内容编码器处理 2 秒音频 68 ms、基频模型 20 帧 90 ms、
/// 音色模型 100 帧 262 ms，累加后的实时率撑不住直播；DirectML 不绑定 N 卡，
/// A / N / Intel 显卡都能用，符合本项目"不要求特定显卡"的取舍。
///
/// 注意：**降噪那条链仍然走 CPU**（它已经验收过，不因为换包而改变行为），
/// 所以 provider 由调用方决定，这里不设默认值。
/// </summary>
public static class OnnxSessionFactory
{
    /// <summary>GPU 是否可用（第一次尝试后会记录结果，避免反复失败重试）。</summary>
    public static bool GpuAvailable { get; private set; } = true;

    public static InferenceSession Create(string path, bool useGpu, int intraOpThreads = 0)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到模型文件", path);

        if (useGpu && GpuAvailable)
        {
            try
            {
                var gpuOptions = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                };
                if (intraOpThreads > 0) gpuOptions.IntraOpNumThreads = intraOpThreads;
                gpuOptions.AppendExecutionProvider_DML(0);

                var gpuSession = new InferenceSession(path, gpuOptions);
                Log.Info($"AI：{Path.GetFileName(path)} 使用 DirectML（GPU）");
                return gpuSession;
            }
            catch (Exception ex)
            {
                // 常见原因：没有 DX12 设备、显卡驱动不支持、显存不足
                GpuAvailable = false;
                Log.Warn($"AI：DirectML 不可用，后续模型都退回 CPU（{ex.Message}）");
            }
        }

        var cpuOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        if (intraOpThreads > 0) cpuOptions.IntraOpNumThreads = intraOpThreads;

        var cpuSession = new InferenceSession(path, cpuOptions);
        Log.Info($"AI：{Path.GetFileName(path)} 使用 CPU");
        return cpuSession;
    }
}
