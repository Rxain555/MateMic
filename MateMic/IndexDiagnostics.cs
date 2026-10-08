using System.Diagnostics;
using System.Runtime.InteropServices;
using MateMic.Ai;
using MateMic.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MateMic;

/// <summary>
/// 索引（faiss）检索耗时自检：
/// <code>MateMic.exe --indexcheck [--index 名称] [--frames N] [--iters M] [--nprobe N] [--rate R]</code>
///
/// **只跑 CPU 侧的 faiss 检索**：不建 ONNX 会话、不碰 GPU、不开任何音频设备，
/// 因此可以在用户正常使用机器时安全地单独测量"索引到底花了多少时间"。
///
/// 为什么要单独测它：用户反馈"一开索引就一卡一卡的、像音频块接不上"（2026-10-08）。
/// 检索耗时虽然已计入 `StreamingRvc.LastInferMs`，但那个数字混了
/// contentvec / rmvpe / synth / 检索四段，无法判断检索本身占多少、有没有长尾。
///
/// ⚠ **中位数好看不代表没问题**：块式实时处理怕的是偶发长尾 ——
/// 一次 150ms 就足够让输出环被抽干（听感就是"块接不上"）。
/// 所以本自检同时打印 P50 / P90 / P95 / 最大值，并逐次列出。
/// </summary>
internal static class IndexDiagnostics
{
    public static int Run(string[] args)
    {
        var frames = Math.Max(1, IntOf(args, "--frames", 275));
        var iters = Math.Max(1, IntOf(args, "--iters", 50));
        var nprobe = Math.Max(1, IntOf(args, "--nprobe", 1));
        var rate = 1.0f;
        if (float.TryParse(ValueOf(args, "--rate"), out var parsed)) rate = Math.Clamp(parsed, 0f, 1f);

        // BLAS / OpenMP 线程数：-1 表示"不动，用库的默认值"。
        // **必须在加载 libopenblas 之前设环境变量**（它在初始化时读，之后再设无效），
        // 打开索引后再用 API 兜底设一次。官方实时实现固定 OPENBLAS_NUM_THREADS=1 / OMP_NUM_THREADS=4，
        // 我们此前两项都没设 —— 于是检索会把全部 16 个核心拉起来跑。
        var blas = IntOf(args, "--blas", -1);
        var omp = IntOf(args, "--omp", -1);
        if (blas > 0) Environment.SetEnvironmentVariable("OPENBLAS_NUM_THREADS", blas.ToString());
        if (omp > 0) Environment.SetEnvironmentVariable("OMP_NUM_THREADS", omp.ToString());

        var path = ResolveIndexPath(ValueOf(args, "--index"));
        if (path == null)
        {
            Log.Error("[索引自检] 没找到可用的 .index 文件（看 data\\components\\ai\\index\\）");
            return 1;
        }

        Log.Info($"[索引自检] 开始：{Path.GetFileName(path)}｜frames={frames}｜nprobe={nprobe}"
                 + $"｜iters={iters}｜indexRate={rate:0.##}｜blas={blas}｜omp={omp}");

        using var index = FaissIndex.Open(path);
        if (index == null)
        {
            Log.Error("[索引自检] 索引打开失败");
            return 1;
        }

        index.SetNprobe(nprobe);
        if (blas > 0) SafeSetOpenBlasThreads(blas);

        Log.Info($"[索引自检] 规模：{index.Nlist} 簇 / {index.Ntotal} 向量 / {index.Dim} 维"
                 + $"｜OpenBLAS 线程数 = {SafeOpenBlasThreads()}｜CPU 逻辑核心 {Environment.ProcessorCount}");

        var dim = index.Dim;

        // 查询特征：默认随机（只测耗时），给 --wav 就用**真实语音**（这才是"索引有没有用"的依据）。
        float[] feats;
        var frameCount = frames;
        var quiet = true;
        if (dim != ContentEncoder.FeatureDim)
        {
            Log.Error($"[索引自检] 这个索引是 {dim} 维，而 contentvec 特征是 {ContentEncoder.FeatureDim} 维，无法做真实语音检索");
            return 1;
        }

        var wav = ValueOf(args, "--wav");
        if (!string.IsNullOrWhiteSpace(wav) && File.Exists(wav))
        {
            var audio = LoadMono16k(wav);
            Log.Info($"[索引自检] 语音素材：{Path.GetFileName(wav)} → 16 kHz 单声道"
                     + $" {audio.Length} 样本（{audio.Length / 16000.0:0.00} 秒）");
            using var encoder = new ContentEncoder(
                Path.Combine(ConfigStore.AiEngineDirectory, "contentvec.onnx"), useGpu: false);
            (feats, frameCount) = encoder.Encode(audio);
            quiet = false;      // 真实特征：这条诊断就是本次自检的重点
            Log.Info($"[索引自检] contentvec 特征 {frameCount} 帧 × {ContentEncoder.FeatureDim} 维");
        }
        else
        {
            feats = new float[(long)frames * dim];
            var rng = new Random(12345);
            for (var i = 0; i < feats.Length; i++) feats[i] = (float)(rng.NextDouble() * 2 - 1);
            Log.Info("[索引自检] 未给 --wav：用随机特征，**只能看耗时/CPU 占用**"
                     + "（随机特征与训练集无关，检索出来的东西说明不了音质问题）");
        }

        // 预热：第一次调用要建 OpenBLAS/OpenMP 线程池、把倒排表页读进来，
        // 不计入统计（否则量到的是"首次开销"而不是稳态耗时）。
        for (var i = 0; i < 3; i++) index.Retrieve(feats, frameCount, rate, quiet: quiet);

        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var wallBefore = Stopwatch.GetTimestamp();

        var times = new double[iters];
        var sw = new Stopwatch();
        for (var i = 0; i < iters; i++)
        {
            sw.Restart();
            index.Retrieve(feats, frameCount, rate, quiet: quiet);
            sw.Stop();
            times[i] = sw.Elapsed.TotalMilliseconds;
        }

        var wallMs = (Stopwatch.GetTimestamp() - wallBefore) * 1000.0 / Stopwatch.Frequency;
        var cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;

        var sorted = (double[])times.Clone();
        Array.Sort(sorted);
        double Pct(double p) => sorted[Math.Min(sorted.Length - 1, (int)Math.Round(p * (sorted.Length - 1)))];

        Log.Info($"[索引自检] 结果：P50 {Pct(0.50):F1}ms｜P90 {Pct(0.90):F1}ms｜P95 {Pct(0.95):F1}ms"
                 + $"｜最大 {sorted[^1]:F1}ms｜最小 {sorted[0]:F1}ms｜平均 {times.Average():F1}ms");

        // CPU 占用是最关键的一列：每次检索**同时占用了几个核心**。
        // 音频回调是硬实时（错过截止就是爆音），检索如果动辄拉满 16 核，
        // 即使自身只要 13ms，也会把音频线程与 GPU 提交挤出去。
        Log.Info($"[索引自检] CPU 占用：CPU 时间 {cpuMs:F0}ms / 墙钟 {wallMs:F0}ms"
                 + $" = 平均 {cpuMs / Math.Max(wallMs, 1):F1} 个核心（{iters} 次调用）");

        // 逐次列出：长尾有没有周期性、是不是每隔几次就冒一次，看一串数字最直观。
        Log.Info("[索引自检] 逐次耗时(ms)：" + string.Join(", ", times.Select(t => t.ToString("F1"))));
        return 0;
    }

