using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MateMic.Core;
using MateMic.Dsp;

namespace MateMic.Denoise;

/// <summary>
/// DPDFNet 系列流式降噪（可在 48 kHz 处理链中使用任意原生采样率的模型）。
///
/// 实现严格照抄官方 Python 参考实现（dpdfnet 包 stream.py / audio.py / onnx_backend.py）：
///   · 参数全部从模型 ONNX 元数据读取（window_type / n_fft / hop_length / sample_rate /
///     state_size / *_init / freq_bins），不写死、不猜
///   · **因果 STFT**（center=0），**Vorbis 窗**，50% 重叠下满足 COLA
///   · 输入张量 [1,1,bins,2]（实/虚），状态张量 [1,state]
///   · 状态初值取自元数据：erb_norm_init 填前 erb_norm_state_size 个，spec_norm_init 紧随其后
///   · 每帧 irfft 后加窗、重叠相加，提交前 hop 个样本
///
/// **采样率适配**：若模型原生采样率不是 48 kHz（例如 GTCRN 与 DPDFNet 的 16 kHz 版本），
/// 会先把输入降到模型采样率、推理后再升回 48 kHz，模型侧按自己的 hop 分帧。
/// </summary>
public sealed class DpdfNetDenoiseModel : IDenoiseModel
{
    private readonly InferenceSession _session;
    private readonly string _specInput;
    private readonly string _specOutput;

    /// <summary>频谱张量的实际形状（各模型不同：DPDFNet [1,1,481,2]、GTCRN [1,257,1,2]）。</summary>
    private readonly int[] _specShape;

    private readonly int _fftSize;
    private readonly int _hopSize;
    private readonly int _bins;
    private readonly float[] _window;

    private readonly float[] _inputBuffer;
    private readonly float[] _outputBuffer;
    private readonly float[] _frameReal;
    private readonly float[] _frameImaginary;

    /// <summary>预算三角函数表：[bins × fftSize]（正向用 −2πkn/N，逆向用其相反数）。</summary>
    private readonly float[] _cosTable;
    private readonly float[] _sinTable;

    /// <summary>
    /// 循环状态张量：输入名 / 输出名 / 形状 / 初值 / 当前值。
    /// DPDFNet 1 组、GTCRN 3 组、PureVox 4 组。
    /// </summary>
    private readonly List<StateTensor> _states = new();
    private DenseTensor<float> _specTensor;

    /// <summary>模型原生采样率（来自元数据，缺省按 48000）。</summary>
    private readonly int _modelSampleRate;

    /// <summary>48 kHz ↔ 模型采样率的转换（模型本身即 48 kHz 时自动旁通）。</summary>
    private readonly RateConverter _downConverter;
    private readonly RateConverter _upConverter;
    private readonly float[] _rateStage;

    private int _inputCount;
    private int _outputCount;
    private int _outputPosition;

    /// <summary>本模块对外固定使用 48 kHz（处理链采样率）。</summary>
    public const int PipelineSampleRate = 48000;

    public DpdfNetDenoiseModel(string path, string displayName)
    {
        // 关键：单线程推理（见 ModelCatalog.CreateSession 的说明：
        // 不限线程时 ONNX Runtime 会把所有核心拉满，实测等效单核占用 949%）。
        _session = ModelCatalog.CreateSession(path);

        var rawMetadata = _session.ModelMetadata?.CustomMetadataMap ?? new Dictionary<string, string>();
        // 元数据键名大小写不统一（PureVox 用大写），统一成小写查
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in rawMetadata) metadata[kv.Key] = kv.Value;
        _fftSize = ReadInt(metadata, "n_fft", 960);
        _hopSize = ReadInt(metadata, "hop_length", _fftSize / 2);
        _bins = ReadInt(metadata, "freq_bins", _fftSize / 2 + 1);

        // 采样率：优先读元数据；DPDFNet 系会声明，GTCRN 这类没有声明的按 fft 长度推断
        _modelSampleRate = ReadInt(metadata, "sample_rate", 0);
        if (_modelSampleRate <= 0)
        {
            // 没有声明时按频谱点数推断：257 点 → 16 kHz（512 FFT），481 → 48 kHz（960 FFT）
            _modelSampleRate = _bins <= 257 ? 16000 : 48000;
            Log.Warn($"{displayName} 未声明采样率，按频点数推断为 {_modelSampleRate} Hz（{_bins} 频点）。");
        }

