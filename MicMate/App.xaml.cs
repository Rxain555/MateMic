using System.Windows;
using MicMate.Core;

namespace MicMate;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 调试 / 便携模式：--appdata <目录> 覆盖数据目录（必须在访问 ConfigStore 其它成员前执行）
        var args = e.Args;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--appdata" or "-a") ConfigStore.UseRoot(args[i + 1]);
        }

        // 单实例：重复启动时把已有窗口激活
        _singleInstance = new Mutex(true, @"Local\MicMate.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("MicMate 已经在运行中，请在系统托盘中查看。", "MicMate",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            ConfigStore.EnsureDirectories();
        }
        catch (Exception ex)
        {
            // 数据目录完全不可写时也要能启动，日志退化为仅内存
            MessageBox.Show("无法创建数据目录：" + ex.Message + "\n程序将以临时目录继续运行。", "MicMate",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Log.Initialize(ConfigStore.LogsDirectory);
        Log.Info("==================================================");
        Log.Info("MicMate 启动");
        Log.Info("数据目录：" + ConfigStore.Root);
        var version = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.1.0";
        Log.Info("版本 " + version + "，运行环境 " + Environment.Version);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("未处理的界面异常", args.Exception);
            MessageBox.Show("发生未处理的错误：" + args.Exception.Message, "MicMate",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Log.Error("未处理的后台异常", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("未观察的任务异常", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);

        // 音频链路诊断：MicMate.exe --audiocheck [秒数] [输出设备关键字] [输入设备关键字]
        if (args.Contains("--audiocheck", StringComparer.OrdinalIgnoreCase))
        {
            AudioDiagnostics.Run(args);
            Shutdown();
            return;
        }

        // 主窗口构造兜底：XAML 解析失败时给出明确提示并退出，
        // 而不是让异常处理框把进程挂在后台（自检/自动化场景下会表现为“卡住”）。
        try
        {
            // --autostart 由开机自启的注册表项写入（见 AutoStartService）：
            // 这种情况下不弹主窗口，直接把窗口收进托盘。
            var main = new MainWindow
            {
                StartMinimizedToTray = args.Contains("--autostart", StringComparer.OrdinalIgnoreCase),
            };
            MainWindow = main;
            main.Show();
        }
        catch (Exception ex)
        {
            Log.Error("主窗口创建失败", ex);
            MessageBox.Show("主窗口创建失败：" + ex.Message, "MicMate",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // 渲染自检：MicMate.exe --selfcheck <输出图片路径> [--expanded]
        // --expanded 会先展开全部模块，便于核对箭头朝向与面板内容。
        var selfCheck = ValueOf(args, "--selfcheck");
        if (selfCheck != null)
        {
            var expandAll = args.Contains("--expanded", StringComparer.OrdinalIgnoreCase);
            var preview = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200),
            };
            var tick = 0;
            preview.Tick += (_, _) =>
            {
                if (++tick > 24)
                {
                    preview.Stop();
                    CaptureAndExit(selfCheck);
                    return;
                }

                if (MainWindow is MainWindow window)
                {
                    if (tick == 2 && expandAll) window.ExpandAllForSelfCheck();
                    window.FeedSelfCheckSignal((float)(tick / 24.0));
                }
            };
            preview.Start();
        }
    }

    /// <summary>取 --name &lt;值&gt; 形式的命令行参数（顺序无关）。</summary>
    private static string? ValueOf(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }

        return null;
    }

    private void CaptureAndExit(string target)
    {
        try
        {
            if (MainWindow is MainWindow window)
            {
                var width = (int)Math.Ceiling(window.ActualWidth);
                var height = (int)Math.Ceiling(window.ActualHeight);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var stream = File.Create(target);
                encoder.Save(stream);
                Log.Info($"自检截图已保存：{target}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("自检截图失败", ex);
        }
        finally
        {
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("MicMate 退出");
        Log.Shutdown();
        if (_ownsMutex) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    public static string VersionText =>
        "v" + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");
}
