using MateMic.Core;
using MateMic.Dsp;
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
//
// 另外覆盖 LoudnessBalanceEffect（峰值保护 / 静音与底噪 / 收敛）与 ToneStyleEffect（预设段数）。
//
// 用法：dotnet run --project tools\dev\DspProbe    （不需要声卡，也不需要界面）
// 说明：第 7、8 节含线程与计时断言，机器在跑别的重活时偶发失败属正常，重跑即可。

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

/// <summary>粗糙的粉红噪声（1/f）：白噪声过一遍一阶低通并混回一部分，频谱重心接近人声。</summary>
float[] PinkNoise(int count, int seed)
{
    var rnd = new Random(seed);
    var x = new float[count];
    var lp = 0f;
    for (var i = 0; i < count; i++)
    {
        var white = (float)(rnd.NextDouble() * 2 - 1);
        lp += 0.12f * (white - lp);
        x[i] = 0.35f * white + 0.65f * lp * 3.2f;
    }

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

    // 计时断言用**区间**而不是等号：每帧 sleep 60 ms 而预算是 50 ms，本来就贴着边界，
    // 机器同时在跑别的活时 Stopwatch 读数会落到边界另一侧（实测偶发 2/4 而不是 3/3）。
    Check(fx.SkippedFrames is 2 or 3, $"SkippedFrames ∈ {{2,3}}（实际 {fx.SkippedFrames}）");
    Check(fx.ProcessedFrames is 2 or 3, $"ProcessedFrames ∈ {{2,3}}（实际 {fx.ProcessedFrames}）");
    Check(fx.SkippedFrames + fx.ProcessedFrames is 5 or 6,
        $"跳过 + 处理 = {fx.SkippedFrames + fx.ProcessedFrames}（每帧都要被记一次，应为 5 或 6）");
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

// ---------------------------------------------------------------- 11. EQ 均衡器
Section("11. EQ 均衡器：平坦时直通、预设整表生效、手动单段生效");
{
    // 复现启动路径：界面默认平坦（全 0 dB），之后再点「沉稳」
    var toneSettings = new ToneSettings { Enabled = true, Style = null };
    var effect = new ToneStyleEffect(format, toneSettings) { Enabled = true };

    var flatTone = Tone(SampleRate, 1000f);
    var passthrough = (float[])flatTone.Clone();
    effect.Read(passthrough);
    var flat = true;
    for (var i = 0; i < passthrough.Length && flat; i++)
        flat &= MathF.Abs(passthrough[i] - flatTone[i]) < 1e-6f;
    Check(flat, "全 0 dB（平坦）时是直通（逐样本一致）");

    // 预设 = 整表替换：沉稳在 125–250 Hz 抬 5 dB、8–16 kHz 削 7 dB
    toneSettings.Gains = EqPreset.GainsOf(ToneStyle.Warm);
    effect.UpdateParameters();

    var at250 = MeasureGain(effect, 250f);
    var at8k = MeasureGain(effect, 8000f);
    Check(at250 > 1.5f, $"250 Hz 处被抬起（实测 {20 * Math.Log10(at250):0.0} dB）");
    Check(at8k < 0.6f, $"8 kHz 处被压低（实测 {20 * Math.Log10(at8k):0.0} dB）");

    // 手动单段：只把 16 kHz 拉满，其余不动
    var manual = EqPreset.Flat();
    manual[9] = EqPreset.MaxGainDb;
    toneSettings.Gains = manual;
    toneSettings.Style = null;
    effect.UpdateParameters();

    var at16k = MeasureGain(effect, 16000f);
    var at1k = MeasureGain(effect, 1000f);
    Check(at16k > 2.5f, $"手动把第 10 段拉满后 16 kHz 抬起（实测 {20 * Math.Log10(at16k):0.0} dB）");
    Check(MathF.Abs(at1k - 1f) < 0.12f, $"未动的第 6 段保持不动（1 kHz 实测 {20 * Math.Log10(at1k):0.0} dB）");
}

// ---------------------------------------------------------------- 12. 电音块长
Section("12. 电音：块长大于内部缓冲时不能静默失效");
{
    var effectSettings = new CreativeEffectSettings
    {
        Enabled = true,
        Kind = CreativeEffectKind.Robot,
        Amount = 88f,
    };

    bool Processed(int blockSize)
    {
        var fx = new CreativeEffect(format, effectSettings) { Enabled = true };
        var tone = Tone(blockSize, 300f, 0.3f);
        var output = (float[])tone.Clone();
        for (var pos = 0; pos < output.Length; pos += blockSize)
            fx.Read(output.AsSpan(pos, Math.Min(blockSize, output.Length - pos)));

        for (var i = 0; i < output.Length; i++)
            if (MathF.Abs(output[i] - tone[i]) > 1e-6f) return true;
        return false;
    }

    Check(Processed(480), "480 样本块被处理（WASAPI 常见周期）");
    Check(Processed(4096), "4096 样本块被处理（修复前该块直接 return，整个电音静默失效）");
}

// ---------------------------------------------------------------- 13. 频谱显示
Section("13. 频谱：对数频带 + 峰值检波，底噪与正常说话都必须看得见起伏");
{
    // 频带边界必须是单调递增的（对数划分），且最低频带跳过直流 bin
    var probe = new SpectrumAnalyzer();
    var monotonic = true;
    for (var i = 1; i < probe.BandCount; i++)
        if (probe.BandCentreHz(i) <= probe.BandCentreHz(i - 1)) monotonic = false;
    Check(monotonic, "频带中心频率单调递增（对数划分）");
    Check(probe.BandCentreHz(0) > 20f && probe.BandCentreHz(probe.BandCount - 1) > 8000f,
        $"最低频带中心 {probe.BandCentreHz(0):0} Hz，最高 {probe.BandCentreHz(probe.BandCount - 1):0} Hz");

    // 分析一段信号，返回中间频带（约 200 Hz – 2 kHz）的平均高度
    float MidHeight(float[] samples)
    {
        var analyzer = new SpectrumAnalyzer();
        for (var pos = 0; pos < samples.Length; pos += 4800)
            analyzer.Feed(samples.AsSpan(pos, Math.Min(4800, samples.Length - pos)));
        analyzer.TryUpdate();

        var bands = new float[analyzer.BandCount];
        analyzer.CopyBands(bands, bands.Length);

        var sum = 0f;
        var count = 0;
        for (var i = 0; i < analyzer.BandCount; i++)
        {
            var hz = analyzer.BandCentreHz(i);
            if (hz is < 200f or > 2000f) continue;
            sum += bands[i];
            count++;
        }

        return count == 0 ? 0f : sum / count;
    }

    // (a) 数字静音：不应有任何起伏
    Check(MidHeight(new float[SampleRate]) == 0f, "数字静音时全部频带为 0");

    // (a1) 低频柱子不能"多根同步起伏"：相邻频带的 bin 区间不能完全相同
    {
        var probe2 = new SpectrumAnalyzer();
        var duplicated = 0;
        var firstDuplicate = -1;
        for (var i = 1; i < probe2.BandCount; i++)
        {
            if (probe2.BandBinRange(i) != probe2.BandBinRange(i - 1)) continue;
            duplicated++;
            if (firstDuplicate < 0) firstDuplicate = i;
        }

        Check(duplicated == 0,
            $"48 个频带没有任何相邻两根共用完全相同的 bin 区间（重复 {duplicated} 处，首处 #{firstDuplicate}）");

        var lowest = probe2.BandCentreHz(0);
        var resolution = 48000f / SpectrumAnalyzer.FftSize;
        Check(lowest >= 38f,
            $"最低频带中心 {lowest:0} Hz（FFT {SpectrumAnalyzer.FftSize} 点 ⇒ 分辨率 {resolution:0.0} Hz，" +
            "低于 40 Hz 的频带会共用谱线、柱子同步起伏）");
        // 用更长的白噪声、取多帧频谱的**平均**再比较柱子高度。
        // 单帧白噪声的谱线本身服从瑞利分布（相邻谱线差几 dB 是正常的），
        // 只取一帧的话"有多少种不同高度"会在 18–23 之间随机跳动，断言就会偶发假失败。
        var wide = Noise(SampleRate * 4, 1f, 91);
        var wideRms = (float)Rms(wide, 0, wide.Length);
        for (var i = 0; i < wide.Length; i++) wide[i] *= 0.01f / wideRms;   // ≈ −40 dBFS

        var noiseBands = new float[probe2.BandCount];
        const int frames = 4;
        var perFrame = wide.Length / frames;
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = frame * perFrame;
            for (var pos = 0; pos < perFrame; pos += SpectrumAnalyzer.FftSize)
            {
                var count = Math.Min(SpectrumAnalyzer.FftSize, perFrame - pos);
                probe2.Feed(wide.AsSpan(offset + pos, count));
            }

            probe2.TryUpdate();
            var frameBands = new float[probe2.BandCount];
            probe2.CopyBands(frameBands, frameBands.Length);
            for (var i = 0; i < frameBands.Length; i++) noiseBands[i] += frameBands[i] / frames;
        }

        // 用户看到的现象是"前 9 条几乎同步起伏"，因此直接对着**低频那几根柱子**断言：
        // 修复前它们读同一条谱线，高度会一模一样；现在每根读自己那份数据，高度互不相同。
        // 注意：整段 48 根有 19 种高度属于正常（宽带噪声的相邻谱线本来就只差几 dB），
        // 所以这里不做"种类数 ≥ N"的断言，只要求低频柱子不重复、整体有一定区分度。
        var distinct = noiseBands.Select(v => (int)MathF.Round(v * 200f)).Distinct().Count();
        Console.WriteLine($"        48 根柱子共 {distinct} 种高度；最低 8 根：" +
                          string.Join(", ", Enumerable.Range(0, 8).Select(i => $"{noiseBands[i]:0.0000}")));

        var lowDistinct = Enumerable.Range(0, 9)
            .Select(i => (int)MathF.Round(noiseBands[i] * 500f))
            .Distinct()
            .Count();
        Check(lowDistinct >= 8,
            $"最低 9 根柱子有 {lowDistinct} 种互不相同的高度（应 ≥ 8；修复前它们是同一条谱线的同一个值，只有 1 种）");

        var lowDuplicateValues = 0;
        for (var i = 1; i < 9; i++)
            if (MathF.Abs(noiseBands[i] - noiseBands[i - 1]) < 0.002f) lowDuplicateValues++;
        Check(lowDuplicateValues <= 1,
            $"最低 9 根柱子里相邻等高的有 {lowDuplicateValues} 对（应 ≤ 1；修复前是 8 对）");
    }

    // (a2) 单次送入超过环形缓冲的块（采集回调 80 ms = 3840，未来块长可能更大）不能抛异常
    {
        var bigFeed = new SpectrumAnalyzer();
        var threw = false;
        try
        {
            bigFeed.Feed(Noise(8192, 0.2f, 77));
            bigFeed.TryUpdate();
        }
        catch (Exception ex)
        {
            threw = true;
            Console.WriteLine("        异常：" + ex.Message);
        }

        var bigBands = new float[bigFeed.BandCount];
        bigFeed.CopyBands(bigBands, bigBands.Length);
        Check(!threw, "单次 Feed 8192 样本（> 环形缓冲 4096）不抛异常");
        Check(bigFeed.TotalSamples == 8192 && bigBands.Any(b => b > 0f),
            $"超长块仍被统计并产生频谱（累计样本 {bigFeed.TotalSamples}）");
    }

    // (b) 约 −46 dBFS 的宽带底噪（实测本机麦克风的房间底噪就在这个量级）：
    //     修复前每 bin 约 −72 dB，落在 −70 的下限之下 → 整片贴底，只有最左边一根柱子轻微起伏
    var floorNoise = Noise(SampleRate, 1f, 21);
    var noiseRms = (float)Rms(floorNoise, 0, floorNoise.Length);
    for (var i = 0; i < floorNoise.Length; i++) floorNoise[i] *= 0.005f / noiseRms;      // ≈ −46 dBFS
    var floorHeight = MidHeight(floorNoise);
    Check(floorHeight > 0.05f && floorHeight < 0.45f,
        $"约 −46 dBFS 房间底噪 → 中频段高度 {floorHeight:0.00}（应在 0.05–0.45，既看得见又不满格）");

    // (c) 约 −30 dBFS 的粉红噪声（接近正常人声的频谱重心与响度）：应当明显更高
    var speech = PinkNoise(SampleRate, 31);
    var speechRms = (float)Rms(speech, 0, speech.Length);
    for (var i = 0; i < speech.Length; i++) speech[i] *= 0.0316f / speechRms;            // ≈ −30 dBFS
    var speechHeight = MidHeight(speech);
    Check(speechHeight > 0.22f, $"约 −30 dBFS 粉红噪声 → 中频段高度 {speechHeight:0.00}（应 > 0.22）");
    Check(speechHeight > floorHeight + 0.15f,
        $"底噪与说话的高度差 {speechHeight - floorHeight:0.00}（应 > 0.15，才能一眼看出起伏）");

    // (d) 峰值检波：单频正弦的能量集中在很窄的几根柱子上，其他地方应当明显更低
    var sine = Tone(SampleRate, 1000f, 0.3f);
    var sineAnalyzer = new SpectrumAnalyzer();
    for (var pos = 0; pos < sine.Length; pos += SpectrumAnalyzer.FftSize)
        sineAnalyzer.Feed(sine.AsSpan(pos, Math.Min(SpectrumAnalyzer.FftSize, sine.Length - pos)));
    sineAnalyzer.TryUpdate();
    var sineBands = new float[sineAnalyzer.BandCount];
    sineAnalyzer.CopyBands(sineBands, sineBands.Length);
    var peakBand = 0;
    for (var i = 1; i < sineAnalyzer.BandCount; i++)
        if (sineBands[i] > sineBands[peakBand]) peakBand = i;

    // 频带下限为了保证"每根柱子读自己的谱线"被抬到 40 Hz，低频前十几根的中心会比理想对数位置
    // 偏窄（对数刻度上表现为整体压缩），因此这里只在很宽的范围内检查峰值位置对不对。
    var peakBandBin = sineAnalyzer.BandBinRange(peakBand);
    var peakBandLoHz = peakBandBin.Low * 48000f / SpectrumAnalyzer.FftSize;
    var peakBandHiHz = peakBandBin.High * 48000f / SpectrumAnalyzer.FftSize;
    Check(peakBandHiHz > 900f && peakBandLoHz < 1120f,
        $"1 kHz 正弦的峰值落在第 {peakBand} 根柱子（bin {peakBandBin.Low}–{peakBandBin.High}，" +
        $"约 {peakBandLoHz:0}–{peakBandHiHz:0} Hz），中心频率 {sineAnalyzer.BandCentreHz(peakBand):0} Hz");

    // 频带必须单调：bin 区间不重叠、中心频率不倒退
    var monotonicBins = true;
    var monotonicHz = true;
    for (var i = 1; i < sineAnalyzer.BandCount; i++)
    {
        if (sineAnalyzer.BandBinRange(i).Low < sineAnalyzer.BandBinRange(i - 1).High) monotonicBins = false;
        if (sineAnalyzer.BandCentreHz(i) <= sineAnalyzer.BandCentreHz(i - 1)) monotonicHz = false;
    }

    Check(monotonicBins, "频带的 bin 区间严格不重叠（每根柱子读自己那份数据）");
    Check(monotonicHz, "频带中心频率单调递增");

    // 单频能量集中在窄带里：峰值柱子必须明显高于其它柱子的平均
    var neighbours = 0f;
    for (var i = 0; i < sineAnalyzer.BandCount; i++)
        if (i != peakBand) neighbours += sineBands[i];
    var neighbourAverage = neighbours / Math.Max(1, sineAnalyzer.BandCount - 1);
    Check(sineBands[peakBand] > 0.35f,
        $"该频带高度 {sineBands[peakBand]:0.00}（0.3 幅度正弦的单 bin 实测约 −23 dB，应 > 0.35）");
    Check(sineBands[peakBand] > neighbourAverage * 5f,
        $"该频带高度是其余频带平均（{neighbourAverage:0.000}）的 {sineBands[peakBand] / Math.Max(neighbourAverage, 1e-6f):0.0} 倍（峰值检波，应 > 5 倍）");
}