        _downConverter = new RateConverter(PipelineSampleRate, _modelSampleRate, 8192);
        _upConverter = new RateConverter(_modelSampleRate, PipelineSampleRate, 8192);
        _rateStage = new float[8192];

        if (metadata.TryGetValue("center", out var center) && center.Trim() == "0")
            Log.Warn($"{displayName} 的元数据声明 center=0（离线路径），本实现按流式因果处理。");

        var windowType = metadata.TryGetValue("window_type", out var wt) ? wt.Trim().ToLowerInvariant() : "vorbis";
        _window = BuildAnalysisWindow(_fftSize, windowType);

        // 主频谱输入/输出：4 维且含长度为 2 的维（复数维不一定在最后）
        var inputMetadata = _session.InputMetadata.ToArray();
        var outputMetadata = _session.OutputMetadata.ToArray();

        static bool IsSpectrum(IReadOnlyList<int> dims) => dims.Count == 4 && dims.Contains(2);

        var specIn = inputMetadata.FirstOrDefault(kv => IsSpectrum(kv.Value.Dimensions));
        var specOut = outputMetadata.FirstOrDefault(kv => IsSpectrum(kv.Value.Dimensions));
        if (specIn.Key == null || specOut.Key == null)
            throw new InvalidOperationException("模型中找不到复数频谱张量（4 维且含长度为 2 的维）。");

        _specInput = specIn.Key;
        _specOutput = specOut.Key;
        _specShape = specIn.Value.Dimensions.Select(d => Math.Max(1, d)).ToArray();

        // 其余输入按"输入 → 同名 _out 输出"配对为循环状态（DPDFNet: state_in/state_out；
        // GTCRN: conv_cache/conv_cache_out …；PureVox: enc_c/enc_c_out …）
        foreach (var input in inputMetadata.Where(kv => kv.Key != _specInput))
        {
            var outputName = FindStateOutput(outputMetadata.Select(kv => kv.Key).ToArray(), input.Key);
            if (outputName == null)
            {
                Log.Warn($"状态张量 {input.Key} 找不到对应输出，已忽略。");
                continue;
            }

            var shape = input.Value.Dimensions.Select(d => Math.Max(1, d)).ToArray();
            var size = shape.Aggregate(1, (a, b) => a * b);
            var initial = BuildInitialState(metadata, input.Key, size);

            _states.Add(new StateTensor
            {
                Input = input.Key,
                Output = outputName,
                Shape = shape,
                Initial = initial,
                Current = (float[])initial.Clone(),
            });
        }

        if (_states.Count == 0) Log.Warn($"{displayName} 没有任何循环状态张量，按无状态模型处理。");

        // 预分配所有张量：模型每帧都要喂状态，若每帧新建 DenseTensor，
        // 100 帧/秒下会造成持续的 GC 压力（GTCRN 每帧 1 个频谱 + 3 个状态张量）。
        _specTensor = new DenseTensor<float>(_specShape);
        foreach (var state in _states)
            state.Tensor = new DenseTensor<float>(state.Current, state.Shape);

        _inputBuffer = new float[_fftSize * 2];
        _outputBuffer = new float[_fftSize * 2];
        _frameReal = new float[_fftSize];
        _frameImaginary = new float[_fftSize];

        _cosTable = new float[_bins * _fftSize];
        _sinTable = new float[_cosTable.Length];
        for (var k = 0; k < _bins; k++)
        {
            var offset = k * _fftSize;
            for (var n = 0; n < _fftSize; n++)
            {
                var phase = 2.0 * Math.PI * k * n / _fftSize;
                _cosTable[offset + n] = (float)Math.Cos(phase);
                _sinTable[offset + n] = (float)Math.Sin(phase);
            }
        }

