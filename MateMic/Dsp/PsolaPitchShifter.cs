using System;

namespace MateMic.Dsp;

/// <summary>
/// 时域 PSOLA 变调器（**音高与共振峰可独立控制**）。
///
/// 为什么需要它：<see cref="PitchShifter"/> 用的是 NAudio 的 SmbPitchShifter，
/// 那是**频谱搬移**——整个频谱一起移动，共振峰跟着音高跑，听感就是"花栗鼠/怪兽"，
/// 只是"变了个音"而不是"换了一把嗓子"（2026-10-03 用户实测反馈）。
///
/// 本类的做法（时域、逐周期重叠相加）：
///   · 从输入里按当前基频周期 P 取一段 2P 长的汉宁窗片段；
///   · 把这段**原样**（或按共振峰因子重采样后）叠加到输出上；
///   · 叠加间隔 = P / 音高因子 —— 间隔决定音高，片段本身决定音色（共振峰）。
/// 因为每个输出片段都是输入波形的拷贝，**声门脉冲形状（即频谱包络/共振峰）得以保留**，
/// 这就是"像换了个人"与"像加速播放"的区别。
///
/// 两个独立控制量：
///   · <see cref="PitchFactor"/>：音高倍数。叠加间隔随之改变，片段长度不变 → 只动音高。
///   · <see cref="FormantFactor"/>：共振峰倍数。把每个片段按 1/F 重采样（长度变 2P/F），
///     叠加间隔仍由音高决定 → 只动共振峰（F &gt; 1 更"细/年轻"，F &lt; 1 更"厚/低沉"）。
///
/// 实时性：输入环形缓冲 + 输出累加缓冲（含归一化累加，避免汉宁窗叠加处起伏），
/// 每次 <see cref="Process"/> **恰好产出请求的样本数**，不依赖"攒够一整块"，
/// 因此不会出现早期自研方案那种"周期性饿死、约 50% 帧静音"的问题。
/// </summary>
public sealed class PsolaPitchShifter
{
    /// <summary>单周期上限（约 47 Hz@48k）。人声用不到，纯防御。</summary>
    private const int MaxPeriod = 1024;

    /// <summary>无声段/清音段用的兜底周期（毫秒）：此时没有基频可跟，退化成 WSOLA 式的连续复制。</summary>
    private const double UnvoicedPeriodMs = 4.0;

    private readonly int _sampleRate;
    /// <summary>
    /// 周期跟踪器。**不能用给电音做音阶吸附的那个 <see cref="PitchDetector"/>**：
    /// <summary>周期跟踪器（求稳：倍频纠错 + 中值平滑 + 先验范围搜索）。</summary>
    private readonly PitchTracker _tracker;

    /// <summary>输入缓冲：累积待分析的输入样本。</summary>
    private readonly float[] _input = new float[MaxPeriod * 8];

    /// <summary>输出累加缓冲与归一化累加（汉宁窗叠加后要除以窗和，否则电平会起伏）。</summary>
    private readonly float[] _acc = new float[MaxPeriod * 8];
    private readonly float[] _norm = new float[MaxPeriod * 8];

    /// <summary>汉宁窗缓存（按长度需要时重建）。</summary>
    private float[] _window = new float[MaxPeriod * 2];

    private int _inputCount;            // _input 中有效样本数
    private double _time;               // 统一时间轴（输入样本，浮点）：输入与输出按同一速率推进，时长才守恒
    private double _writePos;           // 合成位置（输出样本，浮点）
    private int _emitted;               // 已输出的样本数（_acc 的读取游标）
    private int _detectCountdown;       // 距离下次基频检测还有多少输入样本
    private double _currentPeriod;      // 当前周期估计（样本）

    public PsolaPitchShifter(int sampleRate)
    {
        _sampleRate = sampleRate;
        _tracker = new PitchTracker(sampleRate);
        _currentPeriod = UnvoicedPeriodMs * sampleRate / 1000.0;
        BuildWindow(_window.Length);
    }

