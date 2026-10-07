using System.Reflection;
using System.Runtime.InteropServices;
using MateMic.Core;

namespace MateMic.Ai;

/// <summary>AI 变声组件的安装状态。</summary>
public enum AiComponentState
{
    /// <summary>未安装：data\components\ai 不存在或缺关键文件。</summary>
    NotInstalled,

    /// <summary>已安装但引擎/音色不全，无法启用。</summary>
    Incomplete,

    /// <summary>可用。</summary>
    Ready,
}

public sealed record AiComponentStatus(
    AiComponentState State,
    string Message,
    bool HasRuntime,
    bool HasEngine,
    int VoiceCount,
    int IndexCount);

/// <summary>
/// AI 变声组件的检测与原生运行时装配。
///
/// 设计要点：**基础版不含任何 AI 变声组件**（安装包维持几十 MB），
/// 用户按需下载组件包、解压进 <c>data\components\ai\</c>，程序检测到完整即可用。
/// 目录结构见 <see cref="ConfigStore.AiComponentDirectory"/>。
///
/// 两个关键机制：
///
/// <b>1. 原生运行时按需切换（DllImportResolver）</b><br/>
/// 基础版引用的是 CPU 版 onnxruntime，它**根本不含 CUDA</b>；而 CPU/GPU 版的
/// 原生库都叫 <c>onnxruntime.dll</c>，同一进程只能加载一个，且 .NET 默认优先走
/// <c>deps.json</c> 里的 runtimes 路径，运行时"换成 data\ 里的那个"是做不到的。
/// 正解是用 <see cref="NativeLibrary.SetDllImportResolver"/> 自己决定
/// <c>onnxruntime</c> 这个名字解析到哪个文件：组件完整就指向组件里的 GPU 版，
/// 否则交回默认解析（CPU 版）。
///
/// <b>2. 降噪绝不碰 GPU</b><br/>
/// GPU 版只是"支持 CUDA"，不创建 CUDA 会话就不占 GPU。所有降噪会话都显式
/// 钉死 <c>CPUExecutionProvider</c>（见 ModelCatalog.CreateSession），
/// 因此用户一边打游戏一边开降噪不会被抢显卡。
///
/// ⚠ 组件里的 <c>onnxruntime.dll</c> 必须与程序引用的 managed 层**版本一致**，
/// 否则行为不可预期；<see cref="Inspect"/> 会校验版本并给出提示。
/// </summary>
public static class AiComponent
{
    /// <summary>原生运行时必需的文件（缺任一即视为组件不完整）。</summary>
    private static readonly string[] RuntimeFiles =
    {
        "onnxruntime.dll",
        "onnxruntime_providers_cuda.dll",
        "cublasLt64_12.dll",
        "cublas64_12.dll",
        // ⚠ cudart64_12.dll 极易被漏掉：它是 CUDA Runtime，providers_cuda 的必需依赖。
        // 少了它时错误是 "Error 1114：DLL 初始化例程失败"（找到了但初始化失败），
        // 而不是"找不到"，很容易误判成版本不兼容。
        "cudart64_12.dll",
        "cudnn64_9.dll",
        "cudnn_ops64_9.dll",
        "cudnn_adv64_9.dll",
        "cudnn_cnn64_9.dll",
        "cudnn_engines_precompiled64_9.dll",
        "cudnn_graph64_9.dll",
        "cudnn_heuristic64_9.dll",
        "nvrtc64_120_0.dll",
        "nvJitLink_120_0.dll",
    };

    /// <summary>推理引擎必需的文件。</summary>
    private static readonly string[] EngineFiles = { "contentvec.onnx", "rmvpe.onnx" };

    /// <summary>程序引用的 managed onnxruntime 版本，用于校验组件里的原生库。</summary>
    public static Version? ManagedRuntimeVersion =>
        typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly.GetName().Version;

    private static bool _resolverInstalled;
    private static bool _nativeLoaded;
    private static string? _lastError;

    /// <summary>上一次装配原生运行时的失败原因（null 表示没失败）。</summary>
    public static string? LastError => _lastError;

