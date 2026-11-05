using System.Diagnostics;
using System.Text.Json;

namespace MateMic.Ai.AiVoiceIpc;

/// <summary>主进程下发给宿主的参数（序列化成 JSON 走控制块）。</summary>
public sealed class AiVoiceHostParams
{
    public required string Cv { get; init; }
    public required string Rmvpe { get; init; }
    public required string Voice { get; init; }
    public string? Index { get; init; }
    public int BlockMs { get; init; } = 160;
    public int ContextMs { get; init; } = 320;
    public int CrossfadeMs { get; init; } = 40;
    public int Semitones { get; init; }
    public float IndexRate { get; init; }
    public bool UseGpu { get; init; } = true;

    public string ToJson() => JsonSerializer.Serialize(this);
}

/// <summary>
/// 主进程侧的**宿主进程客户端**：负责起子进程、下发参数、收发音频、优雅关闭。
///
/// 设计上的几处刻意选择：
///
/// · **命名带 GUID**：每次启动都用新名字，避免上一次异常退出留下的映射/事件被复用。
/// · **输出环由宿主创建**：谁创建谁负责销毁，主进程只是延迟打开（带重试），
///   这样不存在"两边都以为自己是创建者"的竞态。
/// · **关闭三步走**：先置关闭标志并等它自己退（<see cref="StopWaitMs"/>），
///   超时才强杀整棵进程树。正常路径下子进程会自己释放 CUDA 与模型，
///   强杀只是因为"卡死了也得收掉，不能留孤儿占显存"。
/// · **IsAlive 只做观察**：崩溃检测由调用方决定怎么处理（第一期先记录并降级为直通）。
///
/// 这个类**不做水位管理** —— 输入/输出环的积压策略仍由 <c>AiVoiceEffect</c> 负责，
/// 保持与进程内实现完全一致的缓冲深度，这样"跨进程"才不会引入额外延迟。
/// </summary>
public sealed class AiVoiceHostClient : IDisposable
{
    private const int ChainRate = 48000;

    /// <summary>正常关闭时等子进程自己退出的时间。超过就强杀。</summary>
    private const int StopWaitMs = 5000;

    private readonly string _name;
    private readonly AiVoiceControlBlock _ctl;
    private readonly AudioShmRing _inRing;
    private readonly object _gate = new();

    private Process? _host;
    private AudioShmRing? _outRing;
    private bool _disposed;

    /// <summary>本次会话的共享内存前缀（诊断用）。</summary>
    public string Name => _name;

    /// <summary>子进程 PID（未启动时为 0）。</summary>
    public int HostPid => _host?.Id ?? 0;

    /// <summary>子进程是否还活着。</summary>
    public bool IsAlive
    {
        get
        {
            var host = _host;
            if (host == null) return false;
            try { return !host.HasExited; }
            catch { return false; }
        }
    }

    /// <summary>当前状态（Loading / Ready / Failed / Stopping）。</summary>
    public AiVoiceHostState State => _ctl.GetState().State;

    /// <summary>加载进度（0~1，仅 Loading 时有意义）。</summary>
    public double Progress => _ctl.GetState().Progress;

    /// <summary>最近一次失败原因（没有则为空）。</summary>
    public string LastError => _ctl.GetState().Error;

    private AiVoiceHostClient(string name, AiVoiceControlBlock ctl, AudioShmRing inRing)
    {
        _name = name;
        _ctl = ctl;
        _inRing = inRing;
    }

    /// <summary>
    /// 启动宿主并下发参数。<paramref name="error"/> 非空表示启动失败。
    /// 注意：返回 true 只代表"进程起来了、参数发出去了"，**不代表模型已加载完**
    ///（那要等 <see cref="WaitReady"/>）。
    /// </summary>
    public static AiVoiceHostClient? Start(AiVoiceHostParams parameters, int inputCapacitySamples, out string error)
    {
        error = string.Empty;
        var name = "MateMicAI-" + Guid.NewGuid().ToString("N")[..12];

        AiVoiceControlBlock? ctl = null;
        AudioShmRing? inRing = null;
        try
        {
            ctl = AiVoiceControlBlock.CreateMaster(name + "-ctl");
            inRing = AudioShmRing.CreateProducer(name + "-in",
                inputCapacitySamples > 0 ? inputCapacitySamples : ChainRate * 2);

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                error = "取不到自身可执行文件路径";
                ctl.Dispose();
                inRing.Dispose();
                return null;
            }

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // 数据目录跟着主进程走：宿主只读模型文件，但日志/配置路径要与主进程一致，
                // 免得它去碰"真正的"用户目录。--aivoice-host 本身会豁免单实例检查。
                WorkingDirectory = AppContext.BaseDirectory,
            };
            psi.ArgumentList.Add("--aivoice-host");
            psi.ArgumentList.Add("--shm-name");
            psi.ArgumentList.Add(name);

