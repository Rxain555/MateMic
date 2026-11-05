using System.IO.Pipes;

namespace MateMic.Ai.AiVoiceIpc;

/// <summary>
/// 命名管道音频传输（独立进程方案的音频通道）。
///
/// ## 设计原则：**只用同步阻塞 API**
///
/// 前两版失败的教训：
///   · 共享内存版（<see cref="AudioShmRing"/>）自管指针，跨进程后波形与进程内不一致
///     （相关系数 0.87~0.96），加内存屏障无效；
///   · 管道版第一版混用同步/异步 —— `PipeOptions.Asynchronous` 的句柄上调同步 `Write`
///     会抛 `InvalidOperationException`（"Operation is not valid due to the current state"），
///     而纯同步句柄上调 `ReadAsync` 又做不了超时。
///
/// 所以这里彻底只用**同步阻塞**：
///   · 不做超时；停止靠 <see cref="Dispose"/> 关闭管道把阻塞的读写打断（抛 IOException）；
///   · 「同时要喂又要收」由调用方用**两个线程**解决（一个专写、一个专读），
///     而不是在一条线程上靠超时轮转 —— 后者是前面几版复杂度的根源。
///
/// 帧格式：`int 样本数` + `float[样本数]`，小端（BitConverter）。
/// </summary>
public sealed class AudioPipeLink : IDisposable
{
    /// <summary>单帧上限（样本）。远大于任何合理块长，用于校验帧头合法性。</summary>
    public const int MaxFrameSamples = 96000;

    private readonly NamedPipeServerStream? _server;
    private readonly NamedPipeClientStream? _client;
    private readonly byte[] _buffer = new byte[MaxFrameSamples * sizeof(float) + sizeof(int)];
    private volatile bool _disposed;

    /// <summary>管道名（诊断用）。</summary>
    public string Name { get; }

    /// <summary>本端是写端还是读端。</summary>
    public bool IsWriter { get; }

    private AudioPipeLink(string name, NamedPipeServerStream? server, NamedPipeClientStream? client, bool isWriter)
    {
        Name = name;
        _server = server;
        _client = client;
        IsWriter = isWriter;
    }

    private Stream Stream => (Stream?)_server ?? _client!;

    /// <summary>主进程侧：创建管道并**同步等待**对端连上来。</summary>
    /// <param name="name">管道名（含方向后缀）。</param>
    /// <param name="isWriter">true = 本端写（主→宿主）；false = 本端读（宿主→主）。</param>
    /// <param name="connectTimeoutMs">等待对端连接的时长；超时返回 null。</param>
    public static AudioPipeLink? CreateServer(string name, bool isWriter, int connectTimeoutMs)
    {
        NamedPipeServerStream? server = null;
        try
        {
            server = new NamedPipeServerStream(
                name,
                isWriter ? PipeDirection.Out : PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                // 纯同步：不带 Asynchronous —— 我们需要的是"阻塞读/阻塞写"，不是异步。
                PipeOptions.WriteThrough,
                inBufferSize: 1 << 16,
                outBufferSize: 1 << 16);

            // 用 ConnectAsync + 轮询实现超时：同步的 WaitForConnection() 没有超时参数，
            // 一旦子进程没起来就会永久阻塞（界面僵死）。
            var task = server.WaitForConnectionAsync();
            if (!task.Wait(connectTimeoutMs))
            {
                Core.Log.Warn($"[AI 变声] 管道 {name} 在 {connectTimeoutMs} ms 内没有对端连上来");
                try { server.Dispose(); } catch { }
                return null;
            }

            return new AudioPipeLink(name, server, null, isWriter);
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"[AI 变声] 创建管道 {name} 失败：{ex.Message}");
            try { server?.Dispose(); } catch { }
            return null;
        }
    }

    /// <summary>宿主侧：连接主进程已创建的管道（同步阻塞，带超时）。</summary>
    public static AudioPipeLink? ConnectClient(string name, bool isWriter, int timeoutMs)
    {
        var client = new NamedPipeClientStream(
            ".", name,
            isWriter ? PipeDirection.Out : PipeDirection.In,
            PipeOptions.WriteThrough);

        try
        {
            client.Connect(timeoutMs);   // 内部用 WaitNamedPipe，支持超时
            return new AudioPipeLink(name, null, client, isWriter);
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"[宿主] 连接管道 {name} 失败：{ex.Message}");
            try { client.Dispose(); } catch { }
            return null;
        }
    }

    /// <summary>
    /// 写一帧（**阻塞**直到写完或对端关闭）。只允许在工作线程调用。
    /// </summary>
    /// <returns>true = 写成功；false = 对端已关闭（调用方应结束循环）。</returns>
    public bool Write(ReadOnlySpan<float> samples)
    {
        if (!IsWriter) throw new InvalidOperationException("本端是读端。");
        if (_disposed) return false;
        if (samples.Length <= 0) return true;
        if (samples.Length > MaxFrameSamples) throw new ArgumentException($"单帧超过上限 {MaxFrameSamples}");

        var count = samples.Length;
        BitConverter.TryWriteBytes(_buffer.AsSpan(0, sizeof(int)), count);
        var offset = sizeof(int);
        for (var i = 0; i < count; i++)
        {
            BitConverter.TryWriteBytes(_buffer.AsSpan(offset + i * sizeof(float)), samples[i]);
        }

        try
        {
            Stream.Write(_buffer, 0, offset + count * sizeof(float));
            return true;
        }
        catch (IOException)
        {
            return false;   // 对端关闭
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 读一帧（**阻塞**直到读满一帧或对端关闭）。只允许在工作线程调用。
    /// </summary>
    /// <returns>实读样本数；0 = 对端已关闭或数据非法。</returns>
    public int Read(Span<float> samples)
    {
        if (IsWriter) throw new InvalidOperationException("本端是写端。");
        if (_disposed) return 0;

        try
        {
            if (!ReadExact(_buffer, 0, sizeof(int))) return 0;

            var count = BitConverter.ToInt32(_buffer, 0);
            if (count <= 0 || count > MaxFrameSamples) return 0;

            var bytes = count * sizeof(float);
            if (!ReadExact(_buffer, sizeof(int), bytes)) return 0;

            var take = Math.Min(count, samples.Length);
            for (var i = 0; i < take; i++)
            {
                samples[i] = BitConverter.ToSingle(_buffer, sizeof(int) + i * sizeof(float));
            }
            return take;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
    }

    /// <summary>同步读满指定字节数。返回 false 表示对端关闭。</summary>
    private bool ReadExact(byte[] buffer, int offset, int count)
    {
        var total = 0;
        var stream = Stream;
        while (total < count)
        {
            var n = stream.Read(buffer, offset + total, count - total);
            if (n <= 0) return false;
            total += n;
        }
        return true;
    }

    /// <summary>
    /// 关闭管道。**这也是打断阻塞读写的唯一手段** ——
    /// 工作线程卡在 Read/Write 上时，Dispose 会让它抛 IOException 后返回。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _server?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
    }
}
