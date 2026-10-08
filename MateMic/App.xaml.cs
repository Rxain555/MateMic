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

    /// <summary>
    /// 是否处于自检会话（只要传了 --selfcheck 就为真，**与截图方式无关**）。
    /// 自检用的诊断日志据此开关；与"是否跳过亚克力材质"（IsSelfCheckRun）是两个独立概念。
    /// </summary>
    public static bool IsSelfCheckSession { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 调试 / 便携模式：--appdata <目录> 覆盖数据目录（必须在访问 ConfigStore 其它成员前执行）
        var args = e.Args;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--appdata" or "-a") ConfigStore.UseRoot(args[i + 1]);
        }

        // AI 变声的原生运行时装配：**必须早于任何 ONNX 会话创建**
        // （降噪的模型扫描就会建会话）。组件装了就把 onnxruntime 解析到组件里的
        // 那个后端版本，没装就交回默认的 CPU 版；降噪本身始终显式走 CPU，不占显卡。
        //
        // ⚠ 必须把配置里用户选的运算方式传进去。
        // 曾经这里是无参调用，于是 _preferredProvider 永远是 null、每次都走 auto（CUDA），
        // 用户在界面上切成 CPU / DirectML 后**重启也没用**
        //（2026-10-08 用户实测："我之前说的切换运算方式没用，就是重启后也没用"）。
        Ai.AiComponent.InstallNativeResolver();

        // 单实例：重复启动时把已有窗口激活。
        //
        // ⚠ 用了 --appdata 就**跳过**这个检查：那是"隔离测试"的明确信号
        //（换数据目录正是为了不干扰正在使用的实例），若仍然共用同一个 Mutex，
        // 自动化测试会在用户开着程序时被静默拦住 —— 表现为"测试进程什么都没做就退出、
        // 日志和输出都是空的"，极难排查（2026-10-08 实测踩到）。
        var isolated = false;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--appdata" or "-a") { isolated = true; break; }
        }

        if (!isolated)
        {
            _singleInstance = new Mutex(true, @"Local\MateMic.SingleInstance", out _ownsMutex);
            if (!_ownsMutex)
            {
                DialogHost.Info(MainWindow as Window, "MateMic", "MateMic 已经在运行中，请在系统托盘中查看。");
                Shutdown();
                return;
            }
        }
        else
        {
            Log.Info("[启动] 使用独立数据目录，已跳过单实例检查（隔离测试模式）");
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

        // 使用指南（「使用指南」按钮弹出的分页内容）是**内置**的：
        // 正文在仓库的 Assets\使用指南.txt，编译时嵌进程序集。**必须在构造主窗口之前加载**。
        GuideCatalog.Load();

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
        if (HasFlag(args, "--audiocheck"))
        {
            AudioDiagnostics.Run(args);
            Shutdown();
            return;
        }

        // 快捷键录入通路自检：MateMic.exe --imecheck
        // 验证中文输入法不会拦截录入（曾经反复复发的那个问题），退出码 0=通过。
        if (HasFlag(args, "--imecheck"))
        {
            ImeDiagnostics.Run();
            Shutdown();
            return;
        }

        // 设备热插拔自检：MateMic.exe --devicecheck [秒数]
        // 不开音频流，只订阅设备变更通知并打印前后设备清单。
        // 运行期间手动拔插麦克风即可验证"能否立刻发现、能否识别插回"。
        if (HasFlag(args, "--devicecheck"))
        {
            var exitCode = DeviceDiagnostics.Run(args);
            Shutdown(exitCode);
            return;
        }


        // 设备切换演练：应用照常运行时，过几秒自动切换一次系统默认录音设备再切回来。
        // 用途：验证"默认设备变化 / 设备集合变化"能否真的驱动音频流恢复——
        // 不依赖手动拔插，因此可重复、可自动化。
        var switchTest = HasFlag(args, "--switchtest");

        // 主窗口构造兜底：XAML 解析失败时给出明确提示并退出，
        // 而不是让异常处理框把进程挂在后台（自检/自动化场景下会表现为“卡住”）。
        // 自检标志必须在构造 MainWindow **之前**确定：窗口在 SourceInitialized 里
        // 就据此决定要不要套亚克力材质（RenderTargetBitmap 抓不到 DWM 合成的材质，
        // 所以只有纯 --selfcheck 截图才需要跳过；--screen 抓的是屏幕，材质必须真开）。
        // 是否处于"自检会话"（只要传了 --selfcheck 就算，**与截图方式无关**）。
        // 用途：让自检用的诊断日志（布局测量、箭头朝向、展开动画等）生效。
        IsSelfCheckSession = HasFlag(args, "--selfcheck");

        // 是否跳过亚克力材质：只有"纯 --selfcheck"（自绘截图）才跳过；
        // --screen / --screen=frame 抓的是屏幕，材质必须真开。
        //
        // ⚠ 这两个概念以前被合并成一个 IsSelfCheckRun，结果 `--selfcheck --screen=frame`
        //   会让它变成 false，导致**所有自检日志被静默跳过**——排查问题时表现为
        //   "代码明明写了却没日志"，绕了很久。现已拆开。
        IsSelfCheckRun = HasFlag(args, "--selfcheck") &&
                         ScreenModeOf(args) == null;
        try
        {
            // --autostart 由开机自启的注册表项写入（见 AutoStartService）：
            // 这种情况下不弹主窗口，直接把窗口收进托盘。
            // ⚠ 这个标志必须走**构造函数**传入：窗口要在 Show() 之前把自己摆到屏幕外，
            //   而对象初始化器的赋值发生在构造函数之后，那时已经晚了（详见 MainWindow 该属性注释）。
            var main = new MainWindow(HasFlag(args, "--autostart"));
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

        // 动画诊断：MateMic.exe --animatecheck <输出txt路径>
        // 对一张卡片做展开/收起各一次，逐帧记录 Height / ActualHeight / Visibility。
        // 用途：卡片动画"收尾跳变"连续三轮靠推测修改都无效，改为直接测量每一帧。
        var animCheck = ValueOf(args, "--animatecheck");
        if (animCheck != null)
        {
            var animStarter = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(900),
            };
            animStarter.Tick += (_, _) =>
            {
                animStarter.Stop();
                if (MainWindow is MainWindow animWin) animWin.RunAnimationDiagnostic(animCheck);
                else Shutdown();
            };
            animStarter.Start();
            base.OnStartup(e);
            return;
        }

        // 渲染自检：MateMic.exe --selfcheck <输出图片路径> [--expanded] [--screen[=frame]]
        // --expanded 会先展开全部模块，便于核对箭头朝向与面板内容。
        // --screen      抓"屏幕上的真实窗口"（含 DWM 合成的亚克力材质）
        // --screen=frame 让窗口自己绘制（PrintWindow），标题栏的系统按钮在这条路径下一定能抓到
        var selfCheck = ValueOf(args, "--selfcheck");
        if (selfCheck != null)
        {
            // ⚠ 必须精确匹配：Contains 是子串匹配，"--expandcheck" 里含有 "--expanded"，
// 会让逐项检查的自检先被"全部展开"，导致后续点击变成"收起"（测试工具自己制造假故障）。
            var expandAll = args.Any(a => string.Equals(a, "--expanded", StringComparison.OrdinalIgnoreCase));
            var screenMode = ScreenModeOf(args);
            var preview = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200),
            };
            var tick = 0;
            // 逐项展开自检要走两轮共 14 次点击（每次 400ms），需要更长的存活时间
            var tickLimit = HasFlag(args, "--expandcheck") ? 90 : 24;
            preview.Tick += (_, _) =>
            {
                if (++tick > tickLimit)
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
                    // 布局测量放在第 4 拍：--screen=frame 下渲染循环从第 5 拍起会长时间占住 UI 线程，
                    // 放在 Loaded 或更晚都打不进日志（曾因此以为测量没生效）。
                    if (tick == 4) window.LogLayoutGeometry();
                    // 动画是 160 ms，等到第 5 拍（约 1 s）再读角度，确保停在终值
                    if (tick == 5) window.LogExpanderAngles();
                    if (tick == 6 && HasFlag(args, "--expandcheck"))
                        window.CheckExpanders();
                    if (tick == 7 && HasFlag(args, "--eqcheck"))
                        window.CheckEqPresetLinkage();
                    if (tick == 8) window.MeasureChips();
                    if (tick == 10 && HasFlag(args, "--collapsecheck")) window.CheckCollapseExpand();
                    window.FeedSelfCheckSignal((float)(tick / 24.0));
                }
            };
            preview.Start();
        }

        // AI 变声组件安装进度对话框自检：MateMic.exe --aicomponentcheck <输出图片路径> [--dark]
        // 内容是进度条面板（用户把组件包拖到主界面后弹出的那个）。
        var aiCheck = ValueOf(args, "--aicomponentcheck");
        if (aiCheck != null)
        {
            if (HasFlag(args, "--dark"))
            {
                ThemeManager.Apply(Application.Current.Resources, dark: true,
                    WindowEffects.IsAcrylicActive);
            }

            var preview = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200),
            };
            var tick = 0;
            Window? dialog = null;
            preview.Tick += (_, _) =>
            {
                if (tick == 1)
                {
                    var panel = new AiComponentPanel();
                    panel.SetProgress(45, "正在安装 CUDA 运行时…");
                    dialog = Ui.DialogHost.CreateCustom("安装 AI 变声组件", panel, "关闭");
                    dialog.Show();
                    dialog.UpdateLayout();
                }
                if (++tick > 6)
                {
                    preview.Stop();
                    if (dialog != null) ScreenCapture.CaptureWindow(dialog, aiCheck, "desktop");
                    Log.Info($"[AI 变声] 组件状态：{Ai.AiComponent.Inspect().Message}");
                    try { dialog?.Close(); } catch { /* 忽略 */ }
                    Shutdown();
                }
            };
            preview.Start();
            base.OnStartup(e);
            return;
        }

        // 托盘菜单自检：MateMic.exe --traymenucheck <输出图片路径> [--dark]
        // 打开自绘的托盘菜单（Ui\TrayMenuWindow）抓一张窗口图再退出。
        // 用途：核对菜单的圆角、配色与勾选态在浅色/深色下是否都正常（--dark 强制深色主题）。
        var menuCheck = ValueOf(args, "--traymenucheck");
        if (menuCheck != null)
        {
            if (HasFlag(args, "--dark"))
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

        return HasFlag(args, "--screen") ? "screen" : null;
    }

    /// <summary>
    /// 设备切换演练：6 秒后把默认录音设备切到另一台、再过 6 秒切回来，最后退出。
    /// 全程只依赖系统接口，不需要拔插硬件，因此可以反复执行来验证恢复链路。
    /// </summary>
    /// <summary>
    /// 命令行开关的**精确**匹配（忽略大小写）。
    ///
    /// 不能用 `args.Contains("--x")`：那是**子串**匹配，`--expandcheck` 里含有 `--expanded`，
    /// 会让"逐项展开检查"的自检先被"全部展开"，后续点击就变成收起，制造出假故障
    /// （这个坑真踩过：日志里出现 3 个 ✗，排查半天发现是测试工具自己的问题）。
    /// </summary>
    private static bool HasFlag(string[] args, string flag)
        => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

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