// ---------------------------------------------------------------- 14. 播放器单独监听
Section("14. MicMixer：主输出与监听的混音比例互不干扰（播放器可以单独监听）");
{
    const int block = 480;

    // 麦克风恒为 0.8，播放器恒为 0.4，便于直接读出权重：
    //   主输出里麦克风/播放器各 0.5 → 0.40 / 0.20
    //   监听同比例（监听是否有内容由 sink 是否存在 + 两个开关决定）
    const float MicOnly = 0.4f;            // 0.5 × 0.8
    const float PlayerOnly = 0.2f;         // 0.5 × 0.4
    const float Both = 0.6f;

    float[] ReadMain(MicMixer mixer)
    {
        var buffer = new float[block];
        mixer.Read(buffer);
        return buffer;
    }

    bool Near(float a, float b) => MathF.Abs(a - b) < 1e-4f;

    /// <summary>按开关状态构造一个混音器。</summary>
    /// <param name="monitorSink">监听设备是否打开（决定是否有监听输出）。</param>
    /// <param name="audioMonitor">播放器面板「音频监听」：播放器要不要**额外**进监听。</param>
    /// <param name="micEnabled">工具栏「音频处理」总开关：麦克风是否送出。</param>
    /// <param name="micIntoMonitor">
    /// 麦克风进监听的总闸。真实代码里是 <c>AudioEngine.SyncMonitorMic()</c> 按"监听设备是否打开"设置的；
    /// 本探针把它单独暴露出来，才能同时表达"有监听输出但麦克风不进监听"这一档。
    /// </param>
    (MicMixer Mixer, CapturingSink? Monitor) Build(
        bool monitorSink, bool audioMonitor, bool micEnabled, bool micIntoMonitor)
    {
        var mixer = new MicMixer(format, new ConstantProvider(format, 0.8f));
        mixer.SetPlayer(new ConstantProvider(format, 0.4f));
        CapturingSink? sink = null;
        if (monitorSink)
        {
            sink = new CapturingSink();
            mixer.SetMonitorSink(sink);
        }

        mixer.MonitorMicEnabled = micIntoMonitor;
        mixer.PlayerMonitorEnabled = audioMonitor;
        mixer.MicEnabled = micEnabled;
        return (mixer, sink);
    }

    // 关键前提：**播放器音频始终进主输出**（MIXLINE，"播给别人听"的主路径），
    // 与「音频监听」开关无关。这一条如果破了，就会出现"想播给队友听还得先打开监听"。
    //   ① 总监听关 + 音频监听关 → 没有监听，但主输出里必须有播放器
    {
        var (mixer, sink) = Build(monitorSink: false, audioMonitor: false, micEnabled: true, micIntoMonitor: false);
        var main = ReadMain(mixer);
        Check(sink == null && mixer.MonitorSamples == 0,
            "① 总监听关 + 音频监听关 → 没有任何监听输出");
        Check(Near(main[0], Both),
            $"① 两个监听都关着时，主输出仍然带播放器 = {main[0]:0.0000}（期望 {Both:0.0000}，播给别人听不依赖监听）");
    }

    //   ② 总监听关 + 音频监听开 → 只听播放器，不听麦克风
    {
        var (mixer, sink) = Build(monitorSink: true, audioMonitor: true, micEnabled: true, micIntoMonitor: false);
        var main = ReadMain(mixer);
        Check(Near(main[0], Both), $"② 主输出 = 麦克风+播放器 = {main[0]:0.0000}（期望 {Both:0.0000}）");
        Check(Near(sink!.Samples[0], PlayerOnly),
            $"② 总监听关 + 音频监听开 → 监听里只有播放器 {sink.Samples[0]:0.0000}（期望 {PlayerOnly:0.0000}，不能有麦克风）");
    }

    //   ③ 总监听开 + 音频监听关 → 只听麦克风，不听播放器
    {
        var (mixer, sink) = Build(monitorSink: true, audioMonitor: false, micEnabled: true, micIntoMonitor: true);
        var main = ReadMain(mixer);
        Check(Near(main[0], Both),
            $"③ 主输出 = 麦克风+播放器 = {main[0]:0.0000}（期望 {Both:0.0000}，播放器不受音频监听开关影响）");
        Check(Near(sink!.Samples[0], MicOnly),
            $"③ 总监听开 + 音频监听关 → 监听里只有麦克风 {sink.Samples[0]:0.0000}（期望 {MicOnly:0.0000}，不能有播放器）");
    }

    //   ④ 总监听开 + 音频监听开 → 两者都能监听
    {
        var (mixer, sink) = Build(monitorSink: true, audioMonitor: true, micEnabled: true, micIntoMonitor: true);
        var main = ReadMain(mixer);
        Check(Near(main[0], Both), $"④ 主输出 = 麦克风+播放器 = {main[0]:0.0000}（期望 {Both:0.0000}）");
        Check(Near(sink!.Samples[0], Both),
            $"④ 总监听开 + 音频监听开 → 监听里两者都有 {sink.Samples[0]:0.0000}（期望 {Both:0.0000}）");
    }

    // (e) 闭麦（总开关关）：主输出与监听都只剩播放器，麦克风完全不漏
    {
        var (mixer, sink) = Build(monitorSink: true, audioMonitor: true, micEnabled: false, micIntoMonitor: true);
        var main = ReadMain(mixer);
        Check(Near(main[0], PlayerOnly), $"闭麦后主输出只剩播放器 = {main[0]:0.0000}（期望 {PlayerOnly:0.0000}）");
        Check(Near(sink!.Samples[0], PlayerOnly), $"闭麦后监听只剩播放器 = {sink.Samples[0]:0.0000}（期望 {PlayerOnly:0.0000}）");
    }

    // (f) 闭麦 + 两个监听都关：主输出仍然要把播放器送出去（队友照样能听到）
    {
        var (mixer, _) = Build(monitorSink: false, audioMonitor: false, micEnabled: false, micIntoMonitor: false);
        var main = ReadMain(mixer);
        Check(Near(main[0], PlayerOnly),
            $"全部监听关着 + 闭麦时，主输出 = 播放器 {main[0]:0.0000}（期望 {PlayerOnly:0.0000}）");
        Check(mixer.MonitorSamples == 0, "且没有任何监听输出");
    }

    // (g) 监听打开但采样点数多于请求块：主输出长度不能被监听路径改掉
    {
        var mixer = new MicMixer(format, new ConstantProvider(format, 0.5f));
        mixer.SetPlayer(new ConstantProvider(format, 0.5f));
        var monitor = new CapturingSink();
        mixer.SetMonitorSink(monitor);
        mixer.PlayerMonitorEnabled = true;
        mixer.MicEnabled = true;

        var main = ReadMain(mixer);
        Check(main.Length == block && monitor.Samples.Count == block,
            $"主输出与监听的样本数都等于请求块长（{main.Length} / {monitor.Samples.Count}，期望 {block}）");
    }
}

