using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MicMate.Core;
using MicMate.Dsp;

namespace MicMate.Denoise;

public sealed record DenoiseModelInfo(string Name, string Path, bool IsDefault, string? Error)
{
    public bool IsValid => Error == null;

    public override string ToString() => Name;
}

/// <summary>
/// 模型形态。载入时按张量形状自动识别。
/// </summary>
public enum DenoiseModelKind
{
    /// <summary>输入 [*,481] 对数幅度谱 → 输出 [*,481] 逐频点增益（内置训练器产出）。</summary>
    SpectralGain,

    /// <summary>输入 [1,480] 波形帧 → 输出 [1,480] 降噪波形帧（少数波形域导出）。</summary>
    Waveform,

    /// <summary>
    /// 频谱域流式模型：输入 [1,1,bins,2] 复频谱 + 状态，输出同形状（DPDFNet 等）。
    /// 参数由模型 ONNX 元数据自描述，实现见 <see cref="DpdfNetDenoiseModel"/>。
    /// </summary>
    SpectralStreaming,
}

/// <summary>
/// 降噪模型管理（项目书 3.6）：
/// · 模型文件夹 %LocalAppData%\MicMate/models/
/// · 内置默认模型 + 扫描文件夹中所有 .onnx
/// · 用 OnnxRuntime.InferenceSession 验证张量形状，并自动识别模型形态
/// · 当前模型被删除时自动回退到默认模型并提示
/// </summary>
public static class ModelCatalog
{
    /// <summary>形态 A 的频点数：481 个频点。</summary>
    public static int Bins => SpectralDenoiseModel.FftSize / 2 + 1;

    /// <summary>形态 B 的帧长：480 samples（10 ms @ 48 kHz）。</summary>
    public static int FrameSize => SpectralDenoiseModel.FrameSize;

    public const string DefaultModelName = "默认模型（内置谱减降噪）";

    /// <summary>
    /// 扫描结果缓存。
    /// 每次 Scan 都要为**每个**模型创建 InferenceSession（dpdfnet8 达 15 MB），
    /// 在 UI 线程上同步做这件事会让界面卡死——尤其是点开模型下拉框时。
    /// 因此结果缓存起来，只在首次或显式要求刷新时真正扫描。
    /// </summary>
    private static IReadOnlyList<DenoiseModelInfo>? _cache;

    /// <summary>取得模型列表。默认使用缓存；需要重新扫描时传 <paramref name="forceRefresh"/>。</summary>
    public static IReadOnlyList<DenoiseModelInfo> Scan(bool forceRefresh = false)
    {
        if (!forceRefresh && _cache != null) return _cache;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var list = new List<DenoiseModelInfo>
        {
            new(DefaultModelName, string.Empty, true, null),
        };

        try
        {
            Directory.CreateDirectory(ConfigStore.ModelsDirectory);
            var files = Directory.EnumerateFiles(ConfigStore.ModelsDirectory, "*.onnx")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Log.Info($"扫描模型目录 {ConfigStore.ModelsDirectory}：找到 {files.Count} 个 .onnx 文件");

            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var error = Validate(file, out var info);
                list.Add(new DenoiseModelInfo(error == null ? name : $"{name}（不可用）", file, false, error));

                if (error == null) Log.Info($"  ✓ {name}：{info}");
                else Log.Warn($"  ✗ {name}：{error}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("扫描模型文件夹失败", ex);
        }

        stopwatch.Stop();
        // 耗时较长时明确记录，便于确认"卡顿"是否来自模型校验
        Log.Info($"模型扫描完成：{list.Count - 1} 个外部模型，耗时 {stopwatch.ElapsedMilliseconds} ms");

        _cache = list;
        return list;
    }

    /// <summary>验证模型：创建 InferenceSession，识别形态并检查张量形状。</summary>
    public static string? Validate(string path, out string tensorInfo)
        => Validate(path, out tensorInfo, out _);

    /// <summary>
    /// 建立单线程推理会话。
    /// 官方参考实现就是单线程；不限制的话 ONNX Runtime 默认会把所有核心拉满
    /// （实测等效单核占用 949%，全机 CPU 59%——远超同一功能的 PureVox 的约 2%）。
    /// 所有建会话的地方（校验、频谱域后端、波形域后端）都必须走这里。
    /// </summary>
    internal static InferenceSession CreateSession(string path)
        => new(path, new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        });

