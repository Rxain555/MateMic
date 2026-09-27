namespace MateMic.Core;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>
/// 按天滚动的极简文件日志（保留最近 7 天）。日志写入放在后台队列，绝不阻塞音频线程。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly Queue<string> Pending = new();
    private static readonly AutoResetEvent Signal = new(false);
    private static string _directory = string.Empty;
    private static Thread? _worker;
    private static volatile bool _running;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public static void Initialize(string directory)
    {
        try
        {
            _directory = directory;
            Directory.CreateDirectory(directory);
            Prune();
        }
        catch
        {
            // 日志目录不可写时退化为仅内存日志，不能影响主流程
            _directory = string.Empty;
        }

        _running = true;
        _worker = new Thread(Pump) { IsBackground = true, Name = "MateMic.Log" };
        _worker.Start();
    }

    /// <summary>
    /// 日志文件的编码：**必须显式 UTF-8**。
    /// File.AppendAllLines 默认按系统 ANSI 代码页（中文 Windows 是 936/GBK）写盘，
    /// 而日志内容全是中文（设备名、模块名、错误信息），
    /// 结果就是日志文件里全是 "MateMic 鍚姩" 这种乱码，排查问题时根本没法看。
    /// 新建文件时写 BOM，记事本/资源管理器预览才会正确按 UTF-8 解码。
    /// </summary>
    private static readonly System.Text.UTF8Encoding LogEncoding = new(encoderShouldEmitUTF8Identifier: true);

    public static void Shutdown()
    {
        _running = false;
        Signal.Set();
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);

    public static void Error(string message, Exception? ex = null)
        => Write(LogLevel.Error, ex == null ? message : message + " :: " + ex.GetType().Name + ": " + ex.Message);

    private static void Write(LogLevel level, string message)
    {
        if (level < MinimumLevel) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level.ToString().ToUpperInvariant()}] {message}";
        lock (Gate) Pending.Enqueue(line);
        Signal.Set();
    }

    private static void Pump()
    {
        while (_running || Pending.Count > 0)
        {
            if (Pending.Count == 0)
            {
                Signal.WaitOne(500);
                continue;
            }

            string[] batch;
            lock (Gate)
            {
                batch = Pending.ToArray();
                Pending.Clear();
            }

            try
            {
                if (string.IsNullOrEmpty(_directory)) continue;
                var file = Path.Combine(_directory, $"app_{DateTime.Now:yyyyMMdd}.log");

                // 文件已存在说明之前写过：不要再追加 BOM（否则文件中间会插一个 BOM）
                if (File.Exists(file))
                {
                    File.AppendAllLines(file, batch, LogEncoding);
                }
                else
                {
                    // File.WriteAllLines(+BOM 编码) 会在文件头写入 BOM，
                    // 记事本/资源管理器预览才会按 UTF-8 正确解码中文。
                    File.WriteAllLines(file, batch, LogEncoding);
                }
            }
            catch
            {
                // 日志失败不得影响主流程
            }
        }
    }

    private static void Prune()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var file in Directory.EnumerateFiles(_directory, "app_*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length == 12 && DateTime.TryParseExact(name[4..], "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out var day) && day < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
        }
    }
}
