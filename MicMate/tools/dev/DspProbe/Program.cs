using MicMate.Core;
using MicMate.Dsp;
using NAudio.Wave;

// DenoiseEffect 回归探针。
//
// 背景：Dsp/DenoiseEffect.cs 曾因"处理结果直接写回调用方缓冲区"而在
// 块长不是 480 整数倍时抛 ArgumentOutOfRangeException（效果被 DynamicChain 吞掉，
// 表现为降噪静默失效），随后的一次重写又把干湿混合写成了恒等于 0 的空操作。
// 本探针把当前实现按各种块长跑一遍，用可判定的断言锁住三件事：
//   1. 任意块长都不抛异常、不产生 NaN；
//   2. 输出恰好是"输入延迟 480 样本"，帧顺序/对齐正确（不重不漏）；
//   3. 模型输出真的进入了音频（干湿混合不是空操作），直通路径逐样本精确。

const int SampleRate = 48000;
const int FrameSize = SpectralDenoiseModel.FrameSize;   // 480

var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);
var failures = 0;

void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + what);
    if (!ok) failures++;
}

void Section(string title) => Console.WriteLine(Environment.NewLine + "== " + title);

DenoiseSettings NewSettings(float wet = 100f, float strength = 100f)
    => new() { Enabled = true, Wet = wet, Strength = strength };

DenoiseEffect NewEffect(IDenoiseModel model, float wet = 100f, float strength = 100f)
    => new(format, NewSettings(wet, strength), model) { Enabled = true };

// 非周期信号：用伪随机序列，这样"延迟 480"这类断言不会被正弦的周期性掩盖
float[] Noise(int count, float amplitude = 0.3f, int seed = 1234)
{
    var rnd = new Random(seed);
    var x = new float[count];
    for (var i = 0; i < count; i++) x[i] = (float)(rnd.NextDouble() * 2 - 1) * amplitude;
    return x;
}

float[] Tone(int count, float hz, float amplitude = 0.3f)
{
    var x = new float[count];
    for (var i = 0; i < count; i++)
        x[i] = amplitude * MathF.Sin(2f * MathF.PI * hz * i / SampleRate);
    return x;
}

float[] RunBlocks(DenoiseEffect fx, float[] input, int[] blockSizes)
{
    var output = (float[])input.Clone();
    var pos = 0;
    var k = 0;
    while (pos < output.Length)
    {
        var size = blockSizes[Math.Min(k++, blockSizes.Length - 1)];
        var n = Math.Min(size, output.Length - pos);
        fx.Read(output.AsSpan(pos, n));
        pos += n;
    }

    return output;
}

int[] Blocks(int size) => new[] { size };

int[] RandomBlocks(int total, int min, int max, int seed)
{
    var rnd = new Random(seed);
    var list = new List<int>();
    var sum = 0;
    while (sum < total)
    {
        var n = rnd.Next(min, max + 1);
        list.Add(n);
        sum += n;
    }

    return list.ToArray();
}

bool AllFinite(float[] x)
{
    foreach (var v in x)
        if (!float.IsFinite(v)) return false;
    return true;
}

float MaxStep(float[] x, int from, int to)
{
    var m = 0f;
    for (var i = from + 1; i < to; i++) m = Math.Max(m, MathF.Abs(x[i] - x[i - 1]));
    return m;
}

double Rms(float[] x, int from, int to)
{
    double sum = 0;
    for (var i = from; i < to; i++) sum += (double)x[i] * x[i];
    return Math.Sqrt(sum / Math.Max(1, to - from));
}

/// <summary>测某个频率的稳态增益（输出 RMS / 输入 RMS，取后半段以避开瞬态）。</summary>
float MeasureGain(IAudioEffect effect, float hz)
{
    var x = Tone(SampleRate, hz, 0.25f);
    var y = (float[])x.Clone();
    for (var pos = 0; pos < y.Length; pos += 480)
        effect.Read(y.AsSpan(pos, Math.Min(480, y.Length - pos)));
    return (float)(Rms(y, y.Length / 2, y.Length) / Rms(x, x.Length / 2, x.Length));
}