    /// <summary>扫描组件目录，返回安装状态。</summary>
    public static AiComponentStatus Inspect()
    {
        var root = ConfigStore.AiComponentDirectory;
        if (!Directory.Exists(root))
            return new AiComponentStatus(AiComponentState.NotInstalled,
                "未安装 AI 变声组件", false, false, 0, 0);

        var missingRuntime = MissingFiles(ConfigStore.AiRuntimeDirectory, RuntimeFiles);
        var missingEngine = MissingFiles(ConfigStore.AiEngineDirectory, EngineFiles);
        var voices = CountFiles(ConfigStore.AiVoicesDirectory, "*.onnx");
        var indexes = CountFiles(ConfigStore.AiIndexDirectory, "*.index");

        if (missingRuntime.Count > 0)
            return new AiComponentStatus(AiComponentState.Incomplete,
                $"运行时缺 {missingRuntime.Count} 个文件（如 {missingRuntime[0]}）", false, missingEngine.Count == 0, voices, indexes);

        if (missingEngine.Count > 0)
            return new AiComponentStatus(AiComponentState.Incomplete,
                $"推理引擎缺 {missingEngine.Count} 个文件（如 {missingEngine[0]}）", true, false, voices, indexes);

        if (voices == 0)
            return new AiComponentStatus(AiComponentState.Incomplete,
                "还没有音色模型（请把音色 .onnx 放进 voices\\）", true, true, 0, indexes);

        return new AiComponentStatus(AiComponentState.Ready,
            $"组件就绪：{voices} 个音色{(indexes > 0 ? $"，{indexes} 个索引" : "")}",
            true, true, voices, indexes);
    }

    private static List<string> MissingFiles(string directory, string[] names)
    {
        var missing = new List<string>();
        if (!Directory.Exists(directory))
        {
            missing.AddRange(names);
            return missing;
        }
        foreach (var n in names)
            if (!File.Exists(Path.Combine(directory, n))) missing.Add(n);
        return missing;
    }

    private static int CountFiles(string directory, string pattern)
        => Directory.Exists(directory) ? Directory.GetFiles(directory, pattern).Length : 0;

    /// <summary>
    /// 安装 DllImport 解析器，让 <c>onnxruntime</c> 优先解析到组件里的 GPU 版。
    ///
    /// **必须在任何 ONNX 调用之前调用**（降噪的模型扫描就会建会话），
    /// 因此由 App 启动时最先执行。组件不完整时什么都不做，交回默认解析（CPU 版）。
    /// </summary>
    public static void InstallNativeResolver()
    {
        if (_resolverInstalled) return;
        _resolverInstalled = true;

        try
        {
            NativeLibrary.SetDllImportResolver(
                typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly,
                ResolveNativeLibrary);

            var status = Inspect();
            Log.Info($"[AI 变声] 组件状态：{status.State}（{status.Message}）；"
                     + $"managed onnxruntime {ManagedRuntimeVersion}");
        }
        catch (InvalidOperationException)
        {
            // 解析器只能设置一次；重复调用（如自检与正常启动都走一遍）时忽略
        }
    }

    private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName is not ("onnxruntime" or "onnxruntime.dll"))
            return IntPtr.Zero;                       // 其它库交回默认解析

        var runtimeDir = ConfigStore.AiRuntimeDirectory;
        var candidate = Path.Combine(runtimeDir, "onnxruntime.dll");
        if (!File.Exists(candidate))
            return IntPtr.Zero;                       // 没装组件 -> 用程序自带的 CPU 版

        try
        {
            // 让 CUDA 运行时（cublas/cudnn/nvrtc…）与 providers_cuda 能被找到。
            //
            // ⚠ 必须用 AddDllDirectory + SetDefaultDllDirectories，**不能用 SetDllDirectory**：
            // ONNX Runtime 加载 onnxruntime_providers_cuda.dll 时走的是带
            // LOAD_LIBRARY_SEARCH_* 标志的 LoadLibraryEx，那条路径**不接受 SetDllDirectory**
            // 设置的目录，只认 AddDllDirectory 注册的 USER_DIRS。
            // （实测：用 SetDllDirectory 时 onnxruntime.dll 本身加载成功，但创建 CUDA 会话报
            //   "OrtSessionOptionsAppendExecutionProvider_Cuda: Failed to load shared library"。）
            SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
            AddDllDirectory(runtimeDir);

            var handle = NativeLibrary.Load(candidate);
            _nativeLoaded = true;
            Log.Info($"[AI 变声] 已加载组件内的 GPU 版运行时：{candidate}");
            return handle;
        }
        catch (Exception ex)
        {
            _lastError = $"加载组件运行时失败：{ex.Message}";
            Log.Warn($"[AI 变声] {_lastError}；已退回 CPU 版 onnxruntime，程序仍可正常运行");
            return IntPtr.Zero;                       // 退回 CPU 版，程序仍能跑
        }
    }

    /// <summary>组件里的 GPU 版原生库是否真的被加载了。</summary>
    public static bool NativeLoaded => _nativeLoaded;

    private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);
}