    /// <summary>音高倍数（1 = 不变）。由半音换算：2^(semitones/12)。</summary>
    public double PitchFactor { get; set; } = 1.0;

    /// <summary>共振峰倍数（1 = 不变）。&gt;1 更细更"年轻"，&lt;1 更厚更"低沉"。</summary>
    public double FormantFactor { get; set; } = 1.0;

    /// <summary>最近一次检测到的基频（Hz，0 = 未检测到）。诊断用。</summary>
    public float LastDetectedFrequency { get; private set; }

    /// <summary>最近一次合成用的周期（样本）。诊断用。</summary>
    public double LastPeriod => _currentPeriod;

    public void Reset()
    {
        Array.Clear(_input);
        Array.Clear(_acc);
        Array.Clear(_norm);
        _inputCount = 0;
        _time = 0;
        _writePos = 0;
        _emitted = 0;
        _detectCountdown = 0;
        _currentPeriod = UnvoicedPeriodMs * _sampleRate / 1000.0;
        _tracker.Reset();
        LastDetectedFrequency = 0;
    }

    /// <summary>
    /// 就地处理一块音频。内部按"输入进 → 合成够 → 输出同样多"的顺序，
    /// 因此每次调用都恰好填满 buffer（首次调用会有一段约 2 个周期的静音作为启动延迟）。
    /// </summary>
    public void Process(Span<float> buffer)
    {
        if (buffer.Length == 0) return;

        AppendInput(buffer);
        SynthesizeEnough(buffer.Length + MaxPeriod);   // 多合成一点，保证读取时不会读到未写完的区段
        EmitOutput(buffer);
        Compact();
    }

