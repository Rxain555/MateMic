using Signalsmith;

// SignalsmithStretch-CS spike（第五步）：能否既拿到共振峰、又把延迟压下来。
//
// 已知：PresetDefault 正常（120 ms），PresetCheaper 正常（100 ms），
//       直接 Configure 会得到近乎静音的输出（RMS 0.008）。
// 这里试几种调用顺序，看能否在预设分配之后再改小块长。

const int fs = 48000;
const int block = 480;
const float semitones = 7f;

Check("仅 PresetDefault", s => s.PresetDefault(1, fs, false));
Check("PresetDefault → Configure 2880", s =>
{
    s.PresetDefault(1, fs, false);
    s.Configure(1, 2880, 720, false);
});
Check("PresetDefault → Configure 960", s =>
{
    s.PresetDefault(1, fs, false);
    s.Configure(1, 960, 240, false);
});
Check("PresetCheaper → Configure 960", s =>
{
    s.PresetCheaper(1, fs, false);
    s.Configure(1, 960, 240, false);
});
Check("Configure 960 → PresetDefault", s =>
{
    s.Configure(1, 960, 240, false);
    s.PresetDefault(1, fs, false);
});

void Check(string name, Action<Stretch> configure)
{
    try
    {
        using var stretch = new Stretch();
        configure(stretch);
        stretch.SetTransposeSemitones(semitones, 0f);

        var latencyMs = (stretch.InputLatency() + stretch.OutputLatency()) * 1000.0 / fs;
        var input = Harmonic(220, fs * 2);
        var output = new float[input.Length];
        for (var offset = 0; offset + block <= input.Length; offset += block)
            stretch.Process(input.AsSpan(offset, block), output.AsSpan(offset, block));

        var measured = Measure(output.AsSpan(fs, fs - 2000).ToArray(), fs);
        var expected = 220 * Math.Pow(2, semitones / 12.0);
        var cents = measured > 0 ? 1200 * Math.Log2(measured / expected) : double.NaN;

        double rms = 0;
        for (var i = fs; i < output.Length; i++) rms += output[i] * output[i];
        rms = Math.Sqrt(rms / (output.Length - fs));

        var ok = Math.Abs(cents) < 30 && rms > 0.05 ? "✓ 可用" : "✗ 不可用";
        Console.WriteLine($"  {name,-34} 块 {stretch.BlockSamples(),5} 延迟 {latencyMs,6:0.0} ms"
                          + $"  偏差 {cents,8:0.0} 音分  RMS {rms:0.0000}  {ok}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  {name,-34} 抛异常：{ex.GetType().Name}: {ex.Message}");
    }
}

static float[] Harmonic(double hz, int length)
{
    var samples = new float[length];
    for (var i = 0; i < length; i++)
    {
        double v = 0;
        for (var h = 1; h <= 10; h++) v += Math.Sin(2 * Math.PI * hz * h * i / fs) / h;
        samples[i] = (float)(0.3 * v);
    }

    return samples;
}

static float Measure(float[] samples, int sampleRate)
{
    var size = Math.Min(16384, samples.Length);
    var buffer = new float[size];
    Array.Copy(samples, buffer, size);
    var minLag = sampleRate / 1500;
    var maxLag = sampleRate / 70;
    var correlations = new float[maxLag + 2];
    var peak = 0f;
    for (var lag = minLag; lag <= maxLag; lag++)
    {
        double sum = 0, ea = 0, eb = 0;
        for (var i = 0; i < size - lag; i++)
        {
            sum += buffer[i] * buffer[i + lag];
            ea += buffer[i] * buffer[i];
            eb += buffer[i + lag] * buffer[i + lag];
        }

        var den = Math.Sqrt(ea * eb);
        if (den < 1e-9) continue;
        var c = (float)(sum / den);
        correlations[lag] = c;
        if (c > peak) peak = c;
    }

    var threshold = peak * 0.9f;
    for (var lag = minLag + 1; lag <= maxLag; lag++)
    {
        if (correlations[lag] < threshold) continue;
        if (correlations[lag] >= correlations[lag - 1] && correlations[lag] >= correlations[lag + 1])
            return (float)sampleRate / lag;
    }

    return 0f;
}

// 组合验证：男→女预设实际使用的 (+6 半音变调, +4 半音共振峰) 同时生效时的表现
{
    using var stretch = new Stretch();
    stretch.PresetDefault(1, 48000, false);
    stretch.Configure(1, 960, 240, false);
    // compensatePitch 的语义要实测确认：true 时它会**抵消变调**（实测 +6 变调 + 4 共振峰
    // 只升了约 1.3 个半音），所以两种取值都测一遍，取"变调准确 + 共振峰也动了"的那个。
    foreach (var compensate in new[] { true, false })
    {
        using var s2 = new Stretch();
        s2.PresetDefault(1, 48000, false);
        s2.Configure(1, 960, 240, false);
        // 顺序假设：SetFormantSemitones 可能覆盖先前的变调设置，试试先设共振峰、再设变调。
        s2.SetFormantSemitones(4f, compensate);
        s2.SetTransposeSemitones(6f, 0f);

        var input = PulseThroughResonator(120, 700, 48000);
        var output = new float[input.Length];
        for (var offset = 0; offset + 480 <= input.Length; offset += 480)
            s2.Process(input.AsSpan(offset, 480), output.AsSpan(offset, 480));

        var slice = output.AsSpan(24000, 20480).ToArray();
        var pitch = Measure(slice, 48000);
        var expectedPitch = 120 * Math.Pow(2, 6.0 / 12);
        var expectedFormant = 700 * Math.Pow(2, 4.0 / 12);
        Console.WriteLine($"[组合/共振峰先设] compensatePitch={compensate,-5} +6 变调 +4 共振峰 → "
                          + $"基频 {pitch,6:0.0} Hz（期望 {expectedPitch,6:0.0}）"
                          + $"  包络峰 {EnvelopePeak(slice, 48000),6:0} Hz（期望约 {expectedFormant,6:0}）");
    }
}

static float[] PulseThroughResonator(double hz, double formantHz, int length)
{
    var samples = new float[length];
    const double r = 0.985;
    var w = 2 * Math.PI * formantHz / 48000;
    var a1 = 2 * r * Math.Cos(w);
    var a2 = -r * r;
    double y1 = 0, y2 = 0;
    var period = (int)Math.Round(48000 / hz);
    for (var i = 0; i < length; i++)
    {
        var pulse = i % period == 0 ? 1.0 : 0.0;
        var y = pulse + a1 * y1 + a2 * y2;
        y2 = y1;
        y1 = y;
        samples[i] = (float)(0.05 * y);
    }

    return samples;
}

static float EnvelopePeak(float[] samples, int sampleRate)
{
    const int step = 20;
    var mags = new double[4000 / step + 1];
    for (var hz = 200; hz <= 4000; hz += step)
    {
        double re = 0, im = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var phase = 2 * Math.PI * hz * i / sampleRate;
            re += samples[i] * Math.Cos(phase);
            im += samples[i] * Math.Sin(phase);
        }

        mags[hz / step] = Math.Sqrt(re * re + im * im);
    }

    var best = 0.0;
    var bestHz = 0;
    for (var k = 3; k < mags.Length - 3; k++)
    {
        double sum = 0;
        for (var j = -3; j <= 3; j++) sum += mags[k + j];
        if (sum > best) { best = sum; bestHz = k * step; }
    }

    return bestHz;
}