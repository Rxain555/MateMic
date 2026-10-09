using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MateMic.Core;

/// <summary>
/// 「延迟与性能」卡片底部的资源占用采样：**本进程 CPU 占用** + **显卡利用率**。
///
/// 为什么要显示它们：用户调「音频块 / 输出缓冲」时是在拿延迟换稳定性，而"能不能换到"
/// 取决于这台机器还剩多少余量 —— 界面上原来只有模块自己的负载（占它自己处理周期的比例），
/// 看不出整机吃了多少（2026-10-09 用户要求补一行）。
///
/// CPU 是**本进程**的（相对全机：所有核心都跑满 = 100%）。
/// GPU 走 NVML，读到的是**显卡整体利用率**（与任务管理器那一列口径一致）——
/// 不是"本进程占显卡的百分比"：NVML 不提供按进程的利用率，而按进程的
/// "GPU Engine" 性能计数器要额外引 System.Diagnostics.PerformanceCounter 包、
/// 且每次枚举实例的开销足以让 UI 卡顿。绝大多数时候只有本程序在用显卡，整体值就够看。
/// 非 NVIDIA 环境（没有 nvml.dll）显示 "—"，不影响其它功能。
/// </summary>
public static class ResourceUsage
{
    /// <summary>本进程 CPU 占用（0~100，相对全机所有核心）。</summary>
    public static double CpuPercent { get; private set; }

    /// <summary>显卡整体利用率（0~100）；null = 读不到（非 N 卡 / 驱动没提供 NVML）。</summary>
    public static double? GpuPercent { get; private set; }

    private static TimeSpan _lastCpu;
    private static long _lastTicks;
    private static bool _primed;

    /// <summary>
    /// 采一次样。调用频率跟着界面刷新（约 0.5 秒一次）就够 ——
    /// CPU 占用是两次采样的差值算出来的，采得太密反而抖动大。
    /// 开销极小（读一次进程计时器 + 一次 NVML 调用），不必另起线程。
    /// </summary>
    public static void Sample()
    {
        SampleCpu();
        GpuPercent = Nvml.TryReadGpuPercent();
    }

    private static void SampleCpu()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var now = Stopwatch.GetTimestamp();

            if (_primed)
            {
                var wallMs = (now - _lastTicks) * 1000.0 / Stopwatch.Frequency;
                var cpuMs = (cpu - _lastCpu).TotalMilliseconds;
                if (wallMs >= 1)
                {
                    // 除以逻辑核心数：这样"单核跑满"在 16 核机器上是 6.25%，
                    // 与任务管理器"CPU"列的语义一致
                    var percent = cpuMs / wallMs / Environment.ProcessorCount * 100.0;
                    CpuPercent = Math.Clamp(percent, 0.0, 100.0);
                }
            }

            _lastCpu = cpu;
            _lastTicks = now;
            _primed = true;
        }
        catch (Exception ex)
        {
            Log.Warn("[资源] 读取本进程 CPU 占用失败：" + ex.Message);
        }
    }

    /// <summary>
    /// NVIDIA 的 NVML：<c>nvml.dll</c> 随显卡驱动安装（通常在 System32）。
    /// 只用到三个函数，全部 P/Invoke，不引任何包。
    /// </summary>
    private static class Nvml
    {
        private static bool _tried;
        private static bool _ready;
        private static IntPtr _device;

        public static double? TryReadGpuPercent()
        {
            if (!_tried) Init();
            if (!_ready) return null;

            try
            {
                return NvmlDeviceGetUtilizationRates(_device, out var rates) == 0
                    ? rates.Gpu
                    : null;
            }
            catch
            {
                _ready = false;      // 驱动被卸载/重启之类：退化成 "—"，不再反复抛
                return null;
            }
        }

        private static void Init()
        {
            _tried = true;
            try
            {
                if (NvmlInitV2() != 0) return;
                if (NvmlDeviceGetHandleByIndexV2(0, out _device) != 0) return;
                _ready = true;
                Log.Info("[资源] 已接入 NVML，可读取显卡利用率");
            }
            catch (DllNotFoundException)
            {
                Log.Info("[资源] 没有 nvml.dll（非 NVIDIA 环境），GPU 占用将显示为 —");
            }
            catch (EntryPointNotFoundException)
            {
                Log.Warn("[资源] nvml.dll 缺少所需导出函数，GPU 占用将显示为 —");
            }
            catch (Exception ex)
            {
                Log.Warn("[资源] NVML 初始化失败，GPU 占用将显示为 —：" + ex.Message);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Utilization
        {
            public uint Gpu;
            public uint Memory;
        }

        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NvmlInitV2();

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NvmlDeviceGetHandleByIndexV2(uint index, out IntPtr device);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NvmlDeviceGetUtilizationRates(IntPtr device, out Utilization rates);
    }
}