    /// <summary>验证模型并返回识别出的形态。</summary>
    public static string? Validate(string path, out string tensorInfo, out DenoiseModelKind kind)
    {
        tensorInfo = string.Empty;
        kind = DenoiseModelKind.SpectralGain;
        try
        {
            using var session = CreateSession(path);

            // 形态 C：频谱域流式模型（DPDFNet / PureVox / GTCRN 这一族）。
            // 特征：≥2 输入 ≥2 输出，主输入是 4 维且含长度为 2 的维（实/虚）。
            var metadata = session.ModelMetadata?.CustomMetadataMap;

            // 元数据键名大小写不统一（DPDFNet 用 n_fft/window_type，PureVox 用大写），统一小写查
            var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (metadata != null)
                foreach (var kv in metadata) meta[kv.Key] = kv.Value;

            var inputs = session.InputMetadata.ToArray();
            var outputs = session.OutputMetadata.ToArray();

            // 复数维不一定在最后：[1,481,1,2]（DPDFNet）与 [1,1025,1,2]（PureVox）都是这种
            static bool LooksLikeSpectrum(IReadOnlyList<int> dims)
                => dims.Count == 4 && dims.Contains(2);

            var specInput = inputs.FirstOrDefault(kv => LooksLikeSpectrum(kv.Value.Dimensions));
            var specOutput = outputs.FirstOrDefault(kv => LooksLikeSpectrum(kv.Value.Dimensions));
            var stateInput = inputs.FirstOrDefault(kv => kv.Key != specInput.Key);

            if (specInput.Key != null && specOutput.Key != null && stateInput.Key != null && outputs.Length >= 2)
            {
                var dimensions = specInput.Value.Dimensions;
                var bins = dimensions.FirstOrDefault(d => d > 2);

                var fft = meta.TryGetValue("n_fft", out var nfft) ? nfft : ((bins - 1) * 2).ToString();
                var hop = meta.TryGetValue("hop_length", out var h) ? h : (bins - 1).ToString();
                var window = meta.TryGetValue("window_type", out var w) ? w : "vorbis（推断）";
                var rate = meta.TryGetValue("sample_rate", out var sr)
                    ? sr
                    : (bins <= 257 ? "16000（推断）" : "48000（推断）");

                tensorInfo = $"频谱域流式 输入 [{string.Join(",", dimensions)}]" +
                             $" / 状态 [{string.Join(",", stateInput.Value.Dimensions)}]" +
                             $" · FFT {fft} / hop {hop} / {window} 窗 / {bins} 频点 / {rate} Hz";
                kind = DenoiseModelKind.SpectralStreaming;
                return null;
            }

            // 形态 B：波形域（输入/输出都有 [*,480]）
            var waveIn = session.InputMetadata.FirstOrDefault(kv => kv.Value.Dimensions.Length == 2
                                                                    && kv.Value.Dimensions[^1] == FrameSize);
            var waveOut = session.OutputMetadata.FirstOrDefault(kv => kv.Value.Dimensions.Length == 2
                                                                     && kv.Value.Dimensions[^1] == FrameSize);
            if (waveIn.Key != null && waveOut.Key != null)
            {
                var stateCount = session.InputMetadata.Count - 1;
                tensorInfo = $"波形域 [1,{FrameSize}] → [1,{FrameSize}]，循环状态 {stateCount} 组";
                kind = DenoiseModelKind.Waveform;
                return null;
            }

            // 形态 A：增益曲线域
            var input = session.InputMetadata.First();
            var output = session.OutputMetadata.First();

            var inputShape = string.Join(",", input.Value.Dimensions);
            var outputShape = string.Join(",", output.Value.Dimensions);
            tensorInfo = $"增益曲线域 输入 [{inputShape}] {input.Value.ElementType} → 输出 [{outputShape}]";

            var inputDims = input.Value.Dimensions;
            if (inputDims.Length != 2 || inputDims[^1] != Bins)
                return $"模型形状不受支持：既不是波形域 [1,{FrameSize}]，也不是增益曲线域 [*,{Bins}]（实际 [{inputShape}]）";

            var outputDims = output.Value.Dimensions;
            if (outputDims.Length != 2 || outputDims[^1] != Bins)
                return $"模型输出形状不匹配：期望 [*,{Bins}]，实际 [{outputShape}]";

            kind = DenoiseModelKind.SpectralGain;
            return null;
        }
        catch (OnnxRuntimeException ex)
        {
            kind = DenoiseModelKind.SpectralGain;
            return "模型加载失败：" + ex.Message;
        }
        catch (Exception ex)
        {
            kind = DenoiseModelKind.SpectralGain;
            return "模型加载失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 用一组校准频谱跑一次推理，取出每个频点的增益曲线，供内置谱减后端使用。
    /// 这样训练出的 ONNX 模型即可直接驱动实时降噪。
    /// </summary>
    public static bool TryExtractProfile(string path, out float[] gains, out string message)
    {
        gains = Array.Empty<float>();
        message = string.Empty;

        try
        {
            using var session = CreateSession(path);
            var inputName = session.InputMetadata.Keys.First();

            var data = new float[Bins];
            for (var i = 0; i < Bins; i++)
            {
                // 典型底噪水平：−60 dBFS，按频点轻微倾斜以覆盖更多工作点
                data[i] = -60f + i / (float)Bins * 10f;
            }

            var tensor = new DenseTensor<float>(data, new[] { 1, Bins });
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputName, tensor),
            };

            using var results = session.Run(inputs);
            var output = results.First().AsEnumerable<float>().ToArray();
            if (output.Length < Bins)
            {
                message = $"模型输出长度不足：期望 {Bins}，实际 {output.Length}";
                return false;
            }

            gains = new float[Bins];
            Array.Copy(output, gains, Bins);
            for (var i = 0; i < Bins; i++)
                gains[i] = Math.Clamp(gains[i], 0.01f, 1f);

            message = "已从模型提取降噪曲线";
            return true;
        }
        catch (Exception ex)
        {
            message = "模型推理失败：" + ex.Message;
            return false;
        }
    }
}
