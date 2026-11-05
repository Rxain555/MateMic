using System.IO.Pipes;

namespace MateMic.Ai.AiVoiceIpc;

/// <summary>
/// 命名管道音频传输 —— **替代共享内存**的那条跨进程通路。
///
/// ## 为什么换掉共享内存
///
/// 共享内存版本（<see cref="AudioShmRing"/>）在单进程自检里完全正确，但跨进程实测：
/// 同一段音频送过去再取回来，**长度与 RMS 都对得上、波形相关性却只有 0.87~0.96**。
/// 加内存屏障无效（相关系数在 0.87~0.96 之间随运行波动），说明问题不是简单的写序，
/// 而是这套"自己管指针 + 自己管可见性"的方案本身不够稳。
///
/// 管道由**内核管理**缓冲与同步：没有指针、没有可见性、没有回绕计算，
/// 写进去什么读出来就是什么。代价是每块多一次内核拷贝（实测占用见 SelfCheck）。
///
/// ## 结构
///
/// 两条**单向**管道，方向固定，因此不存在两端同时写同一管道的问题：
/// <code>
///   主进程 ──(name-in)──► 宿主       音频输入
///   主进程 ◄──(name-out)── 宿主       音频输出
/// </code>
///
/// 帧格式：`int 样本数` + `float[样本数]`。用 `WriteThrough` 关掉内核缓冲的"攒包"
/// 行为，让每块立刻可读 —— 实时音频不能等内核攒满一包。
///
/// ## 阻塞语义
///
/// <see cref="Write"/> 是阻塞的（管道满时等对端读走），<see cref="Read"/> 带超时。
/// 这两者**只允许在工作线程上调用**（<c>HostWorkerLoop</c>），
/// 音频线程从不直接碰管道 —— 音频线程只读写自己的环形缓冲。
/// </summary>
public sealed class AudioPipeLink : IDisposable
{
    /// <summary>单块上限（样本）。远大于任何合理块长，用于校验帧头合法性。</summary>
    public const int MaxFrameSamples = 96000;

    private readonly NamedPipeServerStream? _server;
    private readonly NamedPipeClientStream? _client;
    private readonly bool _isWriter;
    private readonly byte[] _scratch = new byte[MaxFrameSamples * sizeof(float) + sizeof(int)];

    /// <summary>管道名（诊断用）。</summary>
    public string Name { get; }

    /// <summary>本端是写端还是读端。</summary>
    public bool IsWriter => _isWriter;

    private AudioPipeLink(string name, NamedPipeServerStream server, NamedPipeClientStream client, bool isWriter)
    {
        Name = name;
        _server = server;
        _client = client;
        _isWriter = isWriter;
    }

