using System.IO.MemoryMappedFiles;
using System.Threading;

namespace MateMic.Ai.AiVoiceIpc;

/// <summary>
/// 单生产者 / 单消费者的**跨进程共享内存环形缓冲**（float 单声道）。
///
/// 这是"AI 变声独立进程"的音频通道。为什么不用管道：
/// 命名管道/匿名管道是**流式**的，内核要拷贝两次、还要维护内核缓冲，
/// 每块音频的往返开销在毫秒级且不可控；而共享内存只是 memcpy，
/// 块级传输的开销在微秒级 —— 对本项目"延迟不能增加"的硬要求来说，前者不可接受。
///
/// ## 布局
///
/// 头部固定 64 字节，之后是 float 样本区：
/// <code>
///  偏移 0   long  写指针（**单调递增**，不取模；只有生产者写）
///  偏移 8   long  读指针（单调递增；只有消费者写）
///  偏移 16  int   容量（样本数，用于双端校验）
///  偏移 20  int   魔数（防止误连到别人的映射）
///  偏移 24  int   关闭标志（任一方置 1，另一方据此退出）
///  偏移 32  …     保留
///  偏移 64  …     float 样本区
/// </code>
///
/// 指针单调递增（永不回绕）是刻意的：这样一来"有多少可用"只是两次相减，
/// 不需要处理回绕比较；真正的索引用 <c>pos % capacity</c> 算。long 的容量
/// 足够 48 kHz 连续跑 1200 万年，不必担心溢出。
///
/// ## 线程与进程安全
///
/// 每个方向**只有一个生产者、一个消费者**（这是本项目的实际用法），因此不需要
/// CAS 抢锁：写指针只由生产者改、读指针只由消费者改。两个命名事件负责唤醒：
/// <c>-data</c>（生产者写完通知消费者）、<c>-room</c>（消费者读走通知生产者）。
///
/// ⚠ 这**不是**一个通用的多生产者队列，不要拿它去做别的事。
/// </summary>
public sealed class AudioShmRing : IDisposable
{
    private const int HeaderBytes = 64;
    private const int OffsetWrite = 0;
    private const int OffsetRead = 8;
    private const int OffsetCapacity = 16;
    private const int OffsetMagic = 20;
    private const int OffsetClosed = 24;

    /// <summary>魔数：'MMRR'。用于确认两端连的是同一套布局（写错名字/版本不符时立刻发现）。</summary>
    private const int Magic = 0x4D4D5252;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly EventWaitHandle _dataReady;
    private readonly EventWaitHandle _roomFree;
    private readonly bool _ownsFile;
    private bool _disposed;

    /// <summary>环形缓冲的容量（样本数）。</summary>
    public int CapacitySamples { get; }

    /// <summary>本端是生产者还是消费者（决定我们用哪个事件、写哪个指针）。</summary>
    public bool IsProducer { get; }

    /// <summary>环的名字（诊断用）。</summary>
    public string Name { get; }

    private AudioShmRing(string name, MemoryMappedFile file, MemoryMappedViewAccessor view,
        bool isProducer, int capacity, bool ownsFile)
    {
        Name = name;
        _file = file;
        _view = view;
        IsProducer = isProducer;
        CapacitySamples = capacity;
        _ownsFile = ownsFile;

        // 事件名与映射名同前缀，方便一并排查/清理
        _dataReady = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-data");
        _roomFree = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-room");
    }

    /// <summary>
    /// 生产者侧：创建（或复用）映射并初始化头部。
    /// </summary>
    /// <param name="name">映射名（跨进程靠它对接）。</param>
    /// <param name="capacitySamples">容量（样本数）。建议约 1 秒。</param>
    public static AudioShmRing CreateProducer(string name, int capacitySamples)
    {
        var bytes = HeaderBytes + (long)capacitySamples * sizeof(float);
        var file = MemoryMappedFile.CreateNew(name, bytes);
        var view = file.CreateViewAccessor(0, bytes);

        // 全新映射：指针清零、写魔数与容量
        view.Write(OffsetWrite, 0L);
        view.Write(OffsetRead, 0L);
        view.Write(OffsetMagic, Magic);
        view.Write(OffsetClosed, 0);
        view.Write(OffsetCapacity, capacitySamples);

        return new AudioShmRing(name, file, view, isProducer: true, capacitySamples, ownsFile: true);
    }

