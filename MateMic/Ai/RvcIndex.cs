using System.Numerics;
using MateMic.Core;

namespace MateMic.Ai;

/// <summary>
/// RVC 音色索引检索（FAISS `IndexIVFFlat` 的等价实现）。
///
/// **为什么不用原始 .index 文件**：FAISS 的二进制格式没有可用的 C# 绑定
///（之前调研过：obs-rvc 已停更、VCClient 是 Python、无 C#/Unity 实时 RVC 实现）。
/// 所以约定：用 Python 侧一次性把索引转成四个裸数组文件，放在
/// <c>data\components\ai\index\&lt;名字&gt;.simple\</c>：
/// <code>
///   centroids.f32   nlist  × 768   聚类中心
///   vectors.f32     ntotal × 768   全部特征向量（**按所属簇重排**）
///   starts.i32      nlist          每个簇在 vectors 里的起始下标
///   counts.i32      nlist          每个簇的向量个数（0 表示空簇）
/// </code>
/// 检索就是标准 IVF 两级：先找最近的 nprobe 个中心，再在这些簇里找最近的 k 个向量，
/// 按 <c>square(1/距离)</c> 加权平均，最后按 indexRate 与原始特征混合。
///
/// **作用**：把内容特征在目标音色的特征库里做最近邻替换 —— 音色更"贴"目标，
/// 同时也抑制模型在无声/清音处的"自由编造"。
/// </summary>
public sealed class RvcIndex : IDisposable
{
    public const int Dim = 768;

    private readonly float[] _centroids;    // nlist × 768
    private readonly float[] _vectors;      // ntotal × 768
    private readonly int[] _starts;
    private readonly int[] _counts;
    private readonly int _nlist;

    private RvcIndex(float[] centroids, float[] vectors, int[] starts, int[] counts)
    {
        _centroids = centroids;
        _vectors = vectors;
        _starts = starts;
        _counts = counts;
        _nlist = counts.Length;
    }

    public int VectorCount => _vectors.Length / Dim;

    /// <summary>
    /// 从 <c>&lt;索引名&gt;.simple</c> 目录加载。目录不存在或文件不全返回 null（调用方忽略索引即可）。
    /// </summary>
    public static RvcIndex? Load(string simpleDir)
    {
        try
        {
            var centroids = ReadFloats(Path.Combine(simpleDir, "centroids.f32"));
            var vectors = ReadFloats(Path.Combine(simpleDir, "vectors.f32"));
            var starts = ReadInts(Path.Combine(simpleDir, "starts.i32"));
            var counts = ReadInts(Path.Combine(simpleDir, "counts.i32"));

            if (centroids.Length == 0 || vectors.Length == 0
                || centroids.Length % Dim != 0 || vectors.Length % Dim != 0
                || starts.Length != counts.Length || starts.Length == 0)
            {
                Log.Warn($"[AI 变声] 索引文件不完整或尺寸异常：{simpleDir}");
                return null;
            }

            var idx = new RvcIndex(centroids, vectors, starts, counts);
            Log.Info($"[AI 变声] 索引已加载：{idx._nlist} 个簇 / {idx.VectorCount} 个向量"
                     + $"（{(centroids.Length + vectors.Length) * 4 / 1048576} MB）");
            return idx;
        }
        catch (Exception ex)
        {
            Log.Warn($"[AI 变声] 索引加载失败，将不使用索引：" + ex.Message);
            return null;
        }
    }