// ---------------------------------------------------------------- 1. 块长无关
Section("1. 任意块长：输出 = 输入延迟 480 样本，且不抛异常");
var blockSizes = new[] { 1, 16, 100, 144, 160, 240, 360, 480, 512, 720, 960, 1024, 4096, 8192 };
var input1 = Noise(48000);
foreach (var size in blockSizes)
{
    try
    {
        var fx = NewEffect(new StubModel(StubKind.Identity));
        var output = RunBlocks(fx, input1, Blocks(size));

        var ok = AllFinite(output);
        for (var n = 0; n < FrameSize && ok; n++) ok &= MathF.Abs(output[n]) < 1e-9f;          // 预热静音
        for (var n = FrameSize; n < output.Length && ok; n++) ok &= MathF.Abs(output[n] - input1[n - FrameSize]) < 1e-6f;

        Check(ok, $"块长 {size,5}：480 样本恒定延迟 + 预热静音");
    }
    catch (Exception ex)
    {
        Check(false, $"块长 {size,5}：抛出 {ex.GetType().Name}: {ex.Message}");
    }
}

try
{
    var fx = NewEffect(new StubModel(StubKind.Identity));
    var sizes = RandomBlocks(input1.Length, 1, 1024, 99);
    var output = RunBlocks(fx, input1, sizes);

    var ok = AllFinite(output);
    for (var n = 0; n < FrameSize && ok; n++) ok &= MathF.Abs(output[n]) < 1e-9f;
    for (var n = FrameSize; n < output.Length && ok; n++) ok &= MathF.Abs(output[n] - input1[n - FrameSize]) < 1e-6f;

    Check(ok, $"随机块长（1–1024，共 {sizes.Length} 块）：仍精确延迟 480");
}
catch (Exception ex)
{
    Check(false, "随机块长：抛出 " + ex.GetType().Name + ": " + ex.Message);
}

// ---------------------------------------------------------------- 2. 干湿混合
Section("2. 干湿混合：模型输出必须真的进入音频");
{
    // 完美降噪（输出静音）+ 100% 湿 → 全静音
    var fx = NewEffect(new StubModel(StubKind.Silence));
    var output = RunBlocks(fx, input1, Blocks(480));
    Check(Rms(output, FrameSize, output.Length) < 1e-9, "湿比 100% + 理想模型：输出全静音（降噪真的生效）");

    // 50% 湿 → 恰好是一半的延迟信号
    fx = NewEffect(new StubModel(StubKind.Silence), wet: 50f);
    output = RunBlocks(fx, input1, Blocks(480));
    var ok = true;
    for (var n = FrameSize; n < output.Length && ok; n++)
        ok &= MathF.Abs(output[n] - 0.5f * input1[n - FrameSize]) < 1e-6f;
    Check(ok, "湿比 50%：输出 = 0.5 × 延迟输入");

    // 模型输出恒定 0.5 → 输出恒定 0.5
    fx = NewEffect(new StubModel(StubKind.Constant) { Value = 0.5f });
    output = RunBlocks(fx, input1, Blocks(480));
    ok = true;
    for (var n = FrameSize; n < output.Length && ok; n++) ok &= MathF.Abs(output[n] - 0.5f) < 1e-6f;
    Check(ok, "模型输出 0.5：输出恒为 0.5（帧对齐正确，未错位到上一帧）");
}

// ---------------------------------------------------------------- 3. 直通精确
Section("3. 直通路径：逐样本一致且不引入延迟");
{
    foreach (var (name, fx) in new (string, DenoiseEffect)[]
             {
                 ("Enabled=false", new DenoiseEffect(format, NewSettings(), new StubModel(StubKind.Silence)) { Enabled = false }),
                 ("湿比 0", NewEffect(new StubModel(StubKind.Silence), wet: 0f)),
                 ("强度 0", NewEffect(new StubModel(StubKind.Silence), strength: 0f)),
             })
    {
        var output = RunBlocks(fx, input1, Blocks(720));
        var ok = true;
        for (var n = 0; n < output.Length && ok; n++) ok &= MathF.Abs(output[n] - input1[n]) < 1e-9f;
        Check(ok, $"{name}：输出与输入逐样本相同（零延迟、零改动）");
    }
}

// ---------------------------------------------------------------- 4. 帧边界台阶
Section("4. 帧边界：块长 720（与 480 不对齐）时不应出现台阶");
{
    var tone = Tone(48000, 1000f);
    var fx = NewEffect(new StubModel(StubKind.Gain) { Value = 0.5f });
    var output = RunBlocks(fx, tone, Blocks(720));

    var inputStep = MaxStep(tone, 0, tone.Length);
    var outputStep = MaxStep(output, FrameSize + 1, output.Length);
    Check(outputStep <= inputStep * 1.05f,
        $"输出最大台阶 {outputStep:0.00000} ≤ 输入自身最大台阶 {inputStep:0.00000} × 1.05（无帧边界跳变）");
}