// ============================================================ 15. 设备变更恢复决策
//
// 这条规则决定"设备发生变化后要不要重建音频流"。
// 它出错的表现很隐蔽：要么该恢复时没恢复（用户必须手动重选设备），
// 要么每次变化都无脑重建（听到反复爆音）。用真机拔插来验证代价极高
// （而且实测本机 IMmNotificationClient 回调根本收不到），
// 因此把它抽成纯函数（Core/DeviceRecoveryPolicy.cs）在这里穷举。
//
// ⚠ 历史教训（别再改回保守策略）：
//   曾经是"流停了才重建、首选插回才夺回"。加轮询兜底后，轮询只报"设备集合变了"，
//   而保守策略会判定"设备没变（首选还在）→ 不重建"，于是已经死掉的流永远不被重建，
//   用户依然要手动重选设备。现在的规则是：**设备集合真的变了就重建**——
//   这与用户手动"选一个别的再选回来"是同一件事，因此必定有效。
Section("15. 设备变化：恢复决策（集合变了必须重建，没变不能乱重建）");

{
    // 场景 1：设备集合变了（拔掉或插回）→ 无论流状态如何都必须重建。
    //         这是最关键的一条：用户手动重选设备之所以有效，就是因为重建了流。
    foreach (var running in new[] { false, true })
    foreach (var fallback in new[] { false, true })
    foreach (var present in new[] { false, true })
    {
        var a = DeviceRecoveryPolicy.Decide(
            deviceSetChanged: true, streamRunning: running,
            onFallbackInput: fallback, preferredInputPresent: present);
        Check(a == DeviceRecoveryAction.RestartStream,
            $"设备集合已变化（流在跑={running}，降级中={fallback}，首选可用={present}）→ 重建流" +
            $"（实际 {a}，期望 RestartStream）");
    }

    // 场景 2：集合没变、流在跑、也没在降级 → 绝不能动音频流。
    //         否则插拔一次会连发多条通知/轮询抖动，被反复重建（用户能听到爆音）。
    var action = DeviceRecoveryPolicy.Decide(
        deviceSetChanged: false, streamRunning: true, onFallbackInput: false, preferredInputPresent: true);
    Check(action == DeviceRecoveryAction.None,
        $"集合没变、正常运行 → 不重建（实际 {action}，期望 None）");

    // 场景 3：集合没变、流在跑、但正跑在降级设备上而首选已可用 → 夺回首选（也走重建）
    action = DeviceRecoveryPolicy.Decide(
        deviceSetChanged: false, streamRunning: true, onFallbackInput: true, preferredInputPresent: true);
    Check(action == DeviceRecoveryAction.RestartStream,
        $"降级中且首选已可用 → 重建以夺回首选（实际 {action}，期望 RestartStream）");

    // 场景 4：集合没变、流停了、首选可用 → 重建
    action = DeviceRecoveryPolicy.Decide(
        deviceSetChanged: false, streamRunning: false, onFallbackInput: false, preferredInputPresent: true);
    Check(action == DeviceRecoveryAction.RestartStream,
        $"流已停且首选可用 → 重建流（实际 {action}，期望 RestartStream）");

    // 场景 5：集合没变、流停了、但连首选都没有 → 不重建。
    //         重启必然失败，只会把日志刷满；等设备插回来（那时集合会变）再建。
    action = DeviceRecoveryPolicy.Decide(
        deviceSetChanged: false, streamRunning: false, onFallbackInput: true, preferredInputPresent: false);
    Check(action == DeviceRecoveryAction.None,
        $"无任何可用设备且集合未变 → 不空转重启（实际 {action}，期望 None）");

    // 场景 6：穷举全部 16 种组合，核对"重建 / 不动"的分布
    var combos = new List<(bool Changed, bool Running, bool Fallback, bool Present)>();
    foreach (var changed in new[] { false, true })
    foreach (var running in new[] { false, true })
    foreach (var fallback in new[] { false, true })
    foreach (var present in new[] { false, true })
        combos.Add((changed, running, fallback, present));

    var restarts = combos.Count(c => DeviceRecoveryPolicy.Decide(c.Changed, c.Running, c.Fallback, c.Present)
                                     == DeviceRecoveryAction.RestartStream);
    var none = combos.Count(c => DeviceRecoveryPolicy.Decide(c.Changed, c.Running, c.Fallback, c.Present)
                                 == DeviceRecoveryAction.None);
    Check(restarts == 11 && none == 5,
        $"16 种组合的判定分布：重建 {restarts} / 不动 {none}（期望 11 / 5）");

    // 场景 7：**集合没变时，绝大多数组合必须是不动**——只有 5 种例外（流停了或降级中且首选可用）。
    //         这条守的是"别把正常运行的音频流反复重建"。
    var changedQuiet = combos.Count(c => !c.Changed &&
        DeviceRecoveryPolicy.Decide(c.Changed, c.Running, c.Fallback, c.Present) == DeviceRecoveryAction.None);
    Check(changedQuiet == 5,
        $"集合没变时保持不动的组合数 = {changedQuiet}（期望 5）");

    // 场景 8：集合变了就一定重建（不存在"变了却不动"的组合）
    var changedIgnored = combos.Count(c => c.Changed &&
        DeviceRecoveryPolicy.Decide(c.Changed, c.Running, c.Fallback, c.Present) == DeviceRecoveryAction.None);
    Check(changedIgnored == 0,
        $"集合变化被忽略的组合数 = {changedIgnored}（期望 0；否则热插拔又会失效）");
}

Console.WriteLine();
Console.WriteLine(failures == 0
    ? "全部通过：DenoiseEffect / LoudnessBalanceEffect / ToneStyleEffect / CreativeEffect / " +
      "SpectrumAnalyzer / MicMixer / DeviceRecoveryPolicy 的回归项均符合预期。"
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
{    private int _frames;

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

/// <summary>恒定电平的采样源（麦克风/播放器测试替身）。</summary>
internal sealed class ConstantProvider : ISampleProvider
{
    private readonly float _value;

    public ConstantProvider(WaveFormat waveFormat, float value)
    {
        WaveFormat = waveFormat;
        _value = value;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> buffer)
    {
        buffer.Fill(_value);
        return buffer.Length;
    }
}

/// <summary>把监听收到的样本记下来的测试替身。</summary>
internal sealed class CapturingSink : MicMixer.IMonitorSink
{
    public List<float> Samples { get; } = new();

    public void Write(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples) Samples.Add(sample);
    }
}