        Name = displayName;
        TensorInfo = $"{_modelSampleRate} Hz / {_fftSize} 点 FFT / hop {_hopSize} / {windowType} 窗 / {_bins} 频点" +
                     $" / 状态 {_states.Count} 组" +
                     (_downConverter.IsBypassed ? string.Empty : $"，48000↔{_modelSampleRate} Hz 重采样");
        Log.Info($"已载入频谱域模型 {displayName}：{TensorInfo}");
    }

    /// <summary>一组循环状态：名称、形状、初值与可复用的张量。</summary>
    private sealed class StateTensor
    {
        public required string Input { get; init; }
        public required string Output { get; init; }
        public required int[] Shape { get; init; }
        public required float[] Initial { get; init; }
        public required float[] Current { get; init; }
        public DenseTensor<float>? Tensor { get; set; }
    }

    public string Name { get; }

    public string TensorInfo { get; }

    /// <summary>
    /// 诊断用：只做「分析 → 合成」不跑模型，用于验证 FFT/OLA 是否恒等重建。
    /// 若此模式下仍然对不上，问题就在 FFT 或重叠相加，而不是模型接线。
    /// </summary>
    public bool BypassModel { get; set; }

    private static int ReadInt(IDictionary<string, string> metadata, string key, int fallback)
        => metadata.TryGetValue(key, out var raw) && int.TryParse(raw.Trim(), out var value) ? value : fallback;

    /// <summary>
    /// 按元数据声明的窗口类型建窗。
    /// DPDFNet 用 <c>vorbis</c>；GTCRN 用 <c>hann_sqrt</c>（分析合成各用 sqrt-Hann，
    /// 乘积即 Hann，50% 重叠下满足 COLA）。
    /// </summary>
    private static float[] BuildAnalysisWindow(int length, string windowType)
    {
        var window = new float[length];

        switch (windowType)
        {
            case "hann_sqrt":
            case "sqrt_hann":
                for (var i = 0; i < length; i++)
                    window[i] = MathF.Sqrt(0.5f * (1f - MathF.Cos(2f * MathF.PI * i / length)));
                break;

            case "hann":
                for (var i = 0; i < length; i++)
                    window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / length));
                break;

            default:   // vorbis
                var half = length / 2.0;
                for (var i = 0; i < length; i++)
                {
                    var s = Math.Sin(0.5 * Math.PI * (i + 0.5) / half);
                    window[i] = (float)Math.Sin(0.5 * Math.PI * s * s);
                }

                break;
        }

        return window;
    }

    /// <summary>
    /// 找出与某状态输入配对的输出名。
    /// 约定是同名加 <c>_out</c> 后缀（GTCRN: conv_cache→conv_cache_out；
    /// PureVox: enc_c→enc_c_out），DPDFNet 则是 state_in→state_out 的成对命名。
    /// </summary>
    private static string? FindStateOutput(string[] outputNames, string inputName)
    {
        var direct = outputNames.FirstOrDefault(name =>
            name.Equals(inputName + "_out", StringComparison.OrdinalIgnoreCase));
        if (direct != null) return direct;

        if (inputName.EndsWith("_in", StringComparison.OrdinalIgnoreCase))
        {
            var paired = outputNames.FirstOrDefault(name =>
                name.Equals(inputName[..^3] + "_out", StringComparison.OrdinalIgnoreCase));
            if (paired != null) return paired;
        }

        // 退一步：按出现顺序配对由调用方保证（这里返回未使用的同名后缀匹配）
        return outputNames.FirstOrDefault(name =>
            name.StartsWith(inputName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>从元数据构造状态初值：零 + 该状态对应的归一化初值。</summary>
    private static float[] BuildInitialState(IDictionary<string, string> metadata, string stateName, int size)
    {
        var state = new float[size];

        // DPDFNet 把归一化初值放在一个合并状态里：前 erb_norm_state_size 个是 erb_norm_init，
        // 紧接着 spec_norm_state_size 个是 spec_norm_init。
        // 其它模型的 cache 状态初值就是全零。
        var isCombinedState = stateName.Contains("state", StringComparison.OrdinalIgnoreCase);

        if (isCombinedState)
        {
            var erbSize = ReadInt(metadata, "erb_norm_state_size", 0);
            var specSize = ReadInt(metadata, "spec_norm_state_size", 0);

            Write(metadata, "erb_norm_init", state, 0, erbSize);
            Write(metadata, "spec_norm_init", state, erbSize, specSize);
        }

        return state;

        static void Write(IDictionary<string, string> metadata, string key, float[] target, int offset, int count)
        {
            if (count <= 0 || offset + count > target.Length) return;
            if (!metadata.TryGetValue(key, out var raw)) return;

            var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var written = 0;
            foreach (var part in parts)
            {
                if (written >= count) break;
                if (float.TryParse(part, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var value))
                    target[offset + written++] = value;
            }
        }
    }

    /// <summary>
    /// 处理一块 48 kHz 音频。内部流程：
    ///   48 kHz 输入 → 降到模型采样率 → 按模型 hop 分帧推理 → 升回 48 kHz
    /// 顺序很重要：**先收输入并推理，再把新产生的输出交给调用方**。
    /// </summary>
    public void Process(Span<float> frame)
    {
        if (frame.Length == 0) return;

        // 0) 采样率不同则先降采样
        Span<float> modelInput;
        if (_downConverter.IsBypassed)
        {
            modelInput = frame;
        }
        else
        {
            var converted = _downConverter.Process(frame, _rateStage);
            modelInput = _rateStage.AsSpan(0, converted);
        }

        // 1) 收进模型侧输入缓存
        foreach (var sample in modelInput)
        {
            if (_inputCount >= _inputBuffer.Length) break;   // 防御：极端情况下丢弃
            _inputBuffer[_inputCount++] = sample;
        }

        // 2) 凑够一帧就推理
        while (_inputCount >= _fftSize)
        {
            ProcessOneFrame();

            Array.Copy(_inputBuffer, _hopSize, _inputBuffer, 0, _inputCount - _hopSize);
            _inputCount -= _hopSize;
        }

        // 3) 取出模型侧输出
        var modelOutputCount = 0;
        while (modelOutputCount < _rateStage.Length && _outputCount > 0)
        {
            _rateStage[modelOutputCount++] = _outputBuffer[_outputPosition];
            _outputBuffer[_outputPosition] = 0f;
            _outputPosition = (_outputPosition + 1) % _outputBuffer.Length;
            _outputCount--;
        }

        // 4) 升采样回 48 kHz 交给调用方（不足部分补零，保证前后长度一致）
        if (_upConverter.IsBypassed)
        {
            var copy = Math.Min(modelOutputCount, frame.Length);
            _rateStage.AsSpan(0, copy).CopyTo(frame);
            for (var i = copy; i < frame.Length; i++) frame[i] = 0f;
            return;
        }

        var produced = _upConverter.Process(_rateStage.AsSpan(0, modelOutputCount), frame);
        for (var i = produced; i < frame.Length; i++) frame[i] = 0f;
    }

    private void ProcessOneFrame()
    {
        // 1) 因果 STFT：取前 win_len 个样本加 Vorbis 窗，再做实数 FFT
        for (var i = 0; i < _fftSize; i++)
        {
            _frameReal[i] = _inputBuffer[i] * _window[i];
            _frameImaginary[i] = 0f;
        }

        ForwardRealFft(_frameReal, _frameImaginary);

        if (BypassModel)
        {
            // 直接把分析结果当作"模型输出"送进合成，检验 FFT + OLA 是否恒等重建
            InverseRealFft(_frameReal, _frameImaginary);

            for (var i = 0; i < _fftSize; i++)
            {
                var index = (_outputPosition + _outputCount + i) % _outputBuffer.Length;
                _outputBuffer[index] += _frameReal[i] * _window[i];
            }

            _outputCount += _hopSize;
            return;
        }

        // 2) 按模型声明的形状组张量。
        // 复数维（长度 2）可能出现在不同位置：DPDFNet 是 [1,1,481,2]、GTCRN 是 [1,257,1,2]，
        // 因此这里按形状算出频点维与复数维，再用线性索引填充，而不是硬编码布局。
        var shape = _specShape;
        var complexAxis = -1;
        var binAxis = -1;
        for (var axis = 0; axis < shape.Length; axis++)
        {
            if (shape[axis] == 2 && complexAxis < 0) complexAxis = axis;
            else if (shape[axis] > 2 && binAxis < 0) binAxis = axis;
        }

        if (complexAxis < 0 || binAxis < 0)
            throw new InvalidOperationException($"无法从形状 [{string.Join(",", shape)}] 判断频点维与复数维。");

        // 该维之后所有维度的乘积，即该维在扁平缓冲里的步长
        var binStride = 1;
        for (var axis = binAxis + 1; axis < shape.Length; axis++) binStride *= shape[axis];
        var complexStride = 1;
        for (var axis = complexAxis + 1; axis < shape.Length; axis++) complexStride *= shape[axis];

        var binsInModel = shape[binAxis];

        // 复用预分配的张量（避免每帧分配造成 GC 压力）
        var tensor = _specTensor;
        var flat = tensor.Buffer.Span;
        flat.Clear();

        var count = Math.Min(_bins, binsInModel);
        for (var b = 0; b < count; b++)
        {
            flat[b * binStride] = _frameReal[b];
            flat[b * binStride + complexStride] = _frameImaginary[b];
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_specInput, tensor),
        };

        foreach (var state in _states)
        {
            if (state.Tensor == null) continue;
            state.Current.CopyTo(state.Tensor.Buffer.Span);
            inputs.Add(NamedOnnxValue.CreateFromTensor(state.Input, state.Tensor));
        }

        using var results = _session.Run(inputs);

        foreach (var result in results)
        {
            if (result.Name == _specOutput)
            {
                var data = result.AsEnumerable<float>().ToArray();
                for (var b = 0; b < count; b++)
                {
                    _frameReal[b] = data[b * binStride];
                    _frameImaginary[b] = data[b * binStride + complexStride];
                }

                continue;
            }

            // 状态回传：按输出名找到对应状态并更新
            for (var i = 0; i < _states.Count; i++)
            {
                if (_states[i].Output != result.Name) continue;
                var values = result.AsEnumerable<float>().ToArray();
                var state = _states[i];
                if (values.Length == state.Current.Length)
                {
                    Array.Copy(values, state.Current, values.Length);
                }
                else if (values.Length == state.Initial.Length)
                {
                    var resized = new float[state.Current.Length];
                    Array.Copy(values, resized, Math.Min(values.Length, resized.Length));
                    Array.Copy(resized, state.Current, resized.Length);
                }

                break;
            }
        }

        // 3) 逆实数 FFT → 加窗 → 重叠相加
        InverseRealFft(_frameReal, _frameImaginary);

        for (var i = 0; i < _fftSize; i++)
        {
            var index = (_outputPosition + _outputCount + i) % _outputBuffer.Length;
            _outputBuffer[index] += _frameReal[i] * _window[i];
        }

        // 4) Vorbis 窗 50% 重叠满足 COLA，因此前 hop 个样本已经"完成"，可以提交
        _outputCount += _hopSize;
    }

    /// <summary>
    /// 正向实数 FFT。n_fft=960 = 2^6×3×5，用递归混合基 FFT（<see cref="MixedRadixFft"/>），
    /// 把实数序列当作虚部为 0 的复数序列处理，取前 bins 个频点。
    /// </summary>
    private void ForwardRealFft(float[] real, float[] imaginary)
    {
        Array.Clear(imaginary, 0, _fftSize);
        MixedRadixFft.Forward(real, imaginary);
    }

    /// <summary>
    /// 逆向实数 FFT：先用共轭对称补全负频率，再走混合基 FFT 的共轭技巧
    /// （ifft(X) = conj(fft(conj(X)))/N），最后取实部。
    /// </summary>
    private void InverseRealFft(float[] real, float[] imaginary)
    {
        // 补全负频率，得到完整的 fftSize 点共轭对称频谱
        for (var k = 1; k < _fftSize / 2; k++)
        {
            real[_fftSize - k] = real[k];
            imaginary[_fftSize - k] = -imaginary[k];
        }

        imaginary[_fftSize / 2] = 0f;   // Nyquist 必须为实数

        // ifft(X) = conj(fft(conj(X))) / N
        for (var i = 0; i < _fftSize; i++) imaginary[i] = -imaginary[i];

        MixedRadixFft.Forward(real, imaginary);

        var scale = 1f / _fftSize;
        for (var i = 0; i < _fftSize; i++) real[i] *= scale;
    }

    public void Reset()
    {
        foreach (var state in _states) Array.Copy(state.Initial, state.Current, state.Current.Length);
        _inputCount = 0;
        _outputCount = 0;
        _outputPosition = 0;
        Array.Clear(_inputBuffer);
        Array.Clear(_outputBuffer);
    }

    public void Dispose() => _session.Dispose();
}