// ---------------------------------------------------------------- 5. 帧顺序
Section("5. 帧顺序：每帧恰好交付一次、按序交付");
{
    // 喂 6 帧，前 5 帧的处理结果会在这 6 帧的时长内被交出（第 6 帧的结果在更后面）
    var fx = NewEffect(new StubModel(StubKind.Counter));
    var total = FrameSize * 6;
    var output = RunBlocks(fx, Noise(total), Blocks(147));   // 147 与 480 不对齐
    var ok = true;
    for (var frame = 0; frame < 5 && ok; frame++)
    {
        var expected = frame + 1;
        for (var i = 0; i < FrameSize && ok; i++)
            ok &= MathF.Abs(output[(frame + 1) * FrameSize + i] - expected) < 1e-6f;
    }

    Check(ok, "第 k 个输出帧等于第 k 个输入帧的处理结果（1,2,3,4,5）");
}

// ---------------------------------------------------------------- 6. 真实保底模型
Section("6. 真实保底模型（SpectralDenoiseModel）：不破坏音频、且能压住安静的底噪");
{
    // 前半段：响的 300 Hz 人声样信号（应当完整通过）
    // 后半段：只有很低的底噪（-55 dBFS 量级，应当被明显压下去）
    var loud = Tone(SampleRate, 300f, 0.2f);
    var quiet = Noise(SampleRate, 0.003f, 7);
    var noisy = new float[SampleRate * 2];
    var hiss = Noise(SampleRate, 0.004f, 11);
    for (var i = 0; i < SampleRate; i++)
    {
        noisy[i] = loud[i] + hiss[i];
        noisy[SampleRate + i] = quiet[i];
    }

    var fx = NewEffect(new SpectralDenoiseModel(), wet: 100f, strength: 70f);
    var output = RunBlocks(fx, noisy, Blocks(512));

    // 注意测量窗口要避开"响→轻"切换点：高通滤波器会带着前面响信号的振铃衰减下来
    // （约 2 ms 时间常数），从边界处量会把振铃算成"底噪被放大"。
    var settle = SampleRate + SampleRate / 20;   // 切换后 50 ms
    var loudIn = Rms(noisy, FrameSize, SampleRate);
    var loudOut = Rms(output, FrameSize, SampleRate);
    var quietIn = Rms(noisy, settle, noisy.Length);
    var quietOut = Rms(output, settle, output.Length);

    Check(AllFinite(output), "输出全部为有限值（无 NaN/Inf）");
    Check(Rms(output, 0, FrameSize) < 1e-9, "预热的第一帧为静音（帧式处理的固有代价）");
    Check(loudOut > loudIn * 0.7, $"有声段基本直通（{loudIn:0.00000} → {loudOut:0.00000}）");
    Check(quietOut < quietIn * 0.5, $"底噪段被明显压低（{quietIn:0.00000} → {quietOut:0.00000}，" +
                                    $"{20 * Math.Log10(quietOut / quietIn):0.0} dB）");
}

// ---------------------------------------------------------------- 7. 模型切换
Section("7. 切换模型：旧模型不在 UI 线程释放");
{
    var oldModel = new StubModel(StubKind.Silence);
    var fx = NewEffect(oldModel);
    RunBlocks(fx, Noise(4800), Blocks(480));

    var newModel = new StubModel(StubKind.Constant) { Value = 0.25f };
    fx.SetModel(newModel);
    Check(!oldModel.Disposed, "SetModel 之后旧模型尚未被释放（避免与音频线程竞争）");
    Check(ReferenceEquals(fx.Model, newModel), "Model 已指向新模型");

    var output = RunBlocks(fx, Noise(4800), Blocks(480));
    Check(oldModel.Disposed, "下一次 Read 之后旧模型被释放");
    Check(MathF.Abs(output[FrameSize + 10] - 0.25f) < 1e-6f, "切换后由新模型接管处理");

    // 并发烟测：音频线程持续 Read 的同时从另一线程切模型
    var running = true;
    var busy = new StubModel(StubKind.Gain) { Value = 0.9f, FrameDelayMs = 1 };
    var fx2 = NewEffect(busy);
    var error = string.Empty;
    var worker = new Thread(() =>
    {
        try
        {
            var scratch = new float[480];
            while (Volatile.Read(ref running)) fx2.Read(scratch);
        }
        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
    }) { IsBackground = true };
    worker.Start();
    for (var i = 0; i < 20; i++)
    {
        fx2.SetModel(new StubModel(StubKind.Silence));
        Thread.Sleep(15);
    }

    Volatile.Write(ref running, false);
    worker.Join(3000);
    Check(error.Length == 0, "边处理边切模型：音频线程无异常" + (error.Length == 0 ? string.Empty : "（" + error + "）"));
}