    // ---------------------------------------------------------------- 辅助

    private static string? ValueOf(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }
        return null;
    }

    /// <summary>
    /// 把任意格式的音频解成 16 kHz 单声道 float（ContentVec 要的就是这个）。
    /// 读法与 `tools\dev\VcFileProbe` 一致：Media Foundation 解码（wav/mp3/m4a 都能吃）
    /// → 需要就下混单声道 → 需要就重采样到 16 kHz。
    /// </summary>
    private static float[] LoadMono16k(string path)
    {
        using var reader = new MediaFoundationReader(path);
        ISampleProvider provider = reader.ToSampleProvider();
        if (provider.WaveFormat.Channels > 1)
            provider = new StereoToMonoSampleProvider(provider);
        if (provider.WaveFormat.SampleRate != 16000)
            provider = new WdlResamplingSampleProvider(provider, 16000);

        var buffer = new float[8192];
        var all = new List<float>();
        int read;
        while ((read = provider.Read(buffer.AsSpan())) > 0)
            all.AddRange(buffer.AsSpan(0, read).ToArray());
        return all.ToArray();
    }

    private static int IntOf(string[] args, string name, int fallback)
        => int.TryParse(ValueOf(args, name), out var value) ? value : fallback;

    /// <summary>按"绝对路径 → 名称片段 → 目录里第一个"的顺序定位一个索引文件。</summary>
    private static string? ResolveIndexPath(string? wanted)
    {
        if (!string.IsNullOrWhiteSpace(wanted) && File.Exists(wanted)) return wanted;

        var dir = ConfigStore.AiIndexDirectory;
        if (!Directory.Exists(dir)) return null;

        var files = Directory.GetFiles(dir, "*.index");
        if (files.Length == 0) return null;

        if (!string.IsNullOrWhiteSpace(wanted))
        {
            var hit = files.FirstOrDefault(f => Path.GetFileName(f)
                .Contains(wanted, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
            Log.Warn($"[索引自检] 没找到名称含「{wanted}」的索引，改用 {Path.GetFileName(files[0])}");
        }
        return files[0];
    }

    [DllImport("libopenblas", CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "openblas_get_num_threads")]
    private static extern int OpenBlasGetNumThreads();

    [DllImport("libopenblas", CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "openblas_set_num_threads")]
    private static extern void OpenBlasSetNumThreads(int threads);

    private static void SafeSetOpenBlasThreads(int threads)
    {
        try { OpenBlasSetNumThreads(threads); }
        catch (Exception ex) { Log.Warn($"[索引自检] 设置 OpenBLAS 线程数失败：{ex.GetType().Name}"); }
    }

    /// <summary>
    /// 读 OpenBLAS 当前的线程数。官方实时实现显式设了 <c>OPENBLAS_NUM_THREADS=1</c>，
    /// 而我们没有设 —— 若这里打印出来是 16/32 之类，就是"开索引就抖"的头号嫌疑
    ///（BLAS 在音频 worker 线程里拉起一堆线程，与 GPU 提交、音频回调抢 CPU）。
    /// </summary>
    private static string SafeOpenBlasThreads()
    {
        try { return OpenBlasGetNumThreads().ToString(); }
        catch (Exception ex) { return $"读取失败（{ex.GetType().Name}）"; }
    }
}