    /// <summary>
    /// 主进程侧：创建管道并等宿主连上来（阻塞直到连上或超时）。
    /// </summary>
    /// <param name="name">管道名（含方向后缀）。</param>
    /// <param name="isWriter">true = 主进程写（主→宿主）；false = 主进程读（宿主→主）。</param>
    public static AudioPipeLink? CreateServer(string name, bool isWriter, int connectTimeoutMs)
    {
        try
        {
            var server = new NamedPipeServerStream(
                name,
                isWriter ? PipeDirection.Out : PipeDirection.In,
                1,                                   // 单实例：一对一
                PipeTransmissionMode.Byte,
                // WriteThrough：不攒包，写入立刻可读（实时音频不能等内核攒满一包）。
                // Asynchronous：Read 走异步路径（我们要用 CancellationToken 做超时；
                // 同步句柄上调用 ReadAsync 会抛 "Operation is not valid due to the current state"）。
                PipeOptions.WriteThrough | PipeOptions.Asynchronous,
                inBufferSize: 1 << 16,
                outBufferSize: 1 << 16);

            // ⚠ 必须带超时：WaitForConnection() 无超时会永久阻塞 ——
            // 子进程万一没起来，主进程就卡死在这里（表现为"开 AI 变声后整个界面僵住"）。
            if (!server.WaitForConnectionAsync().Wait(connectTimeoutMs))
            {
                Core.Log.Warn($"[AI 变声] 管道 {name} 在 {connectTimeoutMs} ms 内没有对端连上来");
                try { server.Dispose(); } catch { }
                return null;
            }

            return new AudioPipeLink(name, server, null!, isWriter);
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"[AI 变声] 创建管道 {name} 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>宿主侧：连接主进程已创建的管道（带重试，因为主进程可能还没建好）。</summary>
    public static AudioPipeLink? ConnectClient(string name, bool isWriter, int timeoutMs)
    {
        var client = new NamedPipeClientStream(
            ".", name,
            isWriter ? PipeDirection.Out : PipeDirection.In,
            PipeOptions.WriteThrough | PipeOptions.Asynchronous);

        try
        {
            client.Connect(timeoutMs);
            return new AudioPipeLink(name, null!, client, isWriter);
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"[宿主] 连接管道 {name} 失败：{ex.Message}");
            try { client.Dispose(); } catch { }
            return null;
        }
    }

    /// <summary>
    /// 写一块样本（阻塞直到写完）。帧 = int 长度 + float 数据。
    /// **只允许在工作线程调用**。
    /// </summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        if (!_isWriter) throw new InvalidOperationException("本端是读端。");
        if (samples.Length == 0) return;
        if (samples.Length > MaxFrameSamples) throw new ArgumentException($"单块超过上限 {MaxFrameSamples}");

        var count = samples.Length;
        BitConverter.TryWriteBytes(_scratch.AsSpan(0, sizeof(int)), count);
        var bytes = _scratch.AsSpan(sizeof(int), count * sizeof(float));
        for (var i = 0; i < count; i++)
            BitConverter.TryWriteBytes(bytes[(i * sizeof(float))..], samples[i]);

        var stream = Stream;

        // ⚠ 管道是用 PipeOptions.Asynchronous 建的，这种句柄上**不能**调同步 Write
        //（.NET 内部会转成 WriteAsync 然后抛 InvalidOperationException
        // "Operation is not valid due to the current state of the object"）。所以这里也走异步。
        var length = sizeof(int) + count * sizeof(float);
        Stream.WriteAsync(_scratch.AsMemory(0, length)).GetAwaiter().GetResult();

        // 不调 Flush：管道不支持 flush，PipeStream.Flush 会抛
        // "Operation is not valid due to the current state of the object"。
        // 而且创建时已带 PipeOptions.WriteThrough，写入本来就是立刻可读的。
    }

    /// <summary>
    /// 读一块样本（最多 <paramref name="samples"/>.Length 个）。返回实读样本数，0 = 超时/对端关闭。
    /// **只允许在工作线程调用**。
    /// </summary>
    public int Read(Span<float> samples, int timeoutMs)
    {
        if (_isWriter) throw new InvalidOperationException("本端是写端。");

        var stream = Stream;

        // 帧头：4 字节样本数。用同步 Read，靠 CancellationToken 管超时。
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            var headerRead = ReadExact(stream, _scratch, 0, sizeof(int), cts.Token);
            if (headerRead <= 0) return 0;

            var count = BitConverter.ToInt32(_scratch, 0);
            if (count <= 0 || count > MaxFrameSamples) return 0;

            var total = count * sizeof(float);
            var bodyRead = ReadExact(stream, _scratch, sizeof(int), total, cts.Token);
            if (bodyRead < total) return 0;

            var take = Math.Min(count, samples.Length);
            var bytes = _scratch.AsSpan(sizeof(int), total);
            for (var i = 0; i < take; i++)
                samples[i] = BitConverter.ToSingle(bytes[(i * sizeof(float))..]);
            return take;
        }
        catch (OperationCanceledException)
        {
            return 0;   // 超时：没有数据
        }
        catch (IOException)
        {
            return 0;   // 对端关闭
        }
    }

    private static int ReadExact(Stream stream, byte[] buffer, int offset, int count, CancellationToken token)
    {
        var total = 0;
        while (total < count)
        {
            // 用 Memory<byte> 重载：不分配临时数组，也不允许 Span 进 async
            var n = stream.ReadAsync(buffer.AsMemory(offset + total, count - total), token)
                          .GetAwaiter().GetResult();
            if (n <= 0) return total;
            total += n;
        }
        return total;
    }

    private Stream Stream => _isWriter ? _server! : _client!;

    public void Dispose()
    {
        try { _server?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
    }
}