// ---------------------------------------------------------------- 8. 推理超时
Section("8. 单帧推理超过 50 ms：该帧直通并计入跳过数");
{
    // 只喂 3 帧，便于精确断言计数
    var probe = Noise(FrameSize * 3);
    var fx = NewEffect(new StubModel(StubKind.Silence) { FrameDelayMs = 60 });
    var output = RunBlocks(fx, probe, Blocks(480));

    var ok = true;
    for (var n = FrameSize; n < output.Length && ok; n++) ok &= MathF.Abs(output[n] - probe[n - FrameSize]) < 1e-6f;
    Check(ok, "超时帧输出干信号（直通）而不是静音或垃圾数据");
    Check(fx.SkippedFrames == 3, $"SkippedFrames == 3（实际 {fx.SkippedFrames}）");
    Check(fx.ProcessedFrames == 3, $"ProcessedFrames == 3（实际 {fx.ProcessedFrames}）");
}

// ---------------------------------------------------------------- 9. 保底模型单独诊断
Section("9. 诊断：保底模型单独跑低电平白噪声（不经 DenoiseEffect）");
{
    var noise = Noise(SampleRate * 2, 0.003f, 21);
    var input = (float[])noise.Clone();

    var model = new SpectralDenoiseModel { Strength = 70f };
    for (var off = 0; off + FrameSize <= noise.Length; off += FrameSize)
        model.Process(noise.AsSpan(off, FrameSize));

    Console.WriteLine($"    输入 RMS {Rms(input, FrameSize, input.Length):0.000000} → 输出 RMS {Rms(noise, FrameSize, noise.Length):0.000000}" +
                      $"（{20 * Math.Log10(Rms(noise, FrameSize, noise.Length) / Rms(input, FrameSize, input.Length)):0.0} dB）");
    Console.WriteLine($"    模型内部：LastGain = {model.LastGain:0.0000}（{20 * Math.Log10(Math.Max(model.LastGain, 1e-9f)):0.0} dB）" +
                      $"，LastEnvelopeDb = {model.LastEnvelopeDb:0.0} dBFS");
}

// ---------------------------------------------------------------- 10. 响度平衡
Section("10. 响度平衡：峰值保护立即生效、静音/底噪不被加大、能收敛到目标");
{
    var settings = new LoudnessSettings { Enabled = true, TargetLufs = -20f, Speed = 5 };

    // (a) 从 −40 dBFS 突然跳到 0 dBFS：输出绝不允许越过 −1 dBFS
    var step = new float[SampleRate];
    for (var i = 0; i < step.Length; i++)
    {
        var amp = i < SampleRate / 2 ? 0.01f : 1.0f;
        step[i] = amp * MathF.Sin(2f * MathF.PI * 440f * i / SampleRate);
    }

    var stepOut = (float[])step.Clone();
    var fxStep = new LoudnessBalanceEffect(format, settings) { Enabled = true };
    for (var pos = 0; pos < stepOut.Length; pos += 480)
        fxStep.Read(stepOut.AsSpan(pos, Math.Min(480, stepOut.Length - pos)));

    var peak = 0f;
    for (var i = 0; i < stepOut.Length; i++) peak = MathF.Max(peak, MathF.Abs(stepOut[i]));
    Check(peak <= 0.8915f, $"0 dBFS 阶跃后输出峰值 {peak:0.0000}（≤ −1 dBFS；修复前实测 4.20 = +12.5 dBFS）");

    // (b) 3 秒数字静音：增益必须保持不动
    var silence = new float[SampleRate * 3];
    var fxSilence = new LoudnessBalanceEffect(format, settings) { Enabled = true };
    for (var pos = 0; pos < silence.Length; pos += 480)
        fxSilence.Read(silence.AsSpan(pos, Math.Min(480, silence.Length - pos)));
    Check(fxSilence.CurrentGainDb < 1f, $"3 秒静音后增益 {fxSilence.CurrentGainDb:0.0} dB（修复前为 +22 dB）");

    // (c) 很低的底噪：同样不能被抬高
    var floorNoise = Noise(SampleRate * 2, 0.001f, 5);   // ≈ −63 dBFS
    var fxNoise = new LoudnessBalanceEffect(format, settings) { Enabled = true };
    for (var pos = 0; pos < floorNoise.Length; pos += 480)
        fxNoise.Read(floorNoise.AsSpan(pos, Math.Min(480, floorNoise.Length - pos)));
    Check(fxNoise.CurrentGainDb < 1f, $"底噪段增益 {fxNoise.CurrentGainDb:0.0} dB（不被抬高）");

    // (d) −30 dBFS 稳态信号应当收敛到 −20 dBFS 附近（证明改动没有把 AGC 做废）
    var quiet = Tone(SampleRate * 4, 440f, 0.03f);
    var inDb = 20 * Math.Log10(Rms(quiet, 0, quiet.Length));
    var fxQuiet = new LoudnessBalanceEffect(format, settings) { Enabled = true };
    for (var pos = 0; pos < quiet.Length; pos += 480)
        fxQuiet.Read(quiet.AsSpan(pos, Math.Min(480, quiet.Length - pos)));
    var tailDb = 20 * Math.Log10(Rms(quiet, quiet.Length * 3 / 4, quiet.Length));
    Check(Math.Abs(tailDb + 20) < 3, $"输入 {inDb:0.0} dBFS → 尾段 {tailDb:0.0} dBFS（目标 −20，容差 3 dB）");
}

