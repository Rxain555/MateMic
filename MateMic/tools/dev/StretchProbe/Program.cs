using Signalsmith;

// SignalsmithStretch-CS spike（第七步）：块长 → 延迟 / 音质 / CPU。
//
// 音质判据（可测的代理指标）：给一个纯正弦、做 +6 半音变调，
// 理想输出仍是纯正弦。统计**落在预期频率之外的能量占比**（杂散/失真），越低越干净。
// 用矩形窗 DFT 直接算，各块长口径一致，可横向比较。

const int fs = 48000;
const int block = 480;
const double inputHz = 220;
const double transpose = 6;
var expectedHz = inputHz * Math.Pow(2, transpose / 12.0);

Console.WriteLine($"  输入 {inputHz:0} Hz 正弦，变调 +{transpose:0} 半音 → 预期 {expectedHz:0.0} Hz");
Console.WriteLine("  块长      延迟     杂散能量占比   RTF");

foreach (var configBlock in new[] { 960, 1920, 2880, 3840, 5760 })
{
    using var stretch = new Stretch();
    stretch.PresetDefault(1, fs, false);
    stretch.Configure(1, configBlock, configBlock / 4, false);
    stretch.SetTransposeSemitones((float)transpose, 0f);

    var latencyMs = (stretch.InputLatency() + stretch.OutputLatency()) * 1000.0 / fs;

    // 用谐波堆（10 个谐波）而不是纯正弦：更接近人声，能暴露短窗造成的谐波间涂抹
    var input = new float[fs * 2];
    for (var i = 0; i < input.Length; i++)
    {
        double v = 0;
        for (var h = 1; h <= 10; h++) v += Math.Sin(2 * Math.PI * inputHz * h * i / fs) / h;
        input[i] = (float)(0.25 * v);
    }

    var output = new float[input.Length];
    for (var offset = 0; offset + block <= input.Length; offset += block)
        stretch.Process(input.AsSpan(offset, block), output.AsSpan(offset, block));

    var slice = output.AsSpan(fs, fs - 2000).ToArray();
    var spurious = SpuriousRatio(slice, fs, inputHz * Math.Pow(2, transpose / 12.0), 15.0);

    // CPU：处理 5 秒
    var longInput = new float[fs * 5];
    for (var i = 0; i < longInput.Length; i++)
        longInput[i] = (float)(0.3 * Math.Sin(2 * Math.PI * inputHz * i / fs));
    var longOutput = new float[longInput.Length];
    var watch = System.Diagnostics.Stopwatch.StartNew();
    for (var offset = 0; offset + block <= longInput.Length; offset += block)
        stretch.Process(longInput.AsSpan(offset, block), longOutput.AsSpan(offset, block));
    watch.Stop();

    Console.WriteLine($"  {configBlock,5}   {latencyMs,6:0.0} ms   {spurious * 100,10:0.0}%   {watch.Elapsed.TotalSeconds / 5.0:0.000}");
}

// 预期频率 ±bandHz 之外的能量占总能量的比例
static double SpuriousRatio(float[] samples, int sampleRate, double expectedHz, double bandHz)
{
    // 谐波堆：只要靠近"某个预期谐波位置"就算在带内
    var expectedFundamental = expectedHz;
    var total = 0.0;
    var inBand = 0.0;
    for (var hz = 80.0; hz <= 3000.0; hz += 1.0)
    {
        double re = 0, im = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var phase = 2 * Math.PI * hz * i / sampleRate;
            re += samples[i] * Math.Cos(phase);
            im += samples[i] * Math.Sin(phase);
        }

        var power = re * re + im * im;
        total += power;
        var nearest = Math.Round(hz / expectedFundamental) * expectedFundamental;
        if (Math.Abs(hz - nearest) <= bandHz) inBand += power;
    }

    return total <= 0 ? 1 : 1.0 - inBand / total;
}
