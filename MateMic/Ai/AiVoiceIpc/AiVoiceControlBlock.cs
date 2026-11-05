using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;

namespace MateMic.Ai.AiVoiceIpc;

/// <summary>宿主进程的状态。</summary>
public enum AiVoiceHostState
{
    /// <summary>正在加载模型 / 建立 CUDA context。</summary>
    Loading = 0,

    /// <summary>已就绪，可以处理音频。</summary>
    Ready = 1,

    /// <summary>加载或运行失败（错误信息在控制块里）。</summary>
    Failed = 2,

    /// <summary>正在退出。</summary>
    Stopping = 3,
}

/// <summary>
/// AI 变声独立进程的**控制块**：主进程 ↔ 宿主进程之间交换"参数"与"状态"。
///
/// 为什么不走命令行/管道：参数里有音色路径、索引路径这类可能带空格和中文的字符串，
/// 而且状态要能**实时**看到（加载进度、错误消息）。共享内存 + 命名事件最直接。
///
/// ## 布局（两个区，方向相反，所以不会互相抢写）
///
/// <code>
/// [参数区]（主进程写 → 宿主读）
///   偏移 0     int     版本号（每次参数变更 +1；宿主发现变了就重建引擎）
///   偏移 4     int     JSON 字节数
///   偏移 64    UTF-8 JSON（上限 MaxJsonBytes）
///
/// [状态区]（宿主写 → 主进程读）
///   偏移 2112  int     状态（AiVoiceHostState）
///   偏移 2116  int     错误信息字节数
///   偏移 2120  double  加载进度 0..1（仅 Loading 时有意义）
///   偏移 2176  UTF-8 错误信息（上限 MaxErrorBytes）
///
/// [公共]
///   偏移 3200  int     关闭标志（任一方置 1，另一方退出）
/// </code>
///
/// 版本号用"单写者"语义：只有主进程改参数区、只有宿主改状态区，因此不需要 CAS。
/// </summary>
public sealed class AiVoiceControlBlock : IDisposable
{
    /// <summary>参数 JSON 的最大字节数。够放音色/索引路径与全部可调参数。</summary>
    public const int MaxJsonBytes = 3072;

    /// <summary>错误信息的最大字节数（超出截断）。</summary>
    public const int MaxErrorBytes = 1536;

    private const int OffsetParamVersion = 0;
    private const int OffsetParamLength = 4;
    private const int OffsetParams = 64;

    private const int OffsetState = 4160;
    private const int OffsetErrorLength = 4164;
    private const int OffsetProgress = 4168;
    private const int OffsetError = 4200;

    private const int OffsetClosed = 6200;
    private const int TotalBytes = 8192;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly bool _ownsFile;

    /// <summary>参数变更通知（主进程 Set → 宿主 Wait）。</summary>
    public EventWaitHandle ParamsChanged { get; }

    /// <summary>状态变更通知（宿主 Set → 主进程 Wait）。</summary>
    public EventWaitHandle StateChanged { get; }

    /// <summary>本端是宿主侧还是主控侧。</summary>
    public bool IsHostSide { get; }

    private AiVoiceControlBlock(MemoryMappedFile file, MemoryMappedViewAccessor view,
        string name, bool isHostSide, bool ownsFile)
    {
        _file = file;
        _view = view;
        IsHostSide = isHostSide;
        _ownsFile = ownsFile;
        ParamsChanged = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-params");
        StateChanged = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-state");
    }

    /// <summary>主控侧（主进程）：创建映射并初始化为"加载中"。</summary>
    public static AiVoiceControlBlock CreateMaster(string name)
    {
        var file = MemoryMappedFile.CreateNew(name, TotalBytes);
        var view = file.CreateViewAccessor(0, TotalBytes);

        view.Write(OffsetParamVersion, 0);
        view.Write(OffsetParamLength, 0);
        view.Write(OffsetState, (int)AiVoiceHostState.Loading);
        view.Write(OffsetErrorLength, 0);
        view.Write(OffsetProgress, 0.0);
        view.Write(OffsetClosed, 0);

        return new AiVoiceControlBlock(file, view, name, isHostSide: false, ownsFile: true);
    }