    private void AppendInput(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            if (_inputCount >= _input.Length - 1)
            {
                // 缓冲满：丢掉已经分析过的老数据（正常情况下 Compact 会先腾出空间）
                var drop = Math.Min(_inputCount, MaxPeriod);
                Array.Copy(_input, drop, _input, 0, _inputCount - drop);
                _inputCount -= drop;
                _time = Math.Max(0, _time - drop);
            }

            _input[_inputCount++] = sample;
        }
    }

    /// <summary>合成，直到输出累加缓冲里"已写完"的样本数达到目标。</summary>
    ///
    /// ⚠ 关键：窗口必须**居中在离当前时间最近的声门脉冲**上，而不是随便按 hop 推进。
    /// 第一版就是按 hop 直接推进的，结果脉冲在每个窗口里的相对位置逐窗漂移，
    /// 重叠相加互相抵消 —— 实测输出是一团噪声（音高测量值为无意义的 1455 Hz）。
    /// 对齐到脉冲后：输出叠加间隔 = 输出音高，窗口内容又始终对着真实脉冲，
    /// 既能改变音高，又不会互相抵消。
    private void SynthesizeEnough(int targetReady)
    {
        var guard = 0;
        while (_writePos - _emitted < targetReady && guard++ < 2048)
        {
            var period = UpdatePeriod();
            var ratio = Math.Max(0.25, Math.Min(4.0, PitchFactor));
            var hop = period / ratio;                       // 输出叠加间隔 → 决定音高

            // 把当前时间吸附到"脉冲栅格"上：即离 _time 最近的那个脉冲位置
            var center = (int)Math.Round(Math.Round(_time / period) * period);
            var half = (int)Math.Round(period);
            if (center + half >= _inputCount) break;        // 前瞻不够，等下一块输入

            AddWindow(center, half, hop);
            _time += hop;                                   // 输入与输出等速推进 → 时长守恒
            _writePos += hop;
        }
    }

    /// <summary>把以 <paramref name="center"/> 为中心、半长 <paramref name="half"/> 的输入片段叠加到输出。</summary>
    private void AddWindow(int center, int half, double hop)
    {
        // 共振峰因子：把片段按 1/F 重采样（读得更慢/更快），于是频谱包络缩放而音高不受影响
        var formant = Math.Max(0.5, Math.Min(2.0, FormantFactor));
        var halfOut = (int)Math.Round(half / formant);
        var length = halfOut * 2;
        if (length < 8) return;
        if (_window.Length != length) BuildWindow(length);

        var start = (int)Math.Round(_writePos) - halfOut;
        for (var i = 0; i < length; i++)
        {
            var target = start + i;
            if (target < 0) continue;                     // 启动期的负索引：丢弃（相当于引入延迟）
            if (target >= _acc.Length) break;

            // 源位置：以 center 为中心，按 formant 缩放取样
            var source = center + (int)Math.Round((i - halfOut) * formant);
            if (source < 0 || source >= _inputCount) continue;

            var value = _input[source] * _window[i];
            _acc[target] += value;
            _norm[target] += _window[i];
        }
    }

    /// <summary>把累加缓冲里已合成的样本取出、归一化后写进输出。</summary>
    private void EmitOutput(Span<float> buffer)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            var index = _emitted + i;
            if (index < 0 || index >= _acc.Length)
            {
                buffer[i] = 0f;
                continue;
            }

            var norm = _norm[index];
            buffer[i] = norm > 1e-4f ? _acc[index] / norm : 0f;
        }

        _emitted += buffer.Length;
    }

    /// <summary>
    /// 周期性重估基频。用 <see cref="PitchTracker"/>（求稳：倍频纠错 + 中值平滑 + 先验范围搜索），
    /// 并且**分析最新到达的音频**（不能围着 _time 取窗 —— 那是启动延迟之前的位置，
    /// 围着它取窗会让前瞻条件几乎永不满足，检测几乎从不执行，周期一直停在兜底值）。
    /// 清音/静音时**沿用上一个周期**而不是跳回兜底值：栅格突然跳变会带来咔哒声，
    /// 而用上一个周期继续做重叠相加会退化成"逐字复制"，听感是连续的。
    /// </summary>
    private double UpdatePeriod()
    {
        _detectCountdown -= (int)Math.Max(1, _currentPeriod / 4);
        if (_detectCountdown <= 0)
        {
            _detectCountdown = 512;   // 约每 10 ms 估计一次：够跟上颤音，也不吃 CPU

            if (_inputCount >= PitchTracker.WindowSize
                && _tracker.Feed(_input.AsSpan(_inputCount - PitchTracker.WindowSize, PitchTracker.WindowSize)))
            {
                _currentPeriod = Math.Clamp(_tracker.Period, 16, MaxPeriod);
                LastDetectedFrequency = (float)(_sampleRate / _currentPeriod);
            }
            else if (!_tracker.HasPeriod)
            {
                // 还没得到任何可靠周期（启动 / 纯清音）：先用兜底周期跑起来
                _currentPeriod = UnvoicedPeriodMs * _sampleRate / 1000.0;
            }
        }

        return _currentPeriod;
    }

    /// <summary>丢弃已经输出/分析完的前缀，避免缓冲无限增长。</summary>
    private void Compact()
    {
        var keepFrom = Math.Max(0, (int)Math.Round(_time) - MaxPeriod);
        if (keepFrom >= MaxPeriod)
        {
            Array.Copy(_input, keepFrom, _input, 0, _inputCount - keepFrom);
            _inputCount -= keepFrom;
            _time -= keepFrom;
        }

        var dropOutput = Math.Min(_emitted, _acc.Length / 2);
        if (dropOutput > 0)
        {
            Array.Copy(_acc, dropOutput, _acc, 0, _acc.Length - dropOutput);
            Array.Copy(_norm, dropOutput, _norm, 0, _norm.Length - dropOutput);
            Array.Clear(_acc, _acc.Length - dropOutput, dropOutput);
            Array.Clear(_norm, _norm.Length - dropOutput, dropOutput);
            _emitted -= dropOutput;
            _writePos -= dropOutput;
        }
    }

    private void BuildWindow(int length)
    {
        _window = new float[length];
        for (var i = 0; i < length; i++)
            _window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / length));
    }
}
