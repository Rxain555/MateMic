using System.Windows;
using MateMic.Core;
using MateMic.Ui;

namespace MateMic;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsMutex;

    /// <summary>
    /// 当前是否处于渲染自检（--selfcheck）模式。
    /// 自检截图是用 RenderTargetBitmap 直接渲染 WPF 可视树得到的，
    /// **抓不到 DWM 合成在窗口背后的亚克力材质**，半透明面在截图里会"露白"。
    /// 主窗口据此跳过材质、改用不透明配色，保证截图能真实反映布局与配色。
    /// </summary>
    public static bool IsSelfCheckRun { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 调试 / 便携模式：--appdata <目录> 覆盖数据目录（必须在访问 ConfigStore 其它成员前执行）
        var args = e.Args;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--appdata" or "-a") ConfigStore.UseRoot(args[i + 1]);
        }

        // 单实例：重复启动时把已有窗口激活
        _singleInstance = new Mutex(true, @"Local\MateMic.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            DialogHost.Info(MainWindow as Window, "MateMic", "MateMic 已经在运行中，请在系统托盘中查看。");
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
            DialogHost.Warn(MainWindow as Window, "MateMic", "无法创建数据目录：" + ex.Message + "\n程序将以临时目录继续运行。");
        }

        Log.Initialize(ConfigStore.LogsDirectory);
        Log.Info("==================================================");
        Log.Info("MateMic 启动");
        Log.Info("数据目录：" + ConfigStore.Root);

        // 改名迁移：把旧版（MicMate）留下的开机自启项改写成新 exe 与新名字，
        // 否则开机时系统还会去启动那个已经不存在的旧 exe。
        AutoStartService.MigrateLegacyEntry();
        var version = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.1.0";
        Log.Info("版本 " + version + "，运行环境 " + Environment.Version);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("未处理的界面异常", args.Exception);
            DialogHost.Error(MainWindow as Window, "MateMic", "发生未处理的错误：" + args.Exception.Message);
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

        // 音频链路诊断：MateMic.exe --audiocheck [秒数] [输出设备关键字] [输入设备关键字]
        if (args.Contains("--audiocheck", StringComparer.OrdinalIgnoreCase))
        {
            AudioDiagnostics.Run(args);
            Shutdown();
            return;
        }

        // 快捷键录入通路自检：MateMic.exe --imecheck
        // 验证中文输入法不会拦截录入（曾经反复复发的那个问题），退出码 0=通过。
        if (args.Contains("--imecheck", StringComparer.OrdinalIgnoreCase))
        {
            ImeDiagnostics.Run();
            Shutdown();
            return;
        }

        // 设备热插拔自检：MateMic.exe --devicecheck [秒数]
        // 不开音频流，只订阅设备变更通知并打印前后设备清单。
        // 运行期间手动拔插麦克风即可验证"能否立刻发现、能否识别插回"。
        if (args.Contains("--devicecheck", StringComparer.OrdinalIgnoreCase))
        {
            var exitCode = DeviceDiagnostics.Run(args);
            Shutdown(exitCode);
            return;
        }


        // 设备切换演练：应用照常运行时，过几秒自动切换一次系统默认录音设备再切回来。
        // 用途：验证"默认设备变化 / 设备集合变化"能否真的驱动音频流恢复——
        // 不依赖手动拔插，因此可重复、可自动化。
        var switchTest = args.Contains("--switchtest", StringComparer.OrdinalIgnoreCase);

        // 主窗口构造兜底：XAML 解析失败时给出明确提示并退出，
        // 而不是让异常处理框把进程挂在后台（自检/自动化场景下会表现为“卡住”）。
        // 自检标志必须在构造 MainWindow **之前**确定：窗口在 SourceInitialized 里
        // 就据此决定要不要套亚克力材质（RenderTargetBitmap 抓不到 DWM 合成的材质，
        // 所以只有纯 --selfcheck 截图才需要跳过；--screen 抓的是屏幕，材质必须真开）。
        IsSelfCheckRun = args.Contains("--selfcheck", StringComparer.OrdinalIgnoreCase) &&
                         ScreenModeOf(args) == null;
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

            if (switchTest) RunSwitchTest();
        }
        catch (Exception ex)
        {
            Log.Error("主窗口创建失败", ex);
            DialogHost.Error(MainWindow as Window, "MateMic", "主窗口创建失败：" + ex.Message);
            Shutdown();
            return;
        }

        // 渲染自检：MateMic.exe --selfcheck <输出图片路径> [--expanded] [--screen[=frame]]
        // --expanded 会先展开全部模块，便于核对箭头朝向与面板内容。
        // --screen      抓"屏幕上的真实窗口"（含 DWM 合成的亚克力材质）
        // --screen=frame 让窗口自己绘制（PrintWindow），标题栏的系统按钮在这条路径下一定能抓到
        var selfCheck = ValueOf(args, "--selfcheck");
        if (selfCheck != null)
        {
            var expandAll = args.Contains("--expanded", StringComparer.OrdinalIgnoreCase);
            var screenMode = ScreenModeOf(args);
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
                    if (screenMode != null && MainWindow is MainWindow target)
                        ScreenCapture.CaptureWindow(target, selfCheck, screenMode);
                    else
                        CaptureAndExit(selfCheck);
                    Shutdown();
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

        // 托盘菜单自检：MateMic.exe --traymenucheck <输出图片路径> [--dark]
        // 打开自绘的托盘菜单（Ui\TrayMenuWindow）抓一张窗口图再退出。
        // 用途：核对菜单的圆角、配色与勾选态在浅色/深色下是否都正常（--dark 强制深色主题）。
        var menuCheck = ValueOf(args, "--traymenucheck");
        if (menuCheck != null)
        {
            if (args.Contains("--dark", StringComparer.OrdinalIgnoreCase))
            {
                ThemeManager.Apply(Application.Current.Resources, dark: true,
                    WindowEffects.IsAcrylicActive);
            }

            var preview = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150),
            };
            var tick = 0;
            TrayMenuWindow? menu = null;
            preview.Tick += (_, _) =>
            {
                // 菜单是 140ms 淡入动画，等动画走完再抓，否则抓到的是半透明的中间帧
                if (tick == 1) menu = TrayMenuWindow.Open(processingEnabled: true);
                if (++tick > 8)
                {
                    preview.Stop();
                    // 菜单是分层窗口（AllowsTransparency=True），只能用 desktop 模式抓
                    if (menu != null) ScreenCapture.CaptureWindow(menu, menuCheck, "desktop");
                    try { menu?.Close(); } catch { /* 可能已在关闭流程中，忽略 */ }
                    Shutdown();
                    return;
                }
            };
            preview.Start();
        }

    }

    /// <summary>
    /// 解析截图模式：没有 --screen 返回 null（走 WPF 离屏渲染）；
    /// <c>--screen</c> 返回 "screen"；<c>--screen=frame</c> 返回 "frame"。
    /// </summary>
    private static string? ScreenModeOf(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith("--screen=", StringComparison.OrdinalIgnoreCase))
                return arg["--screen=".Length..];
        }

        return args.Contains("--screen", StringComparer.OrdinalIgnoreCase) ? "screen" : null;
    }

    /// <summary>
    /// 设备切换演练：6 秒后把默认录音设备切到另一台、再过 6 秒切回来，最后退出。
    /// 全程只依赖系统接口，不需要拔插硬件，因此可以反复执行来验证恢复链路。
    /// </summary>
    private void RunSwitchTest()
    {
        Log.Info("==== 设备切换演练开始（--switchtest）====");

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(6),
        };
        var step = 0;
        string? originalId = null;
        string? otherId = null;

        timer.Tick += (_, _) =>
        {
            step++;
            try
            {
                using var devices = new Core.DeviceService();
                var list = devices.Enumerate(NAudio.CoreAudioApi.DataFlow.Capture);
                var current = list.FirstOrDefault(d => d.IsDefault);
                var other = list.FirstOrDefault(d => !d.IsDefault && !d.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
                            ?? list.FirstOrDefault(d => !d.IsDefault);

                if (step == 1)
                {
                    originalId = current?.Id;
                    otherId = other?.Id;
                    Log.Info($"演练：当前默认录音设备「{current?.Name}」，准备切到「{other?.Name}」");
                    if (otherId != null)
                        DeviceSwitcher.SetDefault(otherId, NAudio.CoreAudioApi.DataFlow.Capture);
                }
                else if (step == 2)
                {
                    Log.Info("演练：切回原默认设备");
                    if (originalId != null)
                        DeviceSwitcher.SetDefault(originalId, NAudio.CoreAudioApi.DataFlow.Capture);
                }
                else
                {
                    timer.Stop();
                    Log.Info("==== 设备切换演练结束，3 秒后退出 ====");
                    var bye = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    bye.Tick += (_, _) =>
                    {
                        bye.Stop();
                        Shutdown();
                    };
                    bye.Start();
                }
            }
            catch (Exception ex)
            {
                Log.Error("设备切换演练出错", ex);
                timer.Stop();
            }
        };

        timer.Start();
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
        Log.Info("MateMic 退出");
        Log.Shutdown();
        if (_ownsMutex) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    public static string VersionText =>
        "v" + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");
}