            var host = Process.Start(psi);
            if (host == null)
            {
                error = "子进程启动失败";
                ctl.Dispose();
                inRing.Dispose();
                return null;
            }

            // 把宿主输出转发到主进程日志：宿主那边出问题，主进程日志里要能看见
            host.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) Core.Log.Info("[宿主] " + e.Data);
            };
            host.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) Core.Log.Warn("[宿主] " + e.Data);
            };
            host.BeginOutputReadLine();
            host.BeginErrorReadLine();

            var client = new AiVoiceHostClient(name, ctl, inRing);
            client._host = host;   // 同类内可访问私有字段（对象初始化器做不到）
            client._ctl.SetParams(parameters.ToJson());
            return client;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            ctl?.Dispose();
            inRing?.Dispose();
            return null;
        }
    }

    /// <summary>等宿主进入就绪（或失败）。</summary>
    public bool WaitReady(int timeoutMs, out string error)
    {
        var (ok, err) = _ctl.WaitForReady(timeoutMs);
        error = ok ? string.Empty : err;
        return ok;
    }

    /// <summary>
    /// 写输入音频（48 kHz）。返回实际写入的样本数；0 表示环满（调用方决定丢旧还是等）。
    /// </summary>
    public int WriteInput(ReadOnlySpan<float> samples) => _inRing.Write(samples);

    /// <summary>输入环满时等一会儿（宿主读得慢）。返回 true 表示有空间了。</summary>
    public bool WaitForInputRoom(int timeoutMs) => _inRing.WaitForRoom(timeoutMs);

    /// <summary>
    /// 读输出音频（48 kHz）。返回实际读到的样本数；0 表示还没产出。
    ///
    /// 输出环由宿主创建，所以第一次调用时才尝试打开（带重试）；打不开就一直返回 0。
    /// </summary>
    public int ReadOutput(Span<float> samples)
    {
        var ring = _outRing;
        if (ring == null)
        {
            lock (_gate)
            {
                ring = _outRing;
                if (ring == null)
                {
                    ring = AudioShmRing.OpenConsumer(_name + "-out", timeoutMs: 1000);
                    if (ring == null) return 0;
                    _outRing = ring;
                }
            }
        }

        return ring.Read(samples);
    }

    /// <summary>输入环当前的可用样本数（诊断用）。</summary>
    public long InputAvailable => _inRing.AvailableSamples;

    /// <summary>
    /// 优雅关闭：置关闭标志 → 等它自己退（最多 <see cref="StopWaitMs"/>）→ 超时强杀整棵树。
    /// 返回 true 表示是"自己退出的"（正常路径，CUDA/模型都由它自己释放）。
    /// </summary>
    public bool Stop()
    {
        var host = _host;
        if (host == null) return true;

        try { _ctl.RequestClose(); } catch { }

        bool exited;
        try { exited = host.WaitForExit(StopWaitMs); }
        catch { exited = false; }

        if (!exited)
        {
            try
            {
                Core.Log.Warn($"[AI 变声] 宿主进程 {host.Id} 未在 {StopWaitMs} ms 内退出，强制结束。");
                host.Kill(entireProcessTree: true);
                host.WaitForExit(2000);
            }
            catch (Exception ex)
            {
                Core.Log.Warn("[AI 变声] 强制结束宿主失败：" + ex.Message);
            }
        }

        return exited;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { Stop(); } catch { }

        var host = _host;
        _host = null;
        try { host?.Dispose(); } catch { }

        lock (_gate)
        {
            try { _outRing?.Dispose(); } catch { }
            _outRing = null;
        }

        try { _inRing.Dispose(); } catch { }
        try { _ctl.Dispose(); } catch { }
    }
}
