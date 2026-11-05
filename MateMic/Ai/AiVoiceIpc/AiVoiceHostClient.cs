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
/// 主进程侧的**宿主进程客户端**：起子进程、下发参数、收发音频、优雅关闭。
///
/// 音频走 <see cref="AudioPipeLink"/>（命名管道、全同步阻塞）。
/// 之所以不用共享内存：实测那套跨进程后波形与进程内不一致（相关系数 0.87~0.96），
/// 而管道在 <c>--pipecheck</c> 里是 **46080 样本 0 不一致**。
///
/// ⚠ 收发都是**阻塞**的。调用方必须用**两个线程**分别负责写和读
/// （见 <c>AiVoiceEffect.HostWorkerLoop</c>）—— 在一条线程上轮流做会死锁：
/// 管道写满时你在写，没人读；管道空时你在读，没人写。
///
/// 关闭三步走：置控制块关闭标志 → 等它自己退（5 秒）→ 超时才强杀整棵进程树。
/// 正常路径下子进程会自己释放 CUDA 与模型，强杀只是因为"卡死了也得收掉，不能留孤儿占显存"。
/// </summary>
public sealed class AiVoiceHostClient : IDisposable
{
    private const int ChainRate = 48000;

    /// <summary>正常关闭时等子进程自己退出的时间。超过就强杀。</summary>
    private const int StopWaitMs = 5000;

    /// <summary>等子进程连上管道的时长。</summary>
    private const int ConnectWaitMs = 20_000;

    private readonly string _name;
    private readonly AiVoiceControlBlock _ctl;
    private readonly AudioPipeLink _pipeIn;
    private readonly AudioPipeLink _pipeOut;
    private readonly object _gate = new();

    private Process? _host;
    private bool _disposed;

    /// <summary>本次会话的共享内存/管道前缀（诊断用）。</summary>
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

    /// <summary>宿主报告的最近一块推理耗时（毫秒）。用于界面显示"负载"。</summary>
    public double HostInferMs => _ctl.GetHostInferMs();

    private AiVoiceHostClient(string name, AiVoiceControlBlock ctl, AudioPipeLink pipeIn, AudioPipeLink pipeOut)
    {
        _name = name;
        _ctl = ctl;
        _pipeIn = pipeIn;
        _pipeOut = pipeOut;
    }

    /// <summary>
    /// 启动宿主、等它连上管道、下发参数。
    /// 返回 true 只代表"进程起来了、管道通了、参数发了"，**不代表模型已加载完**（那要等 <see cref="WaitReady"/>）。
    /// </summary>
    public static AiVoiceHostClient? Start(AiVoiceHostParams parameters, out string error)
    {
        error = string.Empty;
        var name = "MateMicAI-" + Guid.NewGuid().ToString("N")[..12];

        AiVoiceControlBlock? ctl = null;
        AudioPipeLink? pipeIn = null;
        AudioPipeLink? pipeOut = null;
        Process? host = null;

        try
        {
            ctl = AiVoiceControlBlock.CreateMaster(name + "-ctl");

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                error = "取不到自身可执行文件路径";
                ctl.Dispose();
                return null;
            }

            // ⚠ 顺序：**先起子进程，再建 server**（CreateServer 会阻塞等对端连接）
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            psi.ArgumentList.Add("--aivoice-host");
            psi.ArgumentList.Add("--shm-name");
            psi.ArgumentList.Add(name);

            host = Process.Start(psi);
            if (host == null)
            {
                error = "子进程启动失败";
                ctl.Dispose();
                return null;
            }

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

            // 主→宿主：我们写
            pipeIn = AudioPipeLink.CreateServer(name + "-in", isWriter: true, connectTimeoutMs: ConnectWaitMs);
            if (pipeIn == null) { error = "输入管道未连通"; ctl.RequestClose(); host.Kill(true); ctl.Dispose(); return null; }

            // 宿主→主：我们读
            pipeOut = AudioPipeLink.CreateServer(name + "-out", isWriter: false, connectTimeoutMs: ConnectWaitMs);
            if (pipeOut == null) { error = "输出管道未连通"; pipeIn.Dispose(); ctl.RequestClose(); host.Kill(true); ctl.Dispose(); return null; }

            var client = new AiVoiceHostClient(name, ctl, pipeIn, pipeOut);
            client._host = host;
            client._ctl.SetParams(parameters.ToJson());
            return client;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try { host?.Kill(true); } catch { }
            pipeOut?.Dispose();
            pipeIn?.Dispose();
            ctl?.Dispose();
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
    /// 写输入音频（48 kHz）。**阻塞**直到写完或对端关闭。
    /// 只允许在**喂线程**上调用。返回 false 表示对端已关闭。
    /// </summary>
    public bool WriteInput(ReadOnlySpan<float> samples) => _pipeIn.Write(samples);

    /// <summary>
    /// 读输出音频（48 kHz）。**阻塞**直到读到一帧或对端关闭。
    /// 只允许在**取线程**上调用。返回实读样本数；0 = 对端关闭。
    /// </summary>
    public int ReadOutput(Span<float> samples) => _pipeOut.Read(samples);

    /// <summary>
    /// 下发**热参数**（变调、索引占比）。只影响每块计算，不重建引擎。
    /// </summary>
    public void SetHotParams(int semitones, float indexRate)
        => _ctl.SetHotParams(semitones, Math.Clamp(indexRate, 0f, 1f));

    /// <summary>优雅关闭：置关闭标志 → 关管道（打断阻塞读写）→ 等它自己退 → 超时才强杀。</summary>
    public bool Stop()
    {
        var host = _host;
        if (host == null) return true;

        try { _ctl.RequestClose(); } catch { }

        // 先关管道：喂/取线程可能正阻塞在 Read/Write 上，关掉才能让它们返回
        try { _pipeIn.Dispose(); } catch { }
        try { _pipeOut.Dispose(); } catch { }

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
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try { Stop(); } catch { }

        var host = _host;
        _host = null;
        try { host?.Dispose(); } catch { }
        try { _ctl.Dispose(); } catch { }
    }
}