    /// <summary>
    /// 消费者侧：打开已存在的映射。**带重试**，因为进程刚起来时生产者可能还没建好。
    /// </summary>
    /// <param name="timeoutMs">等待映射出现的总时长。</param>
    /// <returns>失败返回 null（调用方据此报告错误，而不是崩掉）。</returns>
    public static AudioShmRing? OpenConsumer(string name, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                using var file = MemoryMappedFile.OpenExisting(name);
                // 读头部拿到容量（生产者写的）
                var info = file.CreateViewAccessor(0, HeaderBytes, MemoryMappedFileAccess.Read);
                int magic, capacity;
                info.Read(OffsetMagic, out magic);
                info.Read(OffsetCapacity, out capacity);
                info.Dispose();

                if (magic != Magic || capacity <= 0) return null;   // 不是我们的映射 / 布局不符

                var bytes = HeaderBytes + (long)capacity * sizeof(float);

                // ⚠ view 必须从**我们长期持有的那个** file 创建：
                // 若用 `using var file` 里的临时句柄建 view，file 一释放 view 就悬空
                // （自检时因为 GC 还没回收而侥幸通过，跨进程/长时间运行会随机失效）。
                var owned = MemoryMappedFile.CreateOrOpen(name, bytes);
                var view = owned.CreateViewAccessor(0, bytes);
                return new AudioShmRing(name, owned, view, isProducer: false, capacity, ownsFile: false);
            }
            catch (FileNotFoundException)
            {
                Thread.Sleep(20);   // 生产者还没建好，等
            }
        }

        return null;
    }

    /// <summary>生产者：当前可写（空闲）的样本数。</summary>
    public long FreeSamples
    {
        get
        {
            _view.Read(OffsetWrite, out long w);
            _view.Read(OffsetRead, out long r);
            return CapacitySamples - (w - r);
        }
    }

    /// <summary>消费者：当前可读的样本数。</summary>
    public long AvailableSamples
    {
        get
        {
            _view.Read(OffsetWrite, out long w);
            _view.Read(OffsetRead, out long r);
            var n = w - r;
            return n < 0 ? 0 : n;
        }
    }

    /// <summary>任一方请求关闭（对端应据此退出循环）。</summary>
    public bool IsClosed
    {
        get
        {
            _view.Read(OffsetClosed, out int flag);
            return flag != 0;
        }
    }

    /// <summary>请求关闭：置标志并唤醒对端，避免它一直等在事件上。</summary>
    public void RequestClose()
    {
        try
        {
            _view.Write(OffsetClosed, 1);
            _dataReady.Set();
            _roomFree.Set();
        }
        catch
        {
            // 映射可能已被对方销毁：忽略
        }
    }

    /// <summary>
    /// 生产者写入样本。空间不够时**只写能放下的部分**并返回实写数量 ——
    /// 调用方（AI 变声的输入环）本来就负责"满了丢最老"，所以这里不阻塞、不丢弃决策。
    /// </summary>
    public int Write(ReadOnlySpan<float> samples)
    {
        if (!IsProducer) throw new InvalidOperationException("只有生产者可以写。");
        if (samples.Length == 0) return 0;

        _view.Read(OffsetWrite, out long w);
        _view.Read(OffsetRead, out long r);

        var free = CapacitySamples - (w - r);
        if (free <= 0) return 0;

        var count = (int)Math.Min(samples.Length, free);
        WriteAt(w, samples[..count]);

        // ⚠ 内存屏障：必须先让"样本数据"对另一进程可见，再发布新的写指针。
        // 没有它，CPU/编译器可以重排成"指针先更新"，消费者就可能读到**还没写完**的音频 ——
        // 单进程自检察觉不到（同一份缓存），跨进程才暴露：实测同一段音频走跨进程后
        // 与进程内输出的相关系数只有 0.9595（长度、RMS 都对，波形就是不一样）。
        Thread.MemoryBarrier();
        _view.Write(OffsetWrite, w + count);

        _dataReady.Set();
        return count;
    }

    /// <summary>
    /// 消费者读取样本。不足时只返回现有量（不等待）—— 实时音频里"等"就是延迟。
    /// </summary>
    public int Read(Span<float> samples)
    {
        if (IsProducer) throw new InvalidOperationException("只有消费者可以读。");
        if (samples.Length == 0) return 0;

        _view.Read(OffsetWrite, out long w);
        _view.Read(OffsetRead, out long r);

        var available = w - r;
        if (available <= 0) return 0;

        var count = (int)Math.Min(samples.Length, available);

        // ⚠ 对应 Write 侧的屏障：拿到写指针之后，先确保样本数据已经可见，再去读。
        // 否则可能读到生产者刚写上指针、数据还没落地的半截内容。
        Thread.MemoryBarrier();
        ReadAt(r, samples[..count]);
        Thread.MemoryBarrier();
        _view.Write(OffsetRead, r + count);

        _roomFree.Set();
        return count;
    }

    /// <summary>消费者在没数据时等一会儿（毫秒）。返回 true 表示被唤醒（或有数据/被关闭）。</summary>
    public bool WaitForData(int timeoutMs)
    {
        if (AvailableSamples > 0 || IsClosed) return true;
        _dataReady.WaitOne(timeoutMs);
        return AvailableSamples > 0 || IsClosed;
    }

    /// <summary>生产者在没有空间时等一会儿（毫秒）。</summary>
    public bool WaitForRoom(int timeoutMs)
    {
        if (FreeSamples > 0 || IsClosed) return true;
        _roomFree.WaitOne(timeoutMs);
        return FreeSamples > 0 || IsClosed;
    }

    /// <summary>把从样本位置 <paramref name="pos"/> 开始的连续样本写进环（自动拆成两段）。</summary>
    private void WriteAt(long pos, ReadOnlySpan<float> samples)
    {
        var offsetInRing = (int)(pos % CapacitySamples);
        var first = Math.Min(samples.Length, CapacitySamples - offsetInRing);
        var byteOffset = HeaderBytes + (long)offsetInRing * sizeof(float);

        _view.WriteArray(byteOffset, samples[..first].ToArray(), 0, first);

        if (first < samples.Length)
        {
            // 回绕到开头写剩下的
            _view.WriteArray(HeaderBytes, samples[first..].ToArray(), 0, samples.Length - first);
        }
    }

    /// <summary>从环里读出连续样本（自动拆成两段）。</summary>
    private void ReadAt(long pos, Span<float> samples)
    {
        var offsetInRing = (int)(pos % CapacitySamples);
        var first = Math.Min(samples.Length, CapacitySamples - offsetInRing);
        var byteOffset = HeaderBytes + (long)offsetInRing * sizeof(float);

        var buffer = new float[first];
        _view.ReadArray(byteOffset, buffer, 0, first);
        buffer.AsSpan().CopyTo(samples);

        if (first < samples.Length)
        {
            var rest = samples.Length - first;
            var tail = new float[rest];
            _view.ReadArray(HeaderBytes, tail, 0, rest);
            tail.AsSpan().CopyTo(samples[first..]);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _dataReady.Dispose(); } catch { }
        try { _roomFree.Dispose(); } catch { }
        try { _view.Dispose(); } catch { }
        if (_ownsFile)
        {
            try { _file.Dispose(); } catch { }
        }
    }
}
