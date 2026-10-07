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
        if (!libraryName.StartsWith("mm_faiss", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        var dir = ConfigStore.AiRuntimeDirectory;
        var candidate = Path.Combine(dir, "mm_faiss.dll");
        if (!File.Exists(candidate))
        {
            Log.Warn($"[AI 变声] 未找到索引桥接层 {candidate}，索引功能不可用");
            return IntPtr.Zero;
        }

        try
        {
            AddDllDirectory(dir);          // 让 faiss.dll / libopenblas.dll / vcomp140.dll 也能被找到
            return NativeLibrary.Load(candidate);
        }
        catch (Exception ex)
        {
            Log.Error($"[AI 变声] 加载索引桥接层失败：{ex.Message}", ex);
            return IntPtr.Zero;
        }
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
        var dists = new float[(long)frames * k];
        var labels = new long[(long)frames * k];
        var vectors = new float[(long)frames * k * Dim];      // search_and_reconstruct 一次取回近邻向量

        if (mm_index_search(_handle, feats, frames, k, dists, labels, vectors) != 0)
        {
            Log.Warn("[AI 变声] 索引检索失败，本块不做索引替换");
            return;
        }

        for (var f = 0; f < frames; f++)
        {
            var baseOff = f * Dim;
            var labelOff = f * k;

            // 权重：square(1/score)，与官方 pip 版一致
            var wsum = 0.0;
            Span<double> weights = stackalloc double[k];
            for (var i = 0; i < k; i++)
            {
                if (labels[labelOff + i] < 0) { weights[i] = 0; continue; }
                var w = 1.0 / Math.Max(dists[labelOff + i], 1e-9);
                w *= w;
                weights[i] = w;
                wsum += w;
            }
            if (wsum <= 0) continue;

            Span<float> retrieved = stackalloc float[Dim];
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
        if (_handle != IntPtr.Zero)
        {
            try { mm_index_close(_handle); } catch { /* 忽略 */ }
            _handle = IntPtr.Zero;
        }
    }
}
