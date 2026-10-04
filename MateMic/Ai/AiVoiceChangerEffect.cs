using NAudio.Wave;
using MateMic.Core;
using MateMic.Dsp;

namespace MateMic.Ai;

/// <summary>
/// AI 变声（<see cref="IAudioEffect"/>）：在 DSP 链里的位置与 DSP 变声完全相同。
///
/// 为什么要后台线程：一次推理要几百毫秒（编码器 + 基频 + 音色模型），
/// 绝不能在音频回调里做，否则采集回调被阻塞、输入直接丢数据。
/// 所以这里用两个环形缓冲解耦：
///   · 音频线程：把输入塞进输入环、从输出环取结果（引擎没就绪时**直通**，不会突然静音）；
///   · 推理线程：等够一个hop就跑一遍流水线，结果交叉淡入写入输出环。
///
/// 长度补偿：模型输出比输入略短（感受野与帧数取整），这里**实测比例**动态调整每次
/// 吃进的输入样本数，保证输出严格按实时速度供给；靠"补静音"会让输出越跑越空。
/// </summary>
public sealed class AiVoiceChangerEffect : IAudioEffect, IDisposable
{
    private const int InputRate = 48000;

    /// <summary>每次产出的输出样本数（0.30 秒）。越小延迟越低，但推理调用越频繁。</summary>
    private const int HopSamples = InputRate * 30 / 100;

    /// <summary>交叉淡入淡出的长度（0.08 秒），用于抹掉块边界的相位跳变。</summary>
    private const int OverlapSamples = InputRate * 8 / 100;

    private readonly object _gate = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _worker;
    private readonly float[] _inputRing = new float[InputRate * 2];
    private readonly float[] _outputRing = new float[InputRate * 2];
    private int _inputHead, _inputTail, _inputCount;
    private int _outputHead, _outputTail, _outputCount;
    private float[] _window = Array.Empty<float>();
    private float[] _context = Array.Empty<float>();
    private int _contextCount;
    private float[] _held = Array.Empty<float>();
    private int _heldCount;

    private volatile bool _stopping;
    private volatile AiVoiceEngine? _engine;
    private volatile float _semitones;
    private volatile float _formantShift;

    /// <summary>已处理块数（诊断用）。</summary>
    public int WorkerBlocks;

    /// <summary>已写入输出环的样本数（诊断用）。</summary>
    public long PushedSamples;

    /// <summary>因输入环满而丢弃的样本数（诊断用）。</summary>
    public long DroppedSamples;