    private static float[] ReadFloats(string path)
    {
        if (!File.Exists(path)) return Array.Empty<float>();
        var bytes = File.ReadAllBytes(path);
        var result = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, result, 0, result.Length * 4);
        return result;
    }

    private static int[] ReadInts(string path)
    {
        if (!File.Exists(path)) return Array.Empty<int>();
        var bytes = File.ReadAllBytes(path);
        var result = new int[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, result, 0, result.Length * 4);
        return result;
    }

    /// <summary>
    /// 就地对 <paramref name="feats"/>（行优先 [frames × 768]）做检索替换。
    /// indexRate 为 0 时直接返回。
    /// </summary>
    public void Retrieve(float[] feats, int frames, float indexRate, int k = 8, int nprobe = 8)
    {
        if (indexRate <= 0 || frames <= 0) return;
        var rate = Math.Clamp(indexRate, 0f, 1f);
        var n = Math.Min(nprobe, _nlist);

        // 复用缓冲，避免每帧分配
        Span<float> probeDist = stackalloc float[n];
        Span<int> probeIdx = stackalloc int[n];
        Span<float> candDist = stackalloc float[k];
        Span<int> candIdx = stackalloc int[k];
        Span<float> retrieved = stackalloc float[Dim];

        for (var f = 0; f < frames; f++)
        {
            var baseOff = f * Dim;

            // ---- 第一级：找最近的 nprobe 个簇 ----
            //
            // ⚠ 必须用"部分选择"而不是对整个 nlist 排序：原先每帧 Array.Sort 2606 个元素，
            // 160 帧就是 160 次全排序，实测把单块推理从 43ms 拖到 154ms
            //（超过 160ms 的块长 → worker 供不上 → 输出环常空 → 完全没有声音）。
            // 这里只维护"当前最小的 n 个"，复杂度从 O(nlist·log nlist) 降到 O(nlist·n)。
            for (var i = 0; i < n; i++) { probeDist[i] = float.MaxValue; probeIdx[i] = -1; }
            var worstProbe = float.MaxValue;
            for (var c = 0; c < _nlist; c++)
            {
                var d = DistanceSquared(feats, baseOff, _centroids, c * Dim);
                if (d >= worstProbe) continue;
                var pos = n - 1;
                while (pos > 0 && probeDist[pos - 1] > d) { probeDist[pos] = probeDist[pos - 1]; probeIdx[pos] = probeIdx[pos - 1]; pos--; }
                probeDist[pos] = d;
                probeIdx[pos] = c;
                worstProbe = probeDist[n - 1];
            }

            // ---- 第二级：在这些簇里找最近的 k 个向量 ----
            for (var i = 0; i < k; i++) { candDist[i] = float.MaxValue; candIdx[i] = -1; }
            var worst = float.MaxValue;
            for (var p = 0; p < n; p++)
            {
                var c = probeIdx[p];
                if (c < 0) continue;
                var start = _starts[c];
                var count = _counts[c];
                for (var v = 0; v < count; v++)
                {
                    var vi = start + v;
                    var d = DistanceSquared(feats, baseOff, _vectors, vi * Dim);
                    if (d >= worst) continue;
                    var pos = k - 1;
                    while (pos > 0 && candDist[pos - 1] > d) { candDist[pos] = candDist[pos - 1]; candIdx[pos] = candIdx[pos - 1]; pos--; }
                    candDist[pos] = d;
                    candIdx[pos] = vi;
                    worst = candDist[k - 1];
                }
            }

            // ---- 加权平均：weight = square(1/score)，与官方一致 ----
            double wsum = 0;
            Span<double> weights = stackalloc double[k];
            for (var i = 0; i < k; i++)
            {
                if (candIdx[i] < 0) { weights[i] = 0; continue; }
                var w = 1.0 / Math.Max(candDist[i], 1e-9);
                w *= w;
                weights[i] = w;
                wsum += w;
            }
            if (wsum <= 0) continue;

            retrieved.Clear();
            for (var i = 0; i < k; i++)
            {
                if (weights[i] <= 0) continue;
                var vecOff = candIdx[i] * Dim;
                var w = (float)(weights[i] / wsum);
                for (var d = 0; d < Dim; d++) retrieved[d] += _vectors[vecOff + d] * w;
            }
            for (var d = 0; d < Dim; d++)
                feats[baseOff + d] = retrieved[d] * rate + feats[baseOff + d] * (1f - rate);
        }
    }

    /// <summary>
    /// 平方欧氏距离（SIMD 加速）。
    /// 768 维 = 24 个 Vector&lt;float&gt;（AVX 下 8 个 float 一组），实测比朴素循环快约 5~8 倍；
    /// 这一层是索引检索的主要开销，必须用它。
    /// </summary>
    private static float DistanceSquared(float[] a, int aOff, float[] b, int bOff)
    {
        var i = 0;
        var sum = 0f;

        if (Vector.IsHardwareAccelerated)
        {
            var acc = Vector<float>.Zero;
            var w = Vector<float>.Count;
            for (; i + w <= Dim; i += w)
            {
                var d = new Vector<float>(a, aOff + i) - new Vector<float>(b, bOff + i);
                acc += d * d;
            }
            sum = Vector.Dot(acc, Vector<float>.One);
        }

        for (; i < Dim; i++)
        {
            var d = a[aOff + i] - b[bOff + i];
            sum += d * d;
        }
        return sum;
    }

    public void Dispose() { /* 纯托管数组，无需释放 */ }
}