    /// <summary>宿主侧：打开主控已建好的映射（带重试，进程刚起来时主控可能还没建）。</summary>
    public static AiVoiceControlBlock? OpenHost(string name, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                // 必须用 OpenExisting 探测存在性，再用 CreateOrOpen 拿**长期持有**的句柄 ——
                // view 要建在这个长命句柄上，否则它一释放 view 就悬空。
                using (MemoryMappedFile.OpenExisting(name))
                {
                }

                var file = MemoryMappedFile.CreateOrOpen(name, TotalBytes);
                var view = file.CreateViewAccessor(0, TotalBytes);
                return new AiVoiceControlBlock(file, view, name, isHostSide: true, ownsFile: false);
            }
            catch (FileNotFoundException)
            {
                Thread.Sleep(20);
            }
        }

        return null;
    }

    /// <summary>主控侧：写入参数 JSON 并把版本号 +1。返回新的版本号。</summary>
    public int SetParams(string json)
    {
        if (IsHostSide) throw new InvalidOperationException("参数只能由主控侧写。");

        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaxJsonBytes)
            throw new ArgumentException($"参数 JSON 超过 {MaxJsonBytes} 字节（{bytes.Length}）。");

        _view.WriteArray(OffsetParams, bytes, 0, bytes.Length);
        _view.Write(OffsetParamLength, bytes.Length);

        _view.Read(OffsetParamVersion, out int version);
        version++;
        _view.Write(OffsetParamVersion, version);

        ParamsChanged.Set();
        return version;
    }

    /// <summary>宿主侧：读当前参数（版本不变时调用方无需重复解析）。</summary>
    public (int Version, string Json) GetParams()
    {
        _view.Read(OffsetParamVersion, out int version);
        _view.Read(OffsetParamLength, out int length);
        if (length <= 0 || length > MaxJsonBytes) return (version, string.Empty);

        var buffer = new byte[length];
        _view.ReadArray(OffsetParams, buffer, 0, length);
        return (version, Encoding.UTF8.GetString(buffer));
    }

    /// <summary>宿主侧：报告状态（加载中 / 就绪 / 失败）。</summary>
    public void SetState(AiVoiceHostState state, double progress = 0, string? error = null)
    {
        if (!IsHostSide) throw new InvalidOperationException("状态只能由宿主侧写。");

        _view.Write(OffsetState, (int)state);
        _view.Write(OffsetProgress, progress);

        if (!string.IsNullOrEmpty(error))
        {
            var bytes = Encoding.UTF8.GetBytes(error);
            var take = Math.Min(bytes.Length, MaxErrorBytes);
            _view.WriteArray(OffsetError, bytes, 0, take);
            _view.Write(OffsetErrorLength, take);
        }
        else
        {
            _view.Write(OffsetErrorLength, 0);
        }

        StateChanged.Set();
    }

    /// <summary>主控侧：读状态与错误信息。</summary>
    public (AiVoiceHostState State, double Progress, string Error) GetState()
    {
        _view.Read(OffsetState, out int state);
        _view.Read(OffsetProgress, out double progress);
        _view.Read(OffsetErrorLength, out int errorLength);

        var error = string.Empty;
        if (errorLength > 0 && errorLength <= MaxErrorBytes)
        {
            var buffer = new byte[errorLength];
            _view.ReadArray(OffsetError, buffer, 0, errorLength);
            error = Encoding.UTF8.GetString(buffer);
        }

        var value = Enum.IsDefined(typeof(AiVoiceHostState), state)
            ? (AiVoiceHostState)state
            : AiVoiceHostState.Failed;
        return (value, progress, error);
    }

    /// <summary>主控侧：等宿主进入"就绪"或"失败"，最多等 <paramref name="timeoutMs"/>。</summary>
    public (bool Ok, string Error) WaitForReady(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var (state, _, error) = GetState();
            if (state == AiVoiceHostState.Ready) return (true, string.Empty);
            if (state == AiVoiceHostState.Failed) return (false, error.Length > 0 ? error : "宿主进程加载失败");

            StateChanged.WaitOne(50);
        }

        return (false, $"宿主进程在 {timeoutMs} ms 内未就绪");
    }

    /// <summary>任一方请求关闭。</summary>
    public bool IsClosed
    {
        get
        {
            _view.Read(OffsetClosed, out int flag);
            return flag != 0;
        }
    }

    /// <summary>请求关闭并唤醒对端，避免它一直等在事件上。</summary>
    public void RequestClose()
    {
        try
        {
            _view.Write(OffsetClosed, 1);
            ParamsChanged.Set();
            StateChanged.Set();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        try { ParamsChanged.Dispose(); } catch { }
        try { StateChanged.Dispose(); } catch { }
        try { _view.Dispose(); } catch { }
        if (_ownsFile)
        {
            try { _file.Dispose(); } catch { }
        }
    }
}