    public AiVoiceChangerEffect(WaveFormat format)
    {
        WaveFormat = format;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "MateMic.AiVoice",
            Priority = ThreadPriority.AboveNormal,
        };
        _worker.Start();
    }

    public string Name => "AI 变声";

    public bool Enabled { get; set; }

    public WaveFormat WaveFormat { get; }

    /// <summary>引擎是否已就绪（未就绪时直通）。</summary>
    public bool IsReady => _engine != null;

    /// <summary>当前引擎名（组件名）。</summary>
    public string EngineName => _engine?.Name ?? "（未加载）";

    /// <summary>大致延迟：一个 hop + 交叉淡入长度 + 一次推理。仅供参考。</summary>
    public double LatencyMs => (HopSamples + OverlapSamples) * 1000.0 / InputRate + 120;

    public void LoadEngine(AiVoiceEngine? engine)
    {
        var previous = _engine;
        _engine = engine;
        _contextCount = 0;
        _heldCount = 0;
        previous?.Dispose();
    }

    /// <summary>参数写入点在音频线程（见 <see cref="Read"/>），这里刻意留空。</summary>
    public void UpdateParameters()
    {
    }

    public int Read(Span<float> buffer)
    {
        var engine = _engine;
        if (!Enabled || engine == null) return buffer.Length;   // 未就绪：直通

        PushInput(buffer);
        PullOutput(buffer);
        _signal.Set();
        return buffer.Length;
    }

    // ---------------------------------------------------------------- 音频线程侧

    private void PushInput(ReadOnlySpan<float> buffer)
    {
        lock (_gate)
        {
            foreach (var sample in buffer)
            {
                // 满了就丢最旧的：宁可丢一点旧数据，也不能让延迟无限增长
                if (_inputCount == _inputRing.Length)
                {
                    _inputTail = (_inputTail + 1) % _inputRing.Length;
                    _inputCount--;
                }

                _inputRing[_inputHead] = sample;
                _inputHead = (_inputHead + 1) % _inputRing.Length;
                _inputCount++;
            }
        }
    }

    private void PullOutput(Span<float> buffer)
    {
        lock (_gate)
        {
            for (var i = 0; i < buffer.Length; i++)
            {
                if (_outputCount == 0)
                {
                    buffer[i] = 0f;      // 引擎刚启动：先给静音，等第一块结果
                    continue;
                }

                buffer[i] = _outputRing[_outputTail];
                _outputTail = (_outputTail + 1) % _outputRing.Length;
                _outputCount--;
            }
        }
    }

    // ---------------------------------------------------------------- 推理线程侧

    private void WorkerLoop()
    {
        while (!_stopping)
        {
            try
            {
            var engine = _engine;
            if (engine == null || !Enabled)
            {
                _signal.WaitOne(50);
                continue;
            }

            // 比例夹紧：某一块产出为 0 时比例会退化成天文数字（曾因此要求吃进两亿个样本，
            // 直接把整个进程带崩），所以这里和引擎侧都设了上下限。
            var ratio = Math.Clamp(engine.SamplesPerOutputSample, 0.5, 4.0);
            var takeIn = (int)Math.Round(HopSamples * ratio);
            var overlapIn = (int)Math.Round(OverlapSamples * ratio);

            if (!TakeInput(engine, takeIn, overlapIn))
            {
                _signal.WaitOne(20);
                continue;
            }
            }
            catch (Exception ex)
            {
                // 后台线程里抛异常会把整个进程带崩（表现为开了 AI 变声程序直接退出），必须拦住
                Log.Error("AI：推理线程异常（已跳过这一块）", ex);
                _signal.WaitOne(200);
            }
        }
    }

    /// <summary>输入够一个 hop 就转换一次；不够就返回 false 让调用方继续等。</summary>
    private bool TakeInput(AiVoiceEngine engine, int takeIn, int overlapIn)
    {
        float[] window;
        int total;
        lock (_gate)
        {
            if (_inputCount < takeIn) return false;

            total = _contextCount + takeIn;
            if (_window.Length < total) _window = new float[total + 4096];
            window = _window;

            // 先放上一轮的尾巴（作为上下文），再取新样本
            Array.Copy(_context, 0, window, 0, _contextCount);
            var write = _contextCount;
            for (var i = 0; i < takeIn; i++)
            {
                window[write++] = _inputRing[_inputTail];
                _inputTail = (_inputTail + 1) % _inputRing.Length;
                _inputCount--;
            }

            // 更新上下文：本轮窗口的最后 overlapIn 个样本
            if (_context.Length < overlapIn) _context = new float[overlapIn + 1024];
            var keep = Math.Min(overlapIn, total);
            Array.Copy(window, total - keep, _context, 0, keep);
            _contextCount = keep;
        }

        var semitones = _semitones;
        var output = engine.Convert(window.AsSpan(0, total), semitones, out var produced);
        if (produced <= 0) return true;

        Interlocked.Increment(ref WorkerBlocks);
        Emit(output, produced);
        return true;
    }

    /// <summary>把引擎输出交叉淡入后写入输出环：留一段尾巴，与下一块的开头做交叉。</summary>
    private void Emit(float[] output, int produced)
    {
        lock (_gate)
        {
            // 本块与上一块尾部交叉的长度
            var fade = Math.Min(_heldCount, Math.Min(produced, OverlapSamples));

            for (var i = 0; i < fade; i++)
            {
                var w = (float)(i + 1) / (fade + 1);
                PushOutput(_held[i] * (1 - w) + output[i] * w);
            }

            // 中间部分直接输出；最后 OverlapSamples 个样本留到下一块再交叉
            var bodyEnd = produced - OverlapSamples;
            if (bodyEnd < fade) bodyEnd = fade;
            for (var i = fade; i < bodyEnd; i++) PushOutput(output[i]);

            if (_held.Length < OverlapSamples) _held = new float[OverlapSamples];
            var held = Math.Min(OverlapSamples, produced - bodyEnd);
            if (held > 0) Array.Copy(output, bodyEnd, _held, 0, held);
            _heldCount = Math.Max(0, held);
        }
    }

    private void PushOutput(float sample)
    {
        if (_outputCount == _outputRing.Length)
        {
            _outputTail = (_outputTail + 1) % _outputRing.Length;
            _outputCount--;
        }

        PushedSamples++;
        _outputRing[_outputHead] = sample;
        _outputHead = (_outputHead + 1) % _outputRing.Length;
        _outputCount++;
    }

    /// <summary>更新变调（半音）。AI 模式下的「变调」滑条接这里。</summary>
    public void SetPitchShift(float semitones) => _semitones = Math.Clamp(semitones, -12f, 12f);

    /// <summary>共振峰偏移（当前版本未接入音色模型，先记录备用）。</summary>
    public void SetFormantShift(float semitones) => _formantShift = semitones;

    public void Dispose()
    {
        _stopping = true;
        _signal.Set();
        if (!_worker.Join(2000)) Log.Warn("AI：推理线程未能及时退出");
        _engine?.Dispose();
        _engine = null;
        _signal.Dispose();
    }
}
