using System.Reflection;
using System.Runtime.InteropServices;
using MateMic.Core;

namespace MateMic.Ai;

/// <summary>
/// 通过 C++ 桥接层调用 faiss，直接读取 RVC 的 <c>.index</c> 文件。
///
/// **为什么需要这一层**：faiss 的 <c>.index</c> 是它自己的二进制格式，而 C# 没有
/// faiss 绑定。faiss.dll 又只导出 C++ mangled 符号（实测 8136 个导出里只有 10 个是
/// C 风格且全是 CRT 函数），无法用 P/Invoke 直接调。
/// 所以先用 MSVC 编一层 <c>mm_faiss.dll</c>（仅 14 KB）导出 <c>extern "C"</c> 接口，
/// 这里再 P/Invoke 它 —— 于是**用户端完全不需要 Python**，也无需任何预处理。
///
/// **检索顺序与官方一致**：在 contentvec 特征上、2 倍插值之前做检索
///（官方是 hubert → index.search → interpolate），权重用 <c>square(1/score)</c>。
///
/// 性能（160 帧 / 275 帧）：约 6.6ms / 11.2ms；此前自己手写的 IVF 实现约 39ms / 68ms，
/// 那正是"索引一拉就卡"的根源。
/// </summary>
public sealed class FaissIndex : IDisposable
{
    private const string Library = "mm_faiss";

    private IntPtr _handle;
    private bool _disposed;

    // 检索用的复用缓冲。
    //
    // ⚠ 绝不能每次 new：近邻向量是 frames × k × Dim 个 float，
    // 在"块 250ms + 上下文 2500ms"下约 275 帧 ⇒ 275×8×768×4 ≈ 6.7 MB，
    // 而每 250ms 就要检索一次 ⇒ 约 28 MB/s 的分配速率 ⇒ GC 持续触发，
    // 听感是"连续的卡顿感"且延迟升高（2026-10-08 用户实测）。
    private float[] _dists = Array.Empty<float>();
    private long[] _labels = Array.Empty<long>();
    private float[] _vectors = Array.Empty<float>();

    private void EnsureBuffers(int frames, int k)
    {
        var need = (long)frames * k;
        if (_dists.Length < need)
        {
            _dists = new float[need];
            _labels = new long[need];
        }
        var vectorNeed = need * Dim;
        if (_vectors.Length < vectorNeed) _vectors = new float[vectorNeed];
    }

    public int Dim { get; }
    public long Ntotal { get; }
    public int Nlist { get; }

    private FaissIndex(IntPtr handle, int dim, long ntotal, int nlist)
    {
        _handle = handle;
        Dim = dim;
        Ntotal = ntotal;
        Nlist = nlist;
    }