// ---------------------------------------------------------------- 11. 音色风格
Section("11. 音色风格：以「未选择」构造、之后再选预设，3 段必须全部生效");
{
    // 复现启动路径：界面默认一个预设都不选（Style = null），之后再点「沉稳」
    var toneSettings = new ToneSettings { Enabled = true, Style = null };
    var effect = new ToneStyleEffect(format, toneSettings) { Enabled = true };

    var flatTone = Tone(SampleRate, 1000f);
    var passthrough = (float[])flatTone.Clone();
    effect.Read(passthrough);
    var flat = true;
    for (var i = 0; i < passthrough.Length && flat; i++)
        flat &= MathF.Abs(passthrough[i] - flatTone[i]) < 1e-6f;
    Check(flat, "未选择风格时是直通（逐样本一致）");

    toneSettings.Style = ToneStyle.Warm;
    effect.UpdateParameters();

    var at175 = MeasureGain(effect, 175f);
    var at6k = MeasureGain(effect, 6000f);
    Check(at175 > 1.6f, $"175 Hz 处 ≈ +6 dB（实测 {20 * Math.Log10(at175):0.0} dB）");
    Check(at6k < 0.6f, $"6 kHz 处 ≈ −6 dB（实测 {20 * Math.Log10(at6k):0.0} dB；修复前该段根本没建，为 0 dB）");
}

Console.WriteLine();
Console.WriteLine(failures == 0
    ? "全部通过：DenoiseEffect / LoudnessBalanceEffect / ToneStyleEffect 的回归项均符合预期。"
    : $"有 {failures} 项未通过。");
return failures == 0 ? 0 : 1;

// ================================================================= 测试替身

internal enum StubKind
{
    /// <summary>完美降噪：输出全 0。</summary>
    Silence,

    /// <summary>原样返回（用于验证"输出 = 输入延迟 480"）。</summary>
    Identity,

    /// <summary>输出恒定值。</summary>
    Constant,

    /// <summary>整体乘一个增益。</summary>
    Gain,

    /// <summary>输出当前帧序号（用于验证帧顺序与对齐）。</summary>
    Counter,
}

internal sealed class StubModel : IDenoiseModel
{
    private int _frames;

    public StubModel(StubKind kind) => Kind = kind;

    public StubKind Kind { get; }

    public float Value { get; init; }

    /// <summary>模拟一次推理的耗时，用于验证 50 ms 超时路径。</summary>
    public int FrameDelayMs { get; init; }

    public bool Disposed { get; private set; }

    public string Name => "测试替身/" + Kind;

    public string TensorInfo => "—";

    public void Process(Span<float> frame)
    {
        if (FrameDelayMs > 0) Thread.Sleep(FrameDelayMs);

        switch (Kind)
        {
            case StubKind.Silence:
                frame.Clear();
                break;
            case StubKind.Identity:
                break;
            case StubKind.Constant:
                frame.Fill(Value);
                break;
            case StubKind.Gain:
                for (var i = 0; i < frame.Length; i++) frame[i] *= Value;
                break;
            case StubKind.Counter:
                frame.Fill(++_frames);
                break;
        }
    }

    public void Reset() => _frames = 0;

    public void Dispose() => Disposed = true;
}

