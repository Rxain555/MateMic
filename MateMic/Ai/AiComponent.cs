using System.IO.Compression;
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
    int IndexCount,
    string? Provider = null,
    string? ProviderDisplay = null,
    IReadOnlyList<string>? AvailableProviders = null);

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
    /// <summary>
    /// 通用运行时必需的文件（放 <c>runtime\</c> 下）。
    /// 这些与运算后端无关：索引桥接层 + 它的 faiss/OpenBLAS 依赖。
    /// </summary>
    private static readonly string[] RuntimeFiles =
    {
        "mm_faiss.dll",
        "faiss.dll",
        "libopenblas.dll",
        "vcomp140.dll",
    };

    /// <summary>推理引擎必需的文件（放 <c>engine\</c> 下）。</summary>
    private static readonly string[] EngineFiles = { "contentvec.onnx", "rmvpe.onnx" };

    /// <summary>
    /// 一个运算后端的规格：目录名（<c>providers\&lt;Name&gt;\</c>）、界面显示名、必需文件。
    ///
    /// ⚠ <c>onnxruntime.dll</c> 属于后端而**不是**通用部分：
    /// CPU / CUDA / DirectML 三个版本是**三份不同的文件**（CUDA 版还要配 provider DLL，
    /// DirectML 版则内建）。所以它跟着后端目录走，换后端只换一份目录。
    ///
    /// **顺序即优先级**：数组靠前的先被选中，所以 GPU 后端排在 CPU 前面。
    /// </summary>
    private sealed record ProviderSpec(string Name, string Display, string[] Files);

    private static readonly ProviderSpec[] Providers =
    {
        new("cuda", "CUDA（NVIDIA 显卡）", new[]
        {
            "onnxruntime.dll",
            "onnxruntime_providers_cuda.dll",
            "onnxruntime_providers_shared.dll",
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
        }),
    };

    /// <summary>程序引用的 managed onnxruntime 版本，用于校验组件里的原生库。</summary>
    public static Version? ManagedRuntimeVersion =>
        typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly.GetName().Version;

    private static bool _resolverInstalled;
    private static bool _nativeLoaded;
    private static string? _lastError;
    private static string? _activeProvider;

    /// <summary>用户指定的运算方式（"auto" 或具体后端名）。</summary>

    /// <summary>当前生效的运算后端名（cuda / directml / cpu），未装配时为 null。</summary>
    public static string? ActiveProvider => _activeProvider;

    /// <summary>上一次装配原生运行时的失败原因（null 表示没失败）。</summary>
    public static string? LastError => _lastError;

    /// <summary>扫描组件目录，返回安装状态。</summary>
    public static AiComponentStatus Inspect()
    {
        var root = ConfigStore.AiComponentDirectory;
        if (!Directory.Exists(root))
            return new AiComponentStatus(AiComponentState.NotInstalled,
                "未安装 AI 变声组件", false, false, 0, 0);

        var installed = InstalledProviders();

        var missingRuntime = MissingFiles(ConfigStore.AiRuntimeDirectory, RuntimeFiles);
        var missingEngine = MissingFiles(ConfigStore.AiEngineDirectory, EngineFiles);
        var voices = CountFiles(ConfigStore.AiVoicesDirectory, "*.onnx");
        var indexes = CountFiles(ConfigStore.AiIndexDirectory, "*.index");

        if (missingRuntime.Count > 0)
            return new AiComponentStatus(AiComponentState.Incomplete,
                $"通用运行时缺 {missingRuntime.Count} 个文件（如 {missingRuntime[0]}）",
                false, missingEngine.Count == 0, voices, indexes, null, null, installed);

        if (missingEngine.Count > 0)
            return new AiComponentStatus(AiComponentState.Incomplete,
                $"推理引擎缺 {missingEngine.Count} 个文件（如 {missingEngine[0]}）",
                true, false, voices, indexes, null, null, installed);

        // 挑一个完整可用的后端（目前只有 CUDA）
        var spec = ResolveProvider();
        if (spec == null)
        {
            var detail = installed.Count == 0 ? "（未装运算组件）" : "（已装：" + string.Join("、", installed) + "）";
            return new AiComponentStatus(AiComponentState.Incomplete,
                $"缺少运算组件{detail}",
                true, true, voices, indexes, null, null, installed);
        }

        if (voices == 0)
            return new AiComponentStatus(AiComponentState.Incomplete,
                "还没有音色模型（请把音色 .onnx 放进 voices\\）",
                true, true, 0, indexes, spec.Name, spec.Display, installed);

        return new AiComponentStatus(AiComponentState.Ready,
            $"组件就绪：{voices} 个音色{(indexes > 0 ? $"，{indexes} 个索引" : "")}，后端 {spec.Display}",
            true, true, voices, indexes, spec.Name, spec.Display, installed);
    }

    /// <summary>返回第一个完整可用的后端；没有则返回 null。</summary>
    private static ProviderSpec? ResolveProvider()
    {
        // 目前只有 CUDA 一种后端；数组顺序即优先级（将来加别的后端时排在这里）
        foreach (var spec in Providers)
            if (MissingFiles(ConfigStore.AiProviderDirectory(spec.Name), spec.Files).Count == 0)
                return spec;
        return null;
    }

    /// <summary>已完整安装的后端名列表（供界面下拉显示哪些可用）。</summary>
    public static IReadOnlyList<string> InstalledProviders()
    {
        var list = new List<string>();
        foreach (var spec in Providers)
            if (MissingFiles(ConfigStore.AiProviderDirectory(spec.Name), spec.Files).Count == 0)
                list.Add(spec.Name);
        return list;
    }

    /// <summary>后端名 → 界面显示名。</summary>
    public static string ProviderDisplayName(string name)
    {
        foreach (var spec in Providers)
            if (string.Equals(spec.Name, name, StringComparison.OrdinalIgnoreCase))
                return spec.Display;
        return name;
    }

    /// <summary>
    /// 安装一个组件包（.zip）。组件包内部结构是
    /// <c>ai\runtime\…</c> / <c>ai\providers\cuda\…</c> / <c>ai\engine\…</c>，
    /// 这里取 <c>ai\</c> 之后的相对路径拼到组件目录下，
    /// 于是通用包与各后端包可以分别解压、自然叠加。
    /// 返回实际写入的文件数；抛异常表示解压失败。
    /// </summary>
    public static int InstallFromZip(string zipPath)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var root = ConfigStore.AiComponentDirectory;
        Directory.CreateDirectory(root);

        var written = 0;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;          // 目录项

            var relative = entry.FullName.Replace('/', '\\');
            var idx = relative.IndexOf("ai\\", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) relative = relative[(idx + 3)..];
            if (relative.Length == 0) continue;

            // 只接受已知的顶层子目录，避免组件包里的杂项污染目录
            var top = relative.Split('\\')[0];
            if (top is not ("runtime" or "providers" or "engine")) continue;

            var target = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            written++;
        }
        return written;
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

        // 让 cuDNN / DirectML / OpenMP 这些依赖能被找到。
        //
        // ⚠ 必须用 AddDllDirectory + SetDefaultDllDirectories，**不能用 SetDllDirectory**：
        // ONNX Runtime 加载 onnxruntime_providers_cuda.dll 时走的是带
        // LOAD_LIBRARY_SEARCH_* 标志的 LoadLibraryEx，那条路径**不接受 SetDllDirectory**
        // 设置的目录，只认 AddDllDirectory 注册的 USER_DIRS。
        // （实测：用 SetDllDirectory 时 onnxruntime.dll 本身加载成功，但创建 CUDA 会话报
        //   "OrtSessionOptionsAppendExecutionProvider_Cuda: Failed to load shared library"。）
        SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);

        var runtimeDir = ConfigStore.AiRuntimeDirectory;
        if (Directory.Exists(runtimeDir)) AddDllDirectory(runtimeDir);   // faiss 桥接层的依赖

        // onnxruntime.dll 跟随后端：CPU / CUDA / DirectML 是三份不同的文件
        var provider = ResolveProvider();
        if (provider == null)
        {
            _lastError = "没有完整可用的运算后端组件（providers\\ 下需有 cuda / directml / cpu 之一）";
            return IntPtr.Zero;                       // 退回程序自带的 CPU 版
        }

        var providerDir = ConfigStore.AiProviderDirectory(provider.Name);
        var candidate = Path.Combine(providerDir, "onnxruntime.dll");
        if (!File.Exists(candidate))
        {
            _lastError = $"后端 {provider.Display} 缺 onnxruntime.dll";
            return IntPtr.Zero;
        }

        try
        {
            AddDllDirectory(providerDir);
            _activeProvider = provider.Name;

            var handle = NativeLibrary.Load(candidate);
            _nativeLoaded = true;
            Log.Info($"[AI 变声] 已加载组件内的运行时：{candidate}（后端 {provider.Display}）");
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