    // ---------------------------------------------------------------- 原生接口

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mm_index_open(byte[] utf8Path);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mm_index_close(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern long mm_index_ntotal(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mm_index_dim(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mm_index_nlist(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mm_index_set_nprobe(IntPtr handle, int nprobe);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mm_index_search(IntPtr handle, float[] queries, long nq, int k,
                                              float[] outDist, long[] outLabel, float[]? outVec);

    // ---------------------------------------------------------------- 加载

    private static bool _resolverInstalled;

    /// <summary>
    /// 让 <c>mm_faiss.dll</c> 与它的依赖（faiss / libopenblas / vcomp140）都从
    /// 组件目录 <c>data\components\ai\runtime\</c> 加载。
    /// 必须用 AddDllDirectory 而不是 SetDllDirectory —— 后者对带
    /// LOAD_LIBRARY_SEARCH_* 标志的加载路径无效（与 onnxruntime 那次的坑相同）。
    /// </summary>
    public static void InstallResolver()
    {
        if (_resolverInstalled) return;
        _resolverInstalled = true;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(FaissIndex).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // 每个程序集只能设一次；自检与正常启动都会走到这里，忽略重复
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        var dir = ConfigStore.AiRuntimeDirectory;

        if (libraryName.StartsWith("libopenblas", StringComparison.OrdinalIgnoreCase))
        {
            var blas = Path.Combine(dir, "libopenblas.dll");
            if (!File.Exists(blas)) return IntPtr.Zero;
            PinBlasThreads();
            AddDllDirectory(dir);
            return NativeLibrary.Load(blas);
        }

        if (!libraryName.StartsWith("mm_faiss", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        var candidate = Path.Combine(dir, "mm_faiss.dll");
        if (!File.Exists(candidate))
        {
            Log.Warn($"[AI 变声] 未找到索引桥接层 {candidate}，索引功能不可用");
            return IntPtr.Zero;
        }

        try
        {
            PinBlasThreads();              // ⚠ 必须在 libopenblas 被加载**之前**
            AddDllDirectory(dir);          // 让 faiss.dll / libopenblas.dll / vcomp140.dll 也能被找到
            return NativeLibrary.Load(candidate);
        }
        catch (Exception ex)
        {
            Log.Error($"[AI 变声] 加载索引桥接层失败：{ex.Message}", ex);
            return IntPtr.Zero;
        }
    }

    private static bool _blasThreadsPinned;

    /// <summary>
    /// 把 BLAS / OpenMP 的线程数钉成 1。
    ///
    /// **必须在 libopenblas 被加载之前执行** —— 它只在初始化时读这两个环境变量，
    /// 之后再用环境变量改是无效的（所以 Open() 里还会用 API 兜底一次）。
    ///
    /// 官方实时实现同样这么做：`realtime_gui.py` 与 `rvc_worker.py` 里都是
    /// `OPENBLAS_NUM_THREADS=1` + `OMP_NUM_THREADS=4`，而我们此前**两项都没设**。
    ///
    /// 实测（`--indexcheck`，2180 簇 / 85021 向量 / 768 维 / 275 帧 / 30 次调用）：
    /// <code>
    ///   默认（两项都放开）  ：P50 14.1ms，平均占用 11.0 个核心
    ///   只限 OPENBLAS=1     ：P50 19.5ms，平均占用 10.6 个核心   ← 没用，反而更慢
    ///   OPENBLAS=1 + OMP=1  ：P50 16.8ms，平均占用  0.9 个核心   ← 采用
    /// </code>
    /// 多花 2.7ms 换回 10 个核心是必须的：音频回调是硬实时，被抢走就表现为
    /// **"一开索引就一卡一卡的、像音频块接不上"**（2026-10-08 用户报的现象）。
    ///
    /// ⚠ 只设 `OPENBLAS_NUM_THREADS` 没有用（实测占用仍 10.6 核、且更慢）——
    /// 这个规模的并行来自 **faiss 自己的 OpenMP**（对多查询并行），不是 BLAS。
    /// </summary>
    private static void PinBlasThreads()
    {
        if (_blasThreadsPinned) return;
        _blasThreadsPinned = true;
        try
        {
            Environment.SetEnvironmentVariable("OPENBLAS_NUM_THREADS", "1");
            Environment.SetEnvironmentVariable("OMP_NUM_THREADS", "1");
        }
        catch (Exception ex)
        {
            Log.Warn($"[AI 变声] 设置 BLAS/OpenMP 线程数失败：{ex.Message}");
        }
    }

    [DllImport("libopenblas", CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "openblas_set_num_threads")]
    private static extern void OpenBlasSetNumThreads(int threads);

    private static bool _blasApiPinned;

    /// <summary>
    /// 用 API 把 OpenBLAS 线程数再钉一次（环境变量那条路的兜底）：
    /// 万一 libopenblas 已经被别的路径加载过，环境变量就读不到了。
    /// </summary>
    private static void PinBlasThreadsViaApi()
    {
        if (_blasApiPinned) return;
        _blasApiPinned = true;
        try { OpenBlasSetNumThreads(1); }
        catch (Exception ex) { Log.Warn($"[AI 变声] 调用 openblas_set_num_threads 失败：{ex.Message}"); }
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr AddDllDirectory(string newDirectory);

    /// <summary>打开索引文件；失败返回 null（调用方照常工作，只是不用索引）。</summary>
    public static FaissIndex? Open(string path)
    {
        InstallResolver();
        try
        {
            if (!File.Exists(path))
            {
                Log.Warn($"[AI 变声] 索引文件不存在：{path}");
                return null;
            }

            var handle = mm_index_open(Utf8Z(path));
            if (handle == IntPtr.Zero)
            {
                Log.Warn($"[AI 变声] 索引打开失败（格式不受支持或依赖缺失）：{path}");
                return null;
            }

            var index = new FaissIndex(handle, mm_index_dim(handle), mm_index_ntotal(handle), mm_index_nlist(handle));

            // 此时 libopenblas 已随依赖链加载进进程，用 API 再钉一次线程数（环境变量的兜底）
            PinBlasThreadsViaApi();

            Log.Info($"[AI 变声] 索引已加载（faiss）：{Path.GetFileName(path)}"
                     + $" —— {index.Nlist} 个簇 / {index.Ntotal} 个向量 / {index.Dim} 维");
            return index;
        }
        catch (Exception ex)
        {
            Log.Warn($"[AI 变声] 索引加载异常，将不使用索引：{ex.Message}");
            return null;
        }
    }

    private static byte[] Utf8Z(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var result = new byte[bytes.Length + 1];       // 结尾补一个 0
        Array.Copy(bytes, result, bytes.Length);
        return result;
    }

    // ---------------------------------------------------------------- 检索

    /// <summary>一级检索探测的簇数。官方未改动，faiss 默认 1。</summary>
    public void SetNprobe(int nprobe)
    {
        if (_disposed || _handle == IntPtr.Zero) return;
        mm_index_set_nprobe(_handle, nprobe);
    }

    /// <summary>
    /// 就地对 <paramref name="feats"/>（行优先 [frames × 768]）做检索替换。
    /// indexRate 为 0 时直接返回。权重用官方的 <c>square(1/score)</c>。
    /// </summary>
    public void Retrieve(float[] feats, int frames, float indexRate, int k = 8)
    {
        if (_disposed || _handle == IntPtr.Zero) return;
        if (indexRate <= 0 || frames <= 0) return;

        var rate = Math.Clamp(indexRate, 0f, 1f);
        EnsureBuffers(frames, k);
        var dists = _dists;
        var labels = _labels;
        var vectors = _vectors;

        if (mm_index_search(_handle, feats, frames, k, dists, labels, vectors) != 0)
        {
            Log.Warn("[AI 变声] 索引检索失败，本块不做索引替换");
            return;
        }

        // ⚠ 这两个栈缓冲必须在循环**外**分配。
        // 原实现写在循环体内，编译器一直报 CA2014（"潜在的堆栈溢出，将 stackalloc 移出循环"）：
        //   · `stackalloc` 在循环里**不随迭代回收**，275 帧 × (768×4B + 8×8B) ≈ 860KB
        //     全部堆在同一个栈帧上，逼近线程默认 1MB 栈上限；
        //   · 每次迭代还要重新清零这几 KB，纯属白干。
        Span<double> weights = stackalloc double[k];
        Span<float> retrieved = stackalloc float[Dim];

        for (var f = 0; f < frames; f++)
        {
            var baseOff = f * Dim;
            var labelOff = f * k;

            retrieved.Clear();                 // 复用：每帧从零开始累加

            // 权重：square(1/score)，与官方 pip 版一致
            var wsum = 0.0;
            for (var i = 0; i < k; i++)
            {
                if (labels[labelOff + i] < 0) { weights[i] = 0; continue; }
                var w = 1.0 / Math.Max(dists[labelOff + i], 1e-9);
                w *= w;
                weights[i] = w;
                wsum += w;
            }
            if (wsum <= 0) continue;

            for (var i = 0; i < k; i++)
            {
                if (weights[i] <= 0) continue;
                var vectorOff = ((long)f * k + i) * Dim;
                var w = (float)(weights[i] / wsum);
                for (var d = 0; d < Dim; d++) retrieved[d] += vectors[vectorOff + d] * w;
            }
            for (var d = 0; d < Dim; d++)
                feats[baseOff + d] = retrieved[d] * rate + feats[baseOff + d] * (1f - rate);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dists = Array.Empty<float>();
        _labels = Array.Empty<long>();
        _vectors = Array.Empty<float>();
        if (_handle != IntPtr.Zero)
        {
            try { mm_index_close(_handle); } catch { /* 忽略 */ }
            _handle = IntPtr.Zero;
        }
    }
}
