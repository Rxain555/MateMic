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
