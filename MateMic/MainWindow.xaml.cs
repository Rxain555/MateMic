using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MateMic.Audio;
using MateMic.Core;
using MateMic.Denoise;
using MateMic.Dsp;
using MateMic.Ui;
using MateMic.ViewModels;
using MateMic.Windows;
using NAudio.CoreAudioApi;

using Rectangle = System.Windows.Shapes.Rectangle;

namespace MateMic;

public partial class MainWindow : Window, System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));

    private const int SpectrumBars = 48;
    private const string HotkeyPlaceholder = "点击输入快捷键";

    /// <summary>录入期间按钮上显示的占位文字（工具栏全局快捷键按钮用）。</summary>
    private const string RecordingPlaceholder = "请按键…";

    /// <summary>音频列表里的按键控件录入期间显示的占位文字。</summary>
    private const string TrackRecordingPlaceholder = "请按键…";

    private readonly ConfigStore _store = new();
    private readonly AppConfig _config;
    private readonly DeviceService _devices = new();
    private readonly AudioEngine _engine;
    private readonly FilePlayerService _player = new();
    private readonly HotkeyService _hotkeys = new();
    private readonly KeyboardHoldService _keyboard = new();

    /// <summary>
    /// 快捷键录入的统一通路。**所有**录入路径都必须走它，不要再用 WPF 的按键事件：
    /// 中文输入法会把 Key 变成 ImeProcessed，WPF 事件里拿不到物理按键（见该类注释）。
    /// </summary>
    private readonly ShortcutKeyCapture _capture = new();
    private readonly TrayIconService _tray = new();
    private readonly DispatcherTimer _timer;
    private DispatcherTimer? _saveTimer;

    /// <summary>设备热插拔的去抖定时器（系统一次插拔会连发多条通知，见 OnDevicesChanged）。</summary>
    private DispatcherTimer? _deviceChangeDebounce;

    /// <summary>设备轮询兜底定时器（系统通知在本机收不到，见 OnDevicePollTick）。</summary>
    private DispatcherTimer? _devicePoll;

    /// <summary>上一次轮询得到的三类设备 ID 指纹，用来判断有没有变化。</summary>
    private string? _lastDeviceFingerprint;

    /// <summary>待处理的设备变更是否确认"设备集合真的变了"（由轮询置位）。</summary>
    private bool _pendingDeviceSetChanged;

    /// <summary>程序内部改下拉框选择时置位，避免把"我们的刷新"当成"用户换了设备"。</summary>
    private bool _suppressDeviceSelection;

    /// <summary>频谱柱（48 根）与复用的频带缓冲。背景由画布的主题底色提供，不再画底槽。</summary>
    private readonly Rectangle[] _inputBars = new Rectangle[SpectrumBars];
    private readonly Rectangle[] _outputBars = new Rectangle[SpectrumBars];
    private readonly float[] _inputBands = new float[SpectrumBars];
    private readonly float[] _outputBands = new float[SpectrumBars];

    /// <summary>底部对数频率刻度的文字（Canvas 精确摆放，只随宽度重排）。</summary>
    private readonly TextBlock[][] _axisLabels = { new TextBlock[6], new TextBlock[6] };

    /// <summary>EQ 的 10 段推子；构造时由 <see cref="BuildEqBands"/> 生成，索引与 EqPreset 的频段一致。</summary>
    private readonly Slider[] _eqSliders = new Slider[EqPreset.BandCount];

    private readonly List<TrackViewModel> _tracks = new();

    private bool _loading = true;
    private bool _exiting;
    private string? _pendingHotkeyGesture;
    private TrackViewModel? _pendingHotkeyTrack;
    private TrackViewModel? _pendingHoldKeyTrack;
    private Action? _statusAction;

    /// <summary>
    /// 开机自启（命令行 --autostart）时置 true：启动完成后直接收进托盘，不弹主窗口。
    ///
    /// ⚠ 必须是**构造函数参数**，不能改回 `{ get; init; }` + 对象初始化器赋值：
    /// 对象初始化器的赋值发生在构造函数**执行完毕之后**，而窗口的离屏摆放
    /// （<see cref="PrepareTrayStartPlacement"/>，必须在 Show() 之前完成）就在构造函数里，
    /// 那时读到的永远是 false —— 2026-10-05 修黑窗时正是踩了这个坑，
    /// 表现为"代码明明改了、日志里却连一行都没打"。
    /// </summary>
    public bool StartMinimizedToTray { get; }

    /// <summary>
    /// 开机自启收托盘时置 true：窗口还得"先离屏显示一次、等首帧渲染完成再 Hide"。
    /// 为什么不能直接不显示 / 为什么不能马上 Hide，见 <see cref="BeginHideToTray"/>。
    /// </summary>
    private bool _hideToTrayAfterFirstFrame;

    /// <summary>离屏显示前记录的窗口位置，收进托盘后原样还原。</summary>
    private double _trayStartLeft;
    private double _trayStartTop;

    /// <summary>离屏显示万一等不到 ContentRendered 时的兜底定时器。</summary>
    private DispatcherTimer? _trayHideFallback;

    /// <summary>Windows 约定的"屏幕外"坐标：虚拟桌面不会覆盖到这里。</summary>
    private const double OffScreenCoordinate = -32000;

    public MainWindow(bool startMinimizedToTray = false)
    {
        StartMinimizedToTray = startMinimizedToTray;

        _config = _store.Load();
        _engine = new AudioEngine(_devices, _config);

        InitializeComponent();

        // AI 变声引擎在后台加载（约 2 秒），期间在开关上显示**真实进度**：
        // 三个模型逐个加载，ProgressChanged 报的是实际阶段；宽度用 300ms 补间平滑过渡。
        //
        // 加载期间**开关本身不切换**（旋钮不动），等加载完成后再让它滑过去 ——
        // 否则"向右填充的进度"和"旋钮滑动"会同时播放（2026-10-08 用户反馈"两个并列播放"）。
        // 这正是设计稿方案 02 的意图：蓝色从左填满轨道，填满后旋钮再滑过去。
        _engine.AiVoice.LoadingChanged += (_, loading) => Dispatcher.BeginInvoke(() =>
        {
            UpdateAiVoiceLoading(loading, _engine.AiVoice.LoadProgress);
            if (!loading) SetAiVoiceSwitchWithoutReentry(true);
        });
        _engine.AiVoice.ProgressChanged += (_, percent) => Dispatcher.BeginInvoke(() =>
            UpdateAiVoiceLoading(true, percent));

        // 主题必须在窗口第一次渲染之前定下来，否则会先闪一下浅色。
        ApplyThemeToResources();

        ApplyWindowIcon();
        ApplyCaptionIcon();
        RestoreWindowPlacement();
        PrepareTrayStartPlacement();
        VersionText.Text = App.VersionText;
        BuildSpectrumBars(InputSpectrumCanvas, _inputBars);
        BuildSpectrumBars(OutputSpectrumCanvas, _outputBars);
        BuildSpectrumAxis(InputAxisCanvas, _axisLabels[0]);
        BuildSpectrumAxis(OutputAxisCanvas, _axisLabels[1]);
        BuildLevelScale();
        BuildEqBands();

        // 构造期这些画布的 ActualWidth 还是 0，上面几次调用都会提前返回；SizeChanged 也不保证会补上。
        // 因此在窗口布局完成的 Loaded 里再排一次 —— 这是"刻度反复不出现"的根治手段。
        Loaded += (_, _) =>
        {
            LayoutSpectrumBars(InputSpectrumCanvas, _inputBars);
            LayoutSpectrumBars(OutputSpectrumCanvas, _outputBars);
            BuildSpectrumAxis(InputAxisCanvas, _axisLabels[0]);
            BuildSpectrumAxis(OutputAxisCanvas, _axisLabels[1]);
            BuildLevelScale();
            RefreshEqVisuals();
        };

        TrackList.ItemsSource = _tracks;
        LoadTracksFromConfig();

        _devices.DevicesChanged += (_, _) => Dispatcher.BeginInvoke(new Action(OnDevicesChanged));

        _player.StateChanged += (_, state) => Dispatcher.BeginInvoke(new Action(() =>
        {
            HandlePlaybackState(state);
            RefreshTrackHighlight();
        }));

        _player.LoopEnabled = _config.Player.Loop;
        _player.SetVolumePercent(_config.Player.Volume);
        _engine.MicMixer.SetPlayer(_player.Output);
        _engine.SetAudioMonitor(_config.Player.AudioMonitor);

        Loaded += OnLoaded;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnRenderTick;
    }

    // =============================================================== 自绘标题栏

    /// <summary>拖动标题栏移动窗口。双击不做事（窗口固定尺寸，最大化没意义）。</summary>
    private void OnCaptionBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1) return;   // 双击不做最大化

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标松开太快时 DragMove 会抛：忽略即可，不影响使用
        }
    }

    /// <summary>标题栏最小化按钮：最小化到任务栏（不是收进托盘）。</summary>
    private void OnCaptionMinimizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    /// <summary>
    /// 标题栏关闭按钮：直接复用窗口的关闭流程（OnClosing 里按「关闭到托盘」设置决定是收托盘还是退出）。
    /// </summary>
    private void OnCaptionCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>把 exe 图标画到自绘标题栏上（与任务栏、托盘用的是同一份图标）。</summary>
    private void ApplyCaptionIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.ico");
            if (!File.Exists(path)) return;

            var decoder = new IconBitmapDecoder(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            CaptionIcon.Source = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        }
        catch (Exception ex)
        {
            Log.Debug("标题栏图标加载失败：" + ex.Message);
        }
    }

    // =============================================================== 初始化

    /// <summary>
    /// 引擎启动后重新应用降噪模型，并做一次运行期校验。
    /// 音频链在 Start() 之后才真正建立，因此这里再套用一次并记录实际状态，
    /// 避免出现"日志说模型已载入、实际链路却没带上降噪"这种不一致。
    /// </summary>
    private void ReapplyDenoiseAfterStart()
    {
        try
        {
            var info = FindConfiguredModel();
            if (info != null) ApplyModel(info);

            _engine.UpdateAllParameters();
            _engine.StartupMute = false;   // 模型就绪，解除启动静音
            Log.Info("引擎启动后重新应用：降噪模型=" + _engine.DenoiseModelName +
                     "，配置=" + _config.Denoise.Model);
        }
        catch (Exception ex)
        {
            _engine.StartupMute = false;
            Log.Error("引擎启动后重新应用降噪失败", ex);
        }

        // 启动 2 秒后再核对一次：如果降噪已开却一帧都没处理，就把结论写进日志
        var verifier = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        verifier.Tick += (_, _) =>
        {
            verifier.Stop();
            Log.Info("运行期校验：" + _engine.DenoiseDiagnostics);
        };
        verifier.Start();
    }

    /// <summary>
    /// 按配置里的模型标识找模型。
    /// 标识规则：空 = 内置经典降噪；否则是外部模型的文件名（不带扩展名）。
    /// 为了让老配置（曾经存的是显示名）也能平滑过渡，这里同时也按显示名比一次。
    /// </summary>
    private DenoiseModelInfo? FindConfiguredModel()
    {
        var key = _config.Denoise.Model;
        var models = ModelCatalog.Scan();

        // 内置经典降噪（列表里那一项 IsDefault = true）
        if (string.IsNullOrWhiteSpace(key)) return models.FirstOrDefault(m => m.IsDefault);

        var byKey = MatchModel(models, key);
        if (byKey != null) return byKey;

        // 配置里指定的模型不在这台机器上（没随程序带、或被删了）：
        // 退一步用首选模型，再退一步才是内置经典降噪。
        // 不这样做的话，新装用户的配置写着 dpdfnet2 但文件不在，
        // 首启就会是"内置降噪"，而界面上显示的却是另一个名字。
        var preferred = MatchModel(models, ModelCatalog.PreferredModelName);
        if (preferred != null)
        {
            Log.Warn($"配置里的降噪模型「{key}」不在此机器上，已改用首选模型「{preferred.Name}」。");
            return preferred;
        }

        Log.Warn($"配置里的降噪模型「{key}」不在此机器上，且首选模型也不可用，已回退到内置「{ModelCatalog.DefaultModelName}」。");
        return models.FirstOrDefault(m => m.IsDefault) ?? models.FirstOrDefault();
    }

    /// <summary>按"文件名"或"显示名"在模型列表里找，找不到返回 null。</summary>
    private static DenoiseModelInfo? MatchModel(IReadOnlyList<DenoiseModelInfo> models, string key)
        => models.FirstOrDefault(m =>
               string.Equals(Path.GetFileNameWithoutExtension(m.Path), key, StringComparison.OrdinalIgnoreCase))
           ?? models.FirstOrDefault(m => string.Equals(m.Name, key, StringComparison.Ordinal));

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 本程序没有任何文本输入需求，但有一堆"看起来像输入框"的控件。
            // 不禁用输入法的话，点过它们之后 IME 会把窗口当成文本输入目标，
            // 中文输入法会被唤起来。
            //
            // ⚠ 只做到这一层是**不够的**：实测证明 TSF 输入法（微软拼音/搜狗）
            // 不依赖窗口的 IMM32 上下文，摘掉上下文照样工作。
            // 真正解决问题的是 ShortcutKeyCapture 的低层键盘钩子——它直接在系统把按键
            // 交给输入法之前拿到物理按键。下面两步只是"少打扰用户"的辅助手段。
            InputMethod.SetIsInputMethodEnabled(this, false);

            // 窗口材质（亚克力 / 不透明回退）要在可视树建好之后再同步一次：
            // 主题里的 DynamicResource 会自己跟着资源变化刷新，
            // 但窗口 Background 与顶栏底色是 StaticResource 固定的，需要在这里补。
            ApplyWindowMaterial();

            // 快捷键录入钩子：挂到窗口过程上（清候选窗 + 兜底）
            _capture.Attach(new WindowInteropHelper(this).Handle);
            _capture.SuppressInputMethod();

            _hotkeys.Attach(this);
            _tray.Show("MateMic");
            _tray.ShowRequested += (_, _) => RestoreFromTray();
            // 托盘右键菜单改为 WPF 侧自绘（Ui\TrayMenuWindow），托盘服务只负责上报"要弹菜单"
            _tray.MenuRequested += (_, _) => Dispatcher.BeginInvoke(new Action(ShowTrayMenu));

            RefreshDeviceLists();
            ApplyConfigToControls();
            RefreshModels();

            // 设备轮询兜底：本机实测系统通知回调收不到，没有它热插拔就完全不工作。
            // 1.5 秒一次、只比对一个 ID 指纹串，代价可以忽略。
            _lastDeviceFingerprint = BuildDeviceFingerprint();
            _devicePoll = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(1500),
            };
            _devicePoll.Tick += OnDevicePollTick;
            _devicePoll.Start();

            // 关键顺序：RefreshModels 会载入配置里指定的降噪模型，
            // 而处理链是否包含降噪由 UpdateAllParameters 决定。
            // 因此必须在载入模型**之后**再统一应用一次参数，
            // 否则重启后链路仍按"默认模型"构建，必须手动重选一次模型才生效。
            _engine.UpdateAllParameters();
            UpdateMixLineStatus();

            _engine.Start();
            if (!_engine.IsRunning && _engine.LastError != null)
                ShowStatus("音频引擎启动失败：" + _engine.LastError, false);
            else
                Log.Info($"音频链路就绪：采集 {_engine.CaptureLatencyMs} ms / 输出 {_engine.OutputLatencyMs} ms");

            // 引擎启动完成后立刻进入启动静音：此时链路是"默认模型"状态，
            // 先静音、等下面把配置里的模型套用完毕再放开，
            // 避免开头一小段把未降噪的底噪送出去。
            _engine.StartupMute = true;
            ReapplyDenoiseAfterStart();

            // 刻意不在这里弹"降噪已启用"提示：每次开软件都弹一条状态条很吵，
            // 而且总开关与各模块的开关状态本来就在界面上看得见。

            RegisterConfiguredHotkeys();
            RefreshTrackHighlight();
            _timer.Start();

            // 开机自启：不弹窗口，直接收进托盘（托盘双击即可恢复）。
            // 用户若关掉了「关闭到托盘」，则尊重该设置，仍然显示窗口。
            if (StartMinimizedToTray)
            {
                if (_config.CloseToTray)
                {
                    // ⚠ 这里**不能**直接 Hide()：真正的显示动作还没发生，这次 Hide 会被随后的
                    //   显示覆盖，留下一个纯黑死窗口。原因与实测时间线见 BeginHideToTray。
                    BeginHideToTray();
                }
                else
                {
                    Log.Info("以 --autostart 启动：但「关闭到托盘」已关闭，仍显示主窗口。");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("初始化失败", ex);
            ShowStatus("初始化失败：" + ex.Message, false);
        }
        finally
        {
            _loading = false;
        }
    }

    // =============================================================== 开机自启收托盘

    /// <summary>
    /// 开机自启要直接收进托盘时，先把窗口挪到屏幕外并禁止抢焦点。
    ///
    /// 必须**离屏**而不是"不显示"：窗口在这个阶段已经有句柄了，WPF 随时可能把它显示出来
    /// （见 <see cref="BeginHideToTray"/>），只有摆到虚拟桌面之外才能保证用户全程看不到。
    /// 位置在这里改过，收进托盘后由 <see cref="RestoreTrayStartPlacement"/> 原样还原，
    /// 否则下次从托盘恢复窗口会跑到屏幕外。
    /// </summary>
    private void PrepareTrayStartPlacement()
    {
        if (!StartMinimizedToTray || !_config.CloseToTray) return;

        _hideToTrayAfterFirstFrame = true;
        _trayStartLeft = Left;
        _trayStartTop = Top;

        // CenterScreen 会覆盖手工设置的 Left/Top，所以这里必须同时改成 Manual。
        // （配置里没有有效位置时 Left/Top 就是 NaN，还原时按工作区重新居中。）
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = OffScreenCoordinate;
        Top = OffScreenCoordinate;

        // 收托盘的启动不该抢走用户正在打字的窗口的焦点
        ShowActivated = false;

        Log.Info("以 --autostart 启动：窗口已离屏摆放，等待首帧渲染完成后收进托盘。");
    }

    /// <summary>
    /// 把窗口收进托盘（开机自启路径）。
    ///
    /// ⚠ **不能在 Loaded 里直接 Hide()**——2026-10-05 用户报的"开机后一个纯黑窗口"就是这么来的：
    /// WPF 的 <c>Show()</c> 并不当场显示窗口，真正的 ShowWindow 要等首帧渲染完成才执行。
    /// 在 Loaded 里 Hide() 时窗口的 WS_VISIBLE 还没置上，这一刀等于打在空气上；
    /// 而 WPF 排着队的那次显示随后照常执行，于是窗口在"已经收进托盘"之后又被显示出来。
    /// 此时它从未完成过首帧渲染，DWM 拿不到内容，屏幕上就留下一个**纯黑死窗口**：
    /// 点任务栏没反应，只有从托盘「显示主界面」重新 Show 一次才恢复正常（与用户描述完全一致）。
    ///
    /// 实测时间线（--autostart，50 ms 采样一次窗口样式位）：
    ///   584 ms  HWND 已建好，WS_VISIBLE = False
    ///  1400 ms  日志打出"已直接最小化到托盘"（旧代码在这里 Hide）
    ///  1724 ms  WS_VISIBLE 变成 True —— 窗口是在 Hide 之后才被显示出来的
    /// 样式位由 0x06CA0000 变为 0x16CA0000，差的正是 0x10000000（WS_VISIBLE）。
    ///
    /// 因此改为：**等首帧渲染完成（ContentRendered）之后再 Hide**。
    /// 这一次 Hide 撤销的是一次"已经发生的显示"，必定生效；配合构造期的离屏摆放，
    /// 用户在整段时间里看不到任何东西，也不会出现黑窗或残留的任务栏按钮。
    /// </summary>
    private void BeginHideToTray()
    {
        if (!_hideToTrayAfterFirstFrame) return;

        void HideAfterFirstFrame(object? sender, EventArgs e)
        {
            ContentRendered -= HideAfterFirstFrame;
            if (!_hideToTrayAfterFirstFrame) return;   // 兜底已经处理过了
            CompleteHideToTray("首帧渲染完成");
        }

        ContentRendered += HideAfterFirstFrame;

        // 兜底：万一窗口走不完首帧渲染（ContentRendered 不触发），
        // 也不能让它一直待在屏幕外当"隐身进程"。3 秒后无条件收起。
        _trayHideFallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _trayHideFallback.Tick += (_, _) =>
        {
            _trayHideFallback?.Stop();
            _trayHideFallback = null;
            if (_hideToTrayAfterFirstFrame) CompleteHideToTray("兜底定时器到期");
        };
        _trayHideFallback.Start();
    }

    /// <summary>真正执行收起：隐藏窗口 → 还原位置 → 核对"系统层面确实看不见了"。</summary>
    private void CompleteHideToTray(string reason)
    {
        _hideToTrayAfterFirstFrame = false;
        _trayHideFallback?.Stop();
        _trayHideFallback = null;

        Hide();
        RestoreTrayStartPlacement();

        // 核对用的是 Win32 的 WS_VISIBLE，不是 WPF 的 IsVisible：
        // 后者在 Hide() 一调用就变 false，看不出 HWND 有没有真的藏住（这正是当初漏掉这个 bug 的原因）。
        // 排到 ApplicationIdle：此时首帧渲染与随后的显示动作都已经执行完毕。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (WindowEffects.IsWindowReallyVisible(this))
            {
                Log.Warn($"收起托盘后窗口在系统层面仍然可见（{reason}），改用 ShowWindow(SW_HIDE) 兜底。");
                WindowEffects.ForceHide(this);
            }

            Log.Info($"以 --autostart 启动：已直接最小化到托盘（{reason}；" +
                     $"窗口真实可见={WindowEffects.IsWindowReallyVisible(this)}）。");
        }), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// 把构造期为了离屏显示而改掉的窗口位置还原，否则从托盘恢复时窗口会跑到屏幕外。
    /// 原本是 CenterScreen（配置里没有有效位置）时，用工作区手工算一次居中——
    /// 窗口已经显示过，CenterScreen 不会再生效。
    /// </summary>
    private void RestoreTrayStartPlacement()
    {
        ShowActivated = true;

        if (!double.IsNaN(_trayStartLeft) && !double.IsNaN(_trayStartTop))
        {
            Left = _trayStartLeft;
            Top = _trayStartTop;
            return;
        }

        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    /// <summary>
    /// 自检：**预设 / 推子 / 曲线三者必须始终一致**（`--eqcheck`）。
    ///
    /// 这是本次改动最容易出错的地方：数据有两个入口（点预设、拖推子），
    /// 界面有三处呈现（chip 勾选、推子位置、曲线形状），
    /// 任何一处没跟上都会是"看起来对、实际不对"。所以这里按真实路径走一遍，
    /// 并把每步的实际数值打进日志 —— 不靠肉眼比对截图。
    /// </summary>
    public void CheckEqPresetLinkage()
    {
        Log.Info("[EQ 自检] ==== 开始（预设 → 手动 → 重置）====");

        // ① 点预设：增益表整体替换，推子与曲线都要跟上
        var warm = ToneGrid.Children.OfType<RadioButton>()
            .FirstOrDefault(c => string.Equals(c.Tag as string, nameof(ToneStyle.Warm), StringComparison.Ordinal));
        if (warm == null)
        {
            Log.Warn("[EQ 自检] 找不到「沉稳」预设 chip，自检中止。");
            return;
        }

        // 模拟真实点击：RadioButton 会先把自己置位，再触发 Click
        warm.IsChecked = true;
        OnToneSelected(warm, new RoutedEventArgs());
        Log.Info($"[EQ 自检] ① 点「沉稳」→ chip 勾选={warm.IsChecked}（应 True）"
                 + $"｜配置={DescribeGains()}"
                 + $"｜推子={DescribeSliders()}"
                 + $"｜期望={string.Join(",", EqPreset.GainsOf(ToneStyle.Warm).Select(g => g.ToString("0.#")))}");

        // ② 手动拖第 1 段（31 Hz）：只该改这一段，且预设勾选必须被清掉
        _eqSliders[0].Value = 9;
        Log.Info($"[EQ 自检] ② 31 Hz 拖到 +9 → 配置={DescribeGains()}"
                 + $"｜推子={DescribeSliders()}"
                 + $"｜仍有预设勾选={AnyToneChipChecked()}（应 False）");

        // ③ 重置：全部归零、预设清空
        OnEqResetClick(EqResetButton, new RoutedEventArgs());
        Log.Info($"[EQ 自检] ③ 重置 → 配置={DescribeGains()}"
                 + $"｜推子={DescribeSliders()}"
                 + $"｜仍有预设勾选={AnyToneChipChecked()}（应 False）"
                 + $"｜曲线点数={(EqCurveCanvas.Children.OfType<System.Windows.Shapes.Polyline>().FirstOrDefault()?.Points.Count ?? 0)}（应 10）");

        Log.Info("[EQ 自检] ==== 结束 ====");
    }

    private string DescribeGains()
        => string.Join(",", EqPreset.Normalize(_config.Tone.Gains).Select(g => g.ToString("0.#")));

    private string DescribeSliders()
        => string.Join(",", _eqSliders.Select(s => s.Value.ToString("0.#")));

    private bool AnyToneChipChecked()
        => ToneGrid.Children.OfType<RadioButton>().Any(c => c.IsChecked == true);

    /// <summary>
    /// 设置窗口（任务栏 / Alt+Tab）图标。
    /// 从输出目录的 Assets\appicon.ico 读取——不使用 XAML 的 pack URI，
    /// 因为该文件在 csproj 里以 None 方式复制，并未打包进程序集资源。
    /// </summary>
    private void ApplyWindowIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.ico");
            if (!File.Exists(path))
            {
                Log.Warn("未找到窗口图标文件：" + path);
                return;
            }

            var decoder = new IconBitmapDecoder(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Icon = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        }
        catch (Exception ex)
        {
            Log.Warn("加载窗口图标失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 窗口句柄一创建就套上亚克力材质。
    /// 放在这里（而不是 Loaded）是因为 DWM 只认 HWND，越早设置，
    /// 首帧就直接是磨砂效果，不会先闪一下不透明背景。
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (IsSelfCheckMode)
        {
            // 自检截图走 RenderTargetBitmap，抓不到 DWM 合成的窗口背后内容，
            // 半透明面在截图里会变成"透明直接露白"，看不出真实观感。
            // 因此自检模式下强制用不透明配色，保证截图可比对、不误导。
            Log.Info("自检模式：跳过亚克力材质，使用不透明配色以便截图比对。");
            return;
        }

        // 窗口边框与标题栏的深浅跟随**本程序的**深色模式开关，而不是系统主题：
        // 用户明确选了这个开关，界面就该整体一致，不该出现"界面深色、窗口边框浅色"。
        WindowEffects.ApplyAcrylic(this, _config.DarkMode);

        // 标题栏是自绘的，这里只做一次客观自检：确认窗口本身确实"不可最大化"，
        // 免得哪天改坏了又被用户发现（最大化入口已经不存在了，但样式位也该是干净的）。
        WindowEffects.LogCaptionButtonState(this);
    }

    /// <summary>当前是否运行在 <c>--selfcheck</c>（渲染自检）模式。</summary>
    private static bool IsSelfCheckMode => App.IsSelfCheckSession;

    /// <summary>
    /// 把"亚克力是否生效"同步到主题资源，并让已创建的可视元素重新取色。
    /// 主题里凡是用 <c>DynamicResource</c> 引用的画刷会自动更新；
    /// 而 Style 里用 StaticResource 固定下来的少数几处（窗口背景、顶栏底色）
    /// 需要在这里手工补一刀。
    /// </summary>
    /// <summary>
    /// 把当前深浅配色写进**应用级**资源。
    /// 用应用级而不是窗口级：自绘对话框（DialogHost）是独立窗口，
    /// 只有应用级资源才能被它看到，否则深色模式下弹窗仍会是浅色。
    /// </summary>
    private void ApplyThemeToResources()
        => ThemeManager.Apply(Application.Current.Resources, _config.DarkMode, WindowEffects.IsAcrylicActive);

    private void ApplyWindowMaterial()
    {
        ApplyThemeToResources();

        // 窗口底色用"柔和渐变 + 材质透明层"：叠在亚克力之上会呈现有光的玻璃感；
        // 不支持材质时它就是普通的浅灰渐变，界面同样完整。
        if (TryFindResource("WindowSurfaceBrush") is Brush surface)
        {
            Background = surface;
        }

        if (TryFindResource("AcrylicTopBarBrush") is Brush topBar)
            TopBar.Background = topBar;
    }

    /// <summary>
    /// 恢复窗口位置。**尺寸不再恢复**：窗口已经固定大小（XAML 里 ResizeMode=NoResize），
    /// 三栏布局是按这个宽度设计的，拉宽也没有更多信息可显示，索性钉死；
    /// 位置仍然记住，方便用户把它放到顺手的地方。
    /// </summary>
    private void RestoreWindowPlacement()
    {
        if (double.IsNaN(_config.WindowLeft) || double.IsNaN(_config.WindowTop)) return;

        // 虚拟桌面可能跨多显示器，且副屏可以在主屏左边/上边（此时坐标为负）。
        // 早期实现用 `Left > -50 && Left < VirtualScreenWidth` 判断，等于假设原点在 (0,0)，
        // 于是放在左侧副屏的窗口每次启动都会被判定为“跑到屏幕外”而拉回中间。
        var desktopLeft = SystemParameters.VirtualScreenLeft;
        var desktopTop = SystemParameters.VirtualScreenTop;
        var desktopRight = desktopLeft + SystemParameters.VirtualScreenWidth;
        var desktopBottom = desktopTop + SystemParameters.VirtualScreenHeight;

        // 至少要有 100×100 落在虚拟桌面内，才认为这个位置还能用
        var visible = _config.WindowLeft + 100 > desktopLeft && _config.WindowLeft < desktopRight - 100 &&
                      _config.WindowTop + 100 > desktopTop && _config.WindowTop < desktopBottom - 100;

        if (!visible)
        {
            Log.Info($"保存的窗口位置 ({_config.WindowLeft:0},{_config.WindowTop:0}) 已不在当前桌面范围内，改为居中显示。");
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = _config.WindowLeft;
        Top = _config.WindowTop;
    }

    private void ApplyConfigToControls()
    {
        _loading = true;
        try
        {
            ToggleProcessing.IsChecked = _config.AudioProcessingEnabled;
            ToggleMonitor.IsChecked = _config.MonitorEnabled;
            ToggleAutoStart.IsChecked = AutoStartService.IsEnabled();
            _config.AutoStart = ToggleAutoStart.IsChecked == true;
            ToggleCloseTray.IsChecked = _config.CloseToTray;
            ToggleDarkMode.IsChecked = _config.DarkMode;

            ToggleGate.IsChecked = _config.NoiseGate.Enabled;
            GateThresholdSlider.Value = _config.NoiseGate.ThresholdDb;
            GateReleaseSlider.Value = _config.NoiseGate.ReleaseMs;

            ToggleDenoise.IsChecked = _config.Denoise.Enabled;
            DenoiseStrengthSlider.Value = _config.Denoise.Strength;
            DenoiseWetSlider.Value = _config.Denoise.Wet;

            ToggleLoudness.IsChecked = _config.Loudness.Enabled;
            LoudnessTargetSlider.Value = _config.Loudness.TargetLufs;
            LoudnessSpeedSlider.Value = _config.Loudness.Speed;

            ToggleTone.IsChecked = _config.Tone.Enabled;
            // 增益表先规整（长度不对或为 null 一律当平坦），再把推子回填到同一份数据上
            _config.Tone.Gains = EqPreset.Normalize(_config.Tone.Gains);
            SelectToneChip(_config.Tone.Style);
            ApplyEqGainsToSliders();

            ToggleEffect.IsChecked = _config.Effect.Enabled;
            SelectEffectChip(_config.Effect.Kind);
            EffectAmountSlider.Value = _config.Effect.Amount;

            ToggleGain.IsChecked = _config.Gain.Enabled;
            GainSlider.Value = _config.Gain.GainDb;
            ToggleVoiceChanger.IsChecked = _config.VoiceChanger.Enabled;
            VoicePitchSlider.Value = _config.VoiceChanger.Semitones;
            VoiceFormantSlider.Value = _config.VoiceChanger.FormantSemitones;
            VoiceGenderSlider.Value = _config.VoiceChanger.GenderFactor;
            VoiceMixSlider.Value = _config.VoiceChanger.Mix;

            ToggleAudioMonitor.IsChecked = _config.Player.AudioMonitor;
            ToggleLoop.IsChecked = _config.Player.Loop;
            ToggleHoldKey.IsChecked = _config.Player.EnableHoldKey;
            PlayerVolumeSlider.Value = _config.Player.Volume;

            SetHotkeyButtonText(_config.ToggleHotkey);
        }
        finally
        {
            _loading = false;
        }

        SyncExpanderArrows();
        _engine.UpdateAllParameters();

        // 记录实际回填到界面的配置，便于排查"看起来没保存"的问题
        Log.Info("已回填配置：总开关=" + _config.AudioProcessingEnabled +
                 $"，监听={_config.MonitorEnabled}，热键={(string.IsNullOrWhiteSpace(_config.ToggleHotkey) ? "(未设置)" : _config.ToggleHotkey)}" +
                 $"，降噪模型={_config.Denoise.Model}，音色={_config.Tone.Style?.ToString() ?? "(未选)"}" +
                 $"，效果={_config.Effect.Kind?.ToString() ?? "(未选)"}，播放列表={_tracks.Count} 项" +
                 $"，窗口={Width:0}x{Height:0}@{Left:0},{Top:0}");
    }

    /// <summary>快捷键按钮上的文字：未设置时显示提示，已设置时显示键位（效果图里那行是示例内容）。</summary>
    private void SetHotkeyButtonText(string gesture)
        => HotkeyButton.Content = string.IsNullOrWhiteSpace(gesture) ? HotkeyPlaceholder : gesture;

    /// <summary>
    /// 建频谱柱：48 根矩形，**不再画底槽**。
    ///
    /// 用户问"竖块之间的分隔有什么含义"——没有含义：那是每根柱子下面各垫了一根浅灰底槽
    /// （48 根紧挨着），相邻底槽在交界处形成了竖线。已整体删除；背景改由画布自己的主题底色提供。
    /// </summary>
    private void BuildSpectrumBars(Canvas canvas, Rectangle[] bars)
    {
        canvas.Children.Clear();

        for (var i = 0; i < bars.Length; i++)
        {
            var bar = new Rectangle();
            // 动态引用资源：这些柱子只在构造时建一次，用"取一次再赋值"的话，
            // 用户切换深浅主题之后它们仍是旧主题的颜色（与 EQ 滑条同一类问题）。
            bar.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush");
            // 关掉边缘混合：否则相邻柱子即使重叠也会在接缝处半透明，看着仍像有分隔
            RenderOptions.SetEdgeMode(bar, EdgeMode.Aliased);
            bars[i] = bar;
            canvas.Children.Add(bar);
        }

        canvas.SizeChanged += (_, _) => LayoutSpectrumBars(canvas, bars);
        LayoutSpectrumBars(canvas, bars);
    }

    /// <summary>
    /// 频谱柱布局：铺满宽度、**整像素对齐并相互重叠 1px**。
    ///
    /// 为什么必须重叠：每根矩形独立渲染并按亚像素位置抗锯齿，边缘像素会被半透明化；
    /// 即使数学上"紧贴"，相邻两根之间也会露出一条背景色的细缝（用户反馈的"柱之间还有分隔"）。
    /// 做法：左右边界各自四舍五入到整像素，宽度 = 右边界 − 左边界 + 1（多出的 1px 盖住接缝），
    /// 再用 SnapsToDevicePixels + EdgeMode.Aliased 关掉边缘混合。
    /// </summary>
    private static void LayoutSpectrumBars(Canvas canvas, Rectangle[] bars)
    {
        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        if (width <= 2 || height <= 2 || bars.Length == 0) return;

        var slot = width / bars.Length;

        for (var i = 0; i < bars.Length; i++)
        {
            var left = Math.Round(i * slot);
            var right = Math.Round((i + 1) * slot);
            var barWidth = Math.Max(1.0, right - left + 1);   // +1 与右邻重叠，消除接缝

            bars[i].Width = barWidth;
            bars[i].SnapsToDevicePixels = true;
            Canvas.SetLeft(bars[i], left);
        }
    }

    /// <summary>
    /// 频段范围（与 OutputStage 的频带划分一致）：40 Hz – 16 kHz。
    /// 底部刻度的六个标签就落在这个区间内，按对数定位。
    /// </summary>
    private const float SpectrumLowHz = 40f;
    private const float SpectrumHighHz = 16000f;

    /// <summary>把频率映射到画布 x（对数刻度，与频带划分方式一致）。</summary>
    private static double HzToX(double hz, double width)
    {
        var lo = Math.Log10(SpectrumLowHz);
        var hi = Math.Log10(SpectrumHighHz);
        var t = (Math.Log10(Math.Clamp(hz, SpectrumLowHz, SpectrumHighHz)) - lo) / (hi - lo);
        return Math.Clamp(t, 0, 1) * width;
    }

    /// <summary>dBFS 显示范围：0 dBFS 在顶部，-60 dBFS 在底部。</summary>
    private const float MeterCeilingDb = 0f;
    private const float MeterFloorDb = -60f;

    /// <summary>
    /// 底部频率刻度：**按真实对数位置摆放**（不是等高居中）。
    ///
    /// 2026-10-04 反复过一轮：先按对数定位（正确）→ 被误解为"要居中"而改成等高 →
    /// 用户澄清"刻度肯定要按真实位置" → 恢复对数。
    /// 位置用 <see cref="HzToX"/> 算出后减去文字半宽，使刻度数字**中心**对齐频率位置。
    /// </summary>
    private void BuildSpectrumAxis(Canvas axis, TextBlock[] labels)
    {
        // ⚠ 先挂 SizeChanged 再判断宽度：构造期 ActualWidth 为 0，若先 return 就永远不会重排
        axis.SizeChanged += (_, _) => BuildSpectrumAxis(axis, labels);

        axis.Children.Clear();
        var width = axis.ActualWidth;
        if (width <= 2) return;

        var ticks = new[] { 100f, 300f, 1000f, 3000f, 8000f, 16000f };

        for (var i = 0; i < ticks.Length && i < labels.Length; i++)
        {
            var label = new TextBlock
            {
                Text = ticks[i] >= 1000 ? $"{ticks[i] / 1000:0.#}k" : $"{ticks[i]:0}",
                FontSize = 9.5,
                // 不设 FontFamily：跟随窗口的 AppFont（MiSans），与全局字体一致。
                // 之前这里硬编码了 Consolas/Cascadia Mono，所以刻度看起来"没用 MiSans"——
                // 不是字号太小导致的回退，是我显式指定的。等宽在这个尺度上收益也有限，
                // 实测 MiSans 的数字自然宽 23px，比等宽的 30px 还窄，不会挤到相邻刻度。
            };
            // 颜色动态引用资源：刻度只在窗口构造时建一次，取一次赋值的话，
            // 切换深浅主题之后文字仍是旧主题的颜色（与 EQ 滑条同一类问题）。
            label.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var w = label.DesiredSize.Width;

            labels[i] = label;
            // 让文字中心落在该频率的真实对数位置；两端做夹紧，避免越界被裁
            Canvas.SetLeft(label, Math.Clamp(HzToX(ticks[i], width) - w / 2, 0, Math.Max(0, width - w)));
            Canvas.SetTop(label, 0);
            axis.Children.Add(label);
        }

        // 自检专用：刻度曾反复"看不见"，用数据自证它确实被创建、且位置是按对数算的
        if (IsSelfCheckMode)
        {
            var info = string.Join(" ", labels.Where(l => l != null)
                                              .Select(l => $"{l!.Text}@{(int)Canvas.GetLeft(l)}"));
            Log.Info($"[刻度自检] 画布宽={width:0} 子元素={axis.Children.Count} → {info}");
        }
    }

    /// <summary>
    /// 自检专用：**异步**逐个模块点击展开，每次等 400 ms（动画 220/160 ms）后再读结果。
    ///
    /// 踩过的坑：一开始在 OnExpandClick 返回后**同一帧**就读旋转角，结果全都是"朝左"，
    /// 看着像箭头动画坏了——其实动画还没开始跑，基线就是 90°。**测量必须在动画结束后**。
    /// </summary>
    public void CheckExpanders()
    {
        var items = new (string Name, Button Button, FrameworkElement Panel)[]
        {
            ("噪声门", ExpandGate, PanelGate),
            ("AI 降噪", ExpandDenoise, PanelDenoise),
            ("响度平衡", ExpandLoudness, PanelLoudness),
            ("EQ 均衡器", ExpandTone, PanelTone),
            ("效果器", ExpandEffect, PanelCreative),
            ("增益", ExpandGain, PanelGain),
            ("DSP 变声", ExpandVoiceChanger, PanelVoiceChanger),
            ("AI 变声", ExpandAiVoice, PanelAiVoice),
        };

        // 两轮：第 1 轮按各自**初始状态取反**（本来就是展开的先收起），第 2 轮再全部取反回来。
        // 一开始假设"第 1 轮 = 全部展开"，结果配置里本来就展开的三项被点成了收起、报 ✗ ——
        // 那是我的假设错了，不是程序错了。
        var pass = 0;
        var index = 0;
        var pending = default(Button);
        var wasVisible = false;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };

        timer.Tick += (_, _) =>
        {
            if (pending != null)
            {
                var entry = items[index - 1];
                var arrow = FindDescendant<System.Windows.Shapes.Path>(pending, "Arrow");
                var dir = arrow?.Data is Geometry g
                    ? DescribeArrowDirection(g, arrow.RenderTransform, out _)
                    : "?";

                // 每一轮都"取反"，所以期望值 = 点击前状态的相反
                var expectedOpen = !wasVisible;
                var expected = expectedOpen ? "朝下" : "朝左";
                var ok = entry.Panel.Visibility == (expectedOpen ? Visibility.Visible : Visibility.Collapsed)
                         && dir == expected
                         && (expectedOpen ? entry.Panel.ActualHeight > 0.5 : entry.Panel.ActualHeight < 0.5);
                Log.Info($"[展开自检] 第{pass + 1}轮 {entry.Name}：{wasVisible switch { true => "展开", false => "收起" }} → "
                         + $"{(expectedOpen ? "展开" : "收起")}｜面板={entry.Panel.Visibility}"
                         + $"｜Tag={pending.Tag}｜箭头{dir}（期望{expected}）{(ok ? " ✓" : " ✗")}"
                         + $"｜内容高={entry.Panel.ActualHeight:0.#}｜动画残留={_panelAnimations.ContainsKey(pending)}");
            }

            if (index >= items.Length)
            {
                if (pass == 0)
                {
                    pass = 1;
                    index = 0;
                    pending = null;          // ⚠ 必须清掉：否则下一拍会去取 items[index-1] = items[-1] 而抛异常
                    Log.Info("[展开自检] ---- 第 1 轮完成，开始第 2 轮（全部反向）----");
                    return;
                }
                timer.Stop();
                Log.Info("[展开自检] ==== 结束 ====");
                return;
            }

            pending = items[index].Button;
            wasVisible = items[index].Panel.Visibility == Visibility.Visible;
            OnExpandClick(pending, new RoutedEventArgs());
            index++;
        };

        Log.Info("[展开自检] ==== 两轮逐个点击（每轮取反），每次等 400ms 读结果 ====");
        timer.Start();
    }

    /// <summary>
    /// 自检专用：**只针对音色风格**，按"收起 → 展开"这条实际路径逐拍采样。
    ///
    /// 用户报告的现象是"打开时展开是正常的，收起后再展开就展不完全"，
    /// 所以必须按这个顺序测，并逐拍记录高度，才能看出卡在哪一拍。
    /// </summary>
    public void CheckCollapseExpand()
    {
        var panel = PanelTone;
        var button = ExpandTone;
        var step = 0;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };

        void Report(string stage)
        {
            var arrow = FindDescendant<System.Windows.Shapes.Path>(button, "Arrow");
            var dir = arrow?.Data is Geometry g
                ? DescribeArrowDirection(g, arrow.RenderTransform, out _)
                : "?";
            // 卡片高度：ToneGrid 的祖先是 Card Border
            var card = Descendants(this).OfType<Border>()
                .FirstOrDefault(b => b.Style == (Style)FindResource("Card") && Descendants(b).Contains(panel));
            Log.Info($"[收起展开自检] {stage}：面板={panel.Visibility}"
                     + $"｜面板高={panel.ActualHeight:0.#}（Height 属性={(double.IsNaN(panel.Height) ? "NaN" : panel.Height.ToString("0.#"))}）"
                     + $"｜箭头{dir}｜Tag={button.Tag}"
                     + $"｜卡片高={(card?.ActualHeight ?? -1):0.#}"
                     + $"｜动画残留={_panelAnimations.ContainsKey(button)}");
        }

        // 从确定的状态开始：先保证是收起态（启动配置可能本来就是展开）
        if (panel.Visibility == Visibility.Visible)
            OnExpandClick(button, new RoutedEventArgs());

        Report("初始（已确保为收起态）");
        timer.Tick += (_, _) =>
        {
            switch (step)
            {
                case 2:
                    Report("收起动画应已结束");
                    break;
                case 3:
                    OnExpandClick(button, new RoutedEventArgs());     // 关键路径：从收起态再展开
                    break;
                case 5:
                    Report("再展开后 ~240ms");
                    break;
                case 7:
                    Report("再展开后 ~480ms（应完全展开）");
                    timer.Stop();
                    Log.Info("[收起展开自检] ==== 结束 ====");
                    return;
            }
            step++;
        };

        Log.Info("[收起展开自检] ==== 开始（仅音色风格）====");
        timer.Start();
    }

    /// <summary>自检专用：量出各 chip 容器的真实可用宽度与每个 chip 的实际宽度，用于判断是否被挤压。</summary>
    public void MeasureChips()
    {
        void Dump(string name, Panel host)
        {
            var width = host.ActualWidth;
            var chips = host.Children.OfType<FrameworkElement>().ToList();
            var sizes = chips.Select(c =>
            {
                try
                {
                    var tl = c.TransformToAncestor(this).Transform(new Point(0, 0));
                    return $"{c.ActualWidth:0.#}×{c.ActualHeight:0.#}@{tl.X:0}";
                }
                catch { return $"{c.ActualWidth:0.#}×{c.ActualHeight:0.#}"; }
            });
            var cols = host is System.Windows.Controls.Primitives.UniformGrid ug ? ug.Columns : -1;
            // 文字自然宽度（含模板里 Padding 7+7）：用来判断 chip 宽度是否真的不够
            var textWidths = chips.Select(c => c is ContentControl cc && cc.Content is string s
                ? new FormattedText(s, System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        new Typeface(cc.FontFamily, cc.FontStyle, cc.FontWeight, cc.FontStretch),
                        cc.FontSize, Brushes.Black,
                        VisualTreeHelper.GetDpi(this).PixelsPerDip).Width
                : double.NaN).ToList();
            var needed = textWidths.Where(w => !double.IsNaN(w)).Select(w => w + 14).ToList();
            Log.Info($"[尺寸自检] {name}：容器宽={width:0.#}｜列数={cols}｜子项={chips.Count}"
                     + $"｜每个 chip {" "}{string.Join("  ", sizes)}");
            Log.Info($"[尺寸自检] {name}：文字自然宽 {string.Join(" / ", textWidths.Select(w => $"{w:0.#}"))}"
                     + $"｜加内边距后需要 {string.Join(" / ", needed.Select(w => $"{w:0.#}"))}"
                     + $"｜chip 实际 {(chips.Count > 0 ? chips[0].ActualWidth : 0):0.#}"
                     + $" ⇒ {(needed.Count > 0 && chips.Count > 0 && needed[0] > chips[0].ActualWidth ? "放不下" : "放得下")}");
        }

        Dump("EQ 均衡器预设 ToneGrid", ToneGrid);
        Dump("效果器 EffectGrid", EffectGrid);

        // 左栏纵向空间是否够：滚动区视口 vs 内容自然高度，以及各卡片被分配到的实际高度
        if (LeftScroll != null)
        {
            var viewport = LeftScroll.ViewportHeight;
            var extent = LeftScroll.ExtentHeight;
            Log.Info($"[纵向自检] 左栏滚动区：视口高={viewport:0.#}｜内容高={extent:0.#}"
                     + $"｜可滚动={(LeftScroll.ScrollableHeight > 0.5 ? "是" : "否")}"
                     + $"｜卡住={extent > viewport + 0.5 && LeftScroll.ScrollableHeight <= 0.5}");

            var cards = new List<FrameworkElement>();
            void Walk(DependencyObject node)
            {
                var n = VisualTreeHelper.GetChildrenCount(node);
                for (var i = 0; i < n; i++)
                {
                    var child = VisualTreeHelper.GetChild(node, i);
                    if (child is Border b && b.Style == (Style)FindResource("Card")) cards.Add(b);
                    else Walk(child);
                }
            }
            Walk(LeftRegion);
            Log.Info($"[纵向自检] 左栏共 {cards.Count} 张卡片，实际高度："
                     + string.Join("  ", cards.Select(c => $"{c.ActualHeight:0.#}"))
                     + $"｜合计 {cards.Sum(c => c.ActualHeight + c.Margin.Top + c.Margin.Bottom):0.#}");

            // 逐张卡片带标题报告，便于看"哪一张"被压（只看高度数字分不清是哪张）
            foreach (var card in cards)
            {
                var title = Descendants(card).OfType<TextBlock>()
                    .FirstOrDefault(t => t.Style == (Style)FindResource("SectionTitle"))?.Text ?? "(无标题)";
                var panelName = Descendants(card).OfType<FrameworkElement>()
                    .FirstOrDefault(e => e.Name is "PanelGate" or "PanelDenoise" or "PanelLoudness"
                                              or "PanelTone" or "PanelCreative" or "PanelGain"
                                              or "PanelVoiceChanger")?.Name ?? "-";
                var panelHeight = Descendants(card).OfType<FrameworkElement>()
                    .FirstOrDefault(e => e.Name == panelName)?.ActualHeight ?? double.NaN;
                Log.Info($"[纵向自检] 卡片「{title}」高={card.ActualHeight:0.#}"
                         + $"｜内容区 {panelName} 高={(double.IsNaN(panelHeight) ? 0 : panelHeight):0.#}"
                         + $"｜可见={(Descendants(card).OfType<FrameworkElement>().FirstOrDefault(e => e.Name == panelName)?.Visibility.ToString() ?? "-")}");
            }
        }
        else
        {
            Log.Info("[纵向自检] 左栏没有 ScrollViewer —— 内容超高时只能被压缩/裁切");
        }
    }

    /// <summary>自检专用：枚举某元素下的全部视觉子元素。</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    /// <summary>
    /// 自检专用：把关键元素**相对窗口的左右坐标**打进日志，用来核对间距是否对称。
    ///
    /// 用户反馈"小卡片与左边框的间距"和"与频谱卡片的间距"看着不等 —— 这种问题靠看截图猜不准，
    /// 这里直接算：最左卡片左边界、最左卡片右边界（含投影外扩）、中栏卡片左边界、
    /// 最右卡片右边界，以及窗口宽度，全部换算成"距窗口左边多少像素"。
    /// </summary>
    public void LogLayoutGeometry()
    {
        if (!IsSelfCheckMode) return;
        try
        {
            LogLayoutGeometryCore();
        }
        catch (Exception ex)
        {
            // DispatcherTimer 会静默吞掉回调里的异常，导致"代码明明写了却没日志"。
            // 显式兜住并打印，避免再次误判成"测量没生效"。
            Log.Info($"[布局测量] 测量过程异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void LogLayoutGeometryCore()
    {
        double Left(FrameworkElement e)
        {
            if (e.ActualWidth <= 0) return double.NaN;
            try { return e.TransformToAncestor(this).Transform(new Point(0, 0)).X; }
            catch { return double.NaN; }
        }

        double Right(FrameworkElement e)
        {
            if (e.ActualWidth <= 0) return double.NaN;
            try { return e.TransformToAncestor(this).Transform(new Point(e.ActualWidth, 0)).X; }
            catch { return double.NaN; }
        }

        // 三个分区容器的真实坐标（含各自 Padding）⇒ 直接算两处间距
        var leftStart = Left(LeftRegion);
        var leftEnd = Right(LeftRegion);
        var midStart = Left(MidRegion);
        var rightStart = Left(RightRegion);
        var rightEnd = Right(RightRegion);

        // **卡片本身**的坐标才是肉眼看到的边界（容器还有 Padding）
        FrameworkElement? FirstCard(DependencyObject root)
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is Border b && b.Style == (Style)FindResource("Card")) return b;
                if (child is FrameworkElement fe && fe.Name != "Arrow")
                {
                    var deeper = FirstCard(child);
                    if (deeper != null) return deeper;
                }
            }
            return null;
        }

        var lc = FirstCard(LeftRegion);
        var mc = FirstCard(MidRegion);
        var rc = FirstCard(RightRegion);

        Log.Info($"[布局测量] 窗口宽 {ActualWidth:0}｜"
                 + $"左栏容器 [{leftStart:0} … {leftEnd:0}]｜"
                 + $"中栏容器 [{midStart:0} … {Right(MidRegion):0}]｜"
                 + $"右栏容器 [{rightStart:0} … {rightEnd:0}]");
        Log.Info($"[布局测量] 距窗口左边框 {leftStart:0}px｜"
                 + $"左栏与中栏之间 {midStart - leftEnd:0}px｜"
                 + $"中栏与右栏之间 {rightStart - Right(MidRegion):0}px｜"
                 + $"距窗口右边框 {ActualWidth - rightEnd:0}px");
        if (lc != null && mc != null && rc != null)
        {
            Log.Info($"[卡片测量] 卡片左距边框 {Left(lc):0}px｜"
                     + $"左栏卡片右缘 {Right(lc):0} → 中栏卡片左缘 {Left(mc):0} = {Left(mc) - Right(lc):0}px｜"
                     + $"中栏卡片右缘 {Right(mc):0} → 右栏卡片左缘 {Left(rc):0} = {Left(rc) - Right(mc):0}px｜"
                     + $"卡片右距边框 {ActualWidth - Right(rc):0}px");
        }

        // 纵向：各栏**最后一张卡片**的底缘到窗口底边的距离（用户反馈"底部距离偏远"）
        double Bottom(FrameworkElement e)
        {
            try
            {
                var p = e.TransformToAncestor(this).Transform(new Point(0, e.ActualHeight));
                return ActualHeight - p.Y;
            }
            catch { return double.NaN; }
        }

        (string Name, FrameworkElement Panel)[] columns =
        {
            ("左栏", LeftRegion),
            ("中栏", MidRegion),
            ("右栏", RightRegion),
        };

        foreach (var (name, root) in columns)
        {
            var cards = Descendants(root).OfType<Border>()
                .Where(b => b.Style == (Style)FindResource("Card"))
                .ToList();
            if (cards.Count == 0) continue;

            var last = cards.OrderBy(c => Bottom(c)).First();      // 最靠下的那张
            var lastBottom = Bottom(last);
            var title = Descendants(last).OfType<TextBlock>()
                .FirstOrDefault(t => t.Style == (Style)FindResource("SectionTitle"))?.Text;
            if (title == null)
            {
                title = Descendants(last).OfType<TextBlock>()
                    .FirstOrDefault(t => t.Style == (Style)FindResource("LabelText"))?.Text;
            }

            Log.Info($"[底部测量] {name}共 {cards.Count} 张卡片｜最下一张「{title ?? "?"}」底缘距窗口底边 {lastBottom:0}px"
                     + $"｜其余底距 {string.Join("/", cards.OrderByDescending(c => Bottom(c)).Select(c => $"{Bottom(c):0}"))}");
        }

        // 用户点名的元素：播放音频的**列表**。它不是 Card 样式（自绘 Border），
        // 所以单独量它"底缘到窗口底边"与"右缘到窗口右边框"是否一致。
        if (TrackList != null)
        {
            var rightGap = ActualWidth - Right(TrackList);
            Log.Info($"[底部测量] 音频列表 TrackList：底缘距窗口底边 {Bottom(TrackList):0}px"
                     + $"｜右缘距窗口右边框 {rightGap:0}px｜高 {TrackList.ActualHeight:0.#}"
                     + $"｜两者差 {Math.Abs(Bottom(TrackList) - rightGap):0}px");
        }

        // 右栏最靠下的 Border（列表外壳）
        var rightLowest = Descendants(RightRegion).OfType<Border>()
            .Where(b => b.ActualHeight > 1 && Bottom(b) > 0)
            .OrderBy(Bottom)
            .FirstOrDefault();
        if (rightLowest != null)
        {
            Log.Info($"[底部测量] 右栏最下 Border：底缘距窗口底边 {Bottom(rightLowest):0}px"
                     + $"｜高 {rightLowest.ActualHeight:0.#}"
                     + $"｜是否 Card 样式={rightLowest.Style == (Style)FindResource("Card")}");
        }
    }

    /// <summary>把频带值画成柱高（频带值已是 0…1）。</summary>
    private static void UpdateSpectrumBars(Canvas canvas, Rectangle[] bars, SpectrumAnalyzer analyzer, float[] scratch)
    {
        var height = canvas.ActualHeight;
        if (height <= 2) return;

        var count = Math.Min(analyzer.BandCount, scratch.Length);
        analyzer.CopyBands(scratch, count);

        for (var i = 0; i < bars.Length && i < count; i++)
        {
            var v = Math.Clamp(scratch[i], 0f, 1f);
            // 最低保留 1px，否则完全安静时看不出"这里有一根柱子"
            var h = Math.Max(1.0, v * height);
            bars[i].Height = h;
            Canvas.SetTop(bars[i], height - h);
        }
    }

    private static double LinearToDb(float linear)
        => linear <= 1e-6f ? -120 : 20 * Math.Log10(linear);

    private static string FormatDb(double db)
        => db <= -99 ? "−∞ dBFS" : $"{db:0.0} dBFS";


    // =============================================================== 渲染循环

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return;

        _engine.UpdateAnalysis();

        UpdateSpectrumBars(InputSpectrumCanvas, _inputBars, _engine.InputSpectrum, _inputBands);
        UpdateSpectrumBars(OutputSpectrumCanvas, _outputBars, _engine.OutputSpectrum, _outputBands);

        UpdateLevelMeter();
        UpdateTrackProgress();
    }

    /// <summary>
    /// 把播放进度写进列表项（行的背景条按它填充）。
    /// 顺带把"当前项"的标记一起维护，两者本来就是同一件事的两面。
    /// </summary>
    private void UpdateTrackProgress()
    {
        if (_tracks.Count == 0) return;

        var playing = _player.CurrentPath;
        var percent = _player.Progress * 100;

        foreach (var track in _tracks)
        {
            var isCurrent = playing != null
                            && string.Equals(track.FilePath, playing, StringComparison.OrdinalIgnoreCase);
            track.IsCurrent = isCurrent;
            track.ProgressPercent = isCurrent ? percent : 0;
        }
    }

    /// <summary>峰值保持指针的当前 dB（会缓慢回落）。</summary>
    private double _levelPeakDb = MeterFloorDb;
    private long _levelPeakTicks;

    /// <summary>
    /// 电平表（2026-10-04 第三次改版）。
    ///
    /// 这条不是"进度条"，而是**判断音量合不合适的参照尺**：底色固定铺满绿→黄→红，
    /// 用一个**从右往左的遮罩**表示"当前有多响"——越响遮罩越窄、露出的绿色越多；
    /// 太小声时遮罩盖住绿色段、只剩红黄，一眼就知道"要调大"。
    ///
    /// 另外修掉两个旧问题：
    ///   · 线性峰值 ×3.2 当位置用 ⇒ 任何峰值 >0.31 都被 clamp 到 1、指针永远顶在最右（改成 dB 映射）；
    ///   · 读数放在可变宽度的列里 ⇒ 文字长度变化会带动电平平条长短（读数已移到固定宽度列）。
    /// </summary>
    private void UpdateLevelMeter()
    {
        var width = LevelTrack.ActualWidth;
        if (width <= 1) return;

        var rmsDb = LinearToDb(_engine.OutputSpectrum.Rms);

        // 遮罩从右往左盖住"高于当前电平"的部分；露出来的就是当前电平所占的比例
        var levelFraction = DbToFraction(rmsDb);
        LevelMaskRect.Width = Math.Max(0, width * (1 - levelFraction));

        // 峰值保持：**先原地停留 2 秒，再按 6 dB/s 往回缩**（用户要的语义）。
        // 显示位置取 max(峰值保持, 当前电平)，这样这条线不会落在进度条右端的左边
        //（否则会出现"黑线在右、进度条在左、中间空一段"那种反直觉的关系）。
        var now = Stopwatch.GetTimestamp();
        if (_levelPeakTicks == 0) { _levelPeakTicks = now; _levelPeakHoldUntil = now; }

        var peakDb = LinearToDb(_engine.OutputSpectrum.Peak);
        var holdValue = Math.Max(peakDb, _levelPeakDb);          // 跟涨
        var holdSeconds = (now - _levelPeakHoldUntil) / (double)Stopwatch.Frequency;

        if (holdValue > _levelPeakDb)
        {
            _levelPeakDb = holdValue;
            _levelPeakHoldUntil = now;                           // 刷新高点 ⇒ 重新计时停留
        }
        else if (holdSeconds > PeakHoldSeconds)
        {
            var elapsed = (now - _levelPeakTicks) / (double)Stopwatch.Frequency;
            _levelPeakDb = Math.Max(MeterFloorDb, _levelPeakDb - PeakReleaseDbPerSecond * elapsed);
        }

        _levelPeakTicks = now;
        if (_levelPeakDb < MeterFloorDb) _levelPeakDb = MeterFloorDb;

        var shown = Math.Max(_levelPeakDb, rmsDb);               // 不低于当前电平
        var peakAt = Math.Clamp(width * DbToFraction(shown), 0, Math.Max(0, width - 2));
        LevelPeak.Margin = new Thickness(peakAt, 0, 0, 0);
    }

    /// <summary>峰值保持的停留时长（秒）与回落速率（dB/s）。</summary>
    private const double PeakHoldSeconds = 2.0;
    private const double PeakReleaseDbPerSecond = 6.0;

    private long _levelPeakHoldUntil;

    /// <summary>把 dB 映射到 0…1 的位置（-60 dBFS 在左、0 dBFS 在右）。</summary>
    private static double DbToFraction(double db)
        => Math.Clamp((db - MeterFloorDb) / (MeterCeilingDb - MeterFloorDb), 0, 1);

    /// <summary>电平条上方的 dB 刻度（-48 / -36 / -24 / -12 / 0），静态绘制，仅随尺寸重建。</summary>
    private bool _levelScaleHooked;

    private void BuildLevelScale()
    {
        // 与频谱刻度同样的坑：先挂钩、后判断宽度（构造期宽度为 0）
        if (!_levelScaleHooked)
        {
            _levelScaleHooked = true;
            LevelTrack.SizeChanged += (_, _) => BuildLevelScale();
        }

        LevelScaleCanvas.Children.Clear();
        var width = LevelTrack.ActualWidth;
        if (width <= 1) return;

        var brush = (Brush)new BrushConverter().ConvertFromString("#33808A98")!;
        brush.Freeze();

        foreach (var db in new[] { -48.0, -36.0, -24.0, -12.0, 0.0 })
        {
            var x = Math.Round(width * DbToFraction(db)) + 0.5;
            var tick = new System.Windows.Shapes.Line
            {
                X1 = x, X2 = x, Y1 = 3, Y2 = 6,
                Stroke = brush,
                StrokeThickness = 1,
                SnapsToDevicePixels = true,
            };
            LevelScaleCanvas.Children.Add(tick);
        }
    }

    /// <summary>
    /// 自检专用：把全部模块展开。
    /// 默认状态是所有模块折叠、所有开关关闭，因此截图校验展开态时要显式调用。
    /// 注意：OnExpandClick 是**切换**语义，点三次会回到原状（早期版本这里就是三次，
    /// 结果自检截图里模块全是收起的），因此只在收起时点一次。
    /// </summary>
    public void ExpandAllForSelfCheck()
    {
        var panels = new (Button Button, UIElement Panel)[]
        {
            (ExpandGate, PanelGate),
            (ExpandDenoise, PanelDenoise),
            (ExpandLoudness, PanelLoudness),
            (ExpandTone, PanelTone),
            (ExpandEffect, PanelCreative),
            (ExpandGain, PanelGain),
            (ExpandVoiceChanger, PanelVoiceChanger),
            (ExpandAiVoice, PanelAiVoice),
        };

        foreach (var (button, panel) in panels)
        {
            if (panel.Visibility != Visibility.Visible)
                OnExpandClick(button, new RoutedEventArgs());
        }
    }

    /// <summary>
    /// 自检专用：把每个展开箭头的 Tag、旋转角，以及**从几何算出的实际朝向**写进日志。
    ///
    /// 为什么算朝向而不是看截图：这个坑已经踩了三次。角度对不对取决于基线几何，
    /// 而"看起来朝哪"多次判断失误。这里直接取变换后的三点，用"尖相对弦中点的偏移"判定，
    /// 输出 **朝左/朝右/朝上/朝下** 四个词。约定：**展开 = 朝下，收起 = 朝左**。
    /// </summary>
    public void LogExpanderAngles()
    {
        var buttons = new (string Name, Button Button)[]
        {
            ("噪声门", ExpandGate), ("AI 降噪", ExpandDenoise), ("响度平衡", ExpandLoudness),
            ("EQ 均衡器", ExpandTone), ("效果器", ExpandEffect), ("增益", ExpandGain),
            ("DSP 变声", ExpandVoiceChanger),
            ("AI 变声", ExpandAiVoice),
        };

        foreach (var (name, button) in buttons)
        {
            var arrow = FindDescendant<System.Windows.Shapes.Path>(button, "Arrow");
            if (arrow?.Data is not Geometry geometry)
            {
                Log.Info($"[箭头自检] {name}：未找到 Arrow 几何");
                continue;
            }

            var angle = (arrow.RenderTransform as RotateTransform)?.Angle ?? 0;
            var direction = DescribeArrowDirection(geometry, arrow.RenderTransform, out var debug);
            var expected = button.Tag is true ? "朝下" : "朝左";
            Log.Info($"[箭头自检] {name}：Tag={button.Tag} Angle={angle:0.#} → 实际{direction}，期望{expected}"
                     + (direction == expected ? " ✓" : " ✗") + $" | {debug}");
        }
    }

    /// <summary>
    /// 从折线几何 + 变换算出它"尖"朝哪边。
    /// 三个点：首点 P0、顶点 Vertex（尖）、末点 P2。尖相对 P0→P2 弦中点的偏移即朝向。
    /// 屏幕坐标 y 向下 ⇒ +y = 朝下、+x = 朝右。
    /// </summary>
    private static string DescribeArrowDirection(Geometry geometry, Transform? transform, out string debug)
    {
        var flattened = geometry.GetFlattenedPathGeometry();
        if (flattened.Figures.Count == 0) { debug = "无 Figure"; return "未知"; }

        var figure = flattened.Figures[0];
        var points = new List<Point>();
        foreach (var segment in figure.Segments)
        {
            switch (segment)
            {
                case PolyLineSegment poly: points.AddRange(poly.Points); break;
                case LineSegment line: points.Add(line.Point); break;
                case PathSegment seg: points.Add(new Point(double.NaN, double.NaN)); debug = seg.GetType().Name; break;
            }
        }

        var all = new List<Point> { figure.StartPoint };
        all.AddRange(points);
        debug = $"{figure.Segments.Count} 段 / {all.Count} 点 : "
                + string.Join(" ", all.Select(p => $"({p.X:0.#},{p.Y:0.#})")
                                      .Select(s => s));

        if (all.Count < 3) return "未知";

        var p0 = all[0];
        var p2 = all[^1];
        var vertex = all[1];

        if (transform != null)
        {
            var m = transform.Value;
            p0 = m.Transform(p0);
            p2 = m.Transform(p2);
            vertex = m.Transform(vertex);
        }

        var dx = vertex.X - (p0.X + p2.X) / 2;
        var dy = vertex.Y - (p0.Y + p2.Y) / 2;

        if (Math.Abs(dx) >= Math.Abs(dy)) return dx > 0 ? "朝右" : "朝左";
        return dy > 0 ? "朝下" : "朝上";
    }

    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && typed.Name == name) return typed;
            var found = FindDescendant<T>(child, name);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// 自检专用：向两路分析器注入合成频谱与电平，用于离线校验渲染链路。
    /// 每次注入一整窗（<see cref="SpectrumAnalyzer.FftSize"/>）再加一段：
    /// 2048 点 FFT 要攒够 2048 个样本才出第一帧频谱，注入太少会让自检截图里的频谱是空的。
    /// </summary>
    public void FeedSelfCheckSignal(float phase)
    {
        var frames = SpectrumAnalyzer.FftSize + 1024;
        var block = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            var t = (phase * 6f) + i / (float)AudioEngine.SampleRate;
            var value = 0.22f * MathF.Sin(2f * MathF.PI * 220f * t)
                        + 0.16f * MathF.Sin(2f * MathF.PI * 900f * t)
                        + 0.10f * MathF.Sin(2f * MathF.PI * 3400f * t)
                        + 0.05f * (float)(new Random(i).NextDouble() - 0.5);
            block[i] = value;
        }

        _engine.InputSpectrum.Feed(block);
        _engine.OutputSpectrum.Feed(block);
        _engine.InputSpectrum.TryUpdate();
        _engine.OutputSpectrum.TryUpdate();
    }

    // =============================================================== 顶部工具栏

    private void OnProcessingToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _config.AudioProcessingEnabled = ToggleProcessing.IsChecked == true;
        _engine.UpdateAllParameters();
        SaveConfig();

        // 三条开关路径（界面开关 / 全局快捷键 / 托盘菜单）都汇到本方法，
        // 因此音效只需在这里播放一处；_loading 守卫保证启动回填时不响。
        //
        // 音效优先从「监听设备」播放——那正是用户实际在听的设备；
        // 没配监听、或该设备已拔掉时，播放器自己会退回系统默认输出设备。
        // 注意：GetDevice 返回的是新建的 COM 对象，所有权交给播放器释放。
        MMDevice? soundTarget = null;
        if (!string.IsNullOrWhiteSpace(_config.Devices.MonitorDeviceId))
            soundTarget = _devices.GetDevice(_config.Devices.MonitorDeviceId, DataFlow.Render);
        ToggleSoundPlayer.Play(_config.AudioProcessingEnabled, soundTarget);
    }

    private void ToggleProcessing_Click()
        => ToggleProcessing.IsChecked = ToggleProcessing.IsChecked != true;

    private void OnMonitorToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _config.MonitorEnabled = ToggleMonitor.IsChecked == true;
        _engine.SetMonitorEnabled(_config.MonitorEnabled);
        SaveConfig();
    }

    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _config.AutoStart = ToggleAutoStart.IsChecked == true;
        if (!AutoStartService.Apply(_config.AutoStart))
            ShowStatus("写入开机自启注册表失败，请检查权限。", false);
        SaveConfig();
    }

    private void OnTrayToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _config.CloseToTray = ToggleCloseTray.IsChecked == true;
        SaveConfig();
    }

    /// <summary>
    /// 切换深色 / 浅色配色。
    /// 顺序讲究：先换配色（画刷都是 DynamicResource，换完自动生效），
    /// 再更新窗口边框的深浅，最后落配置。
    /// </summary>
    private void OnDarkModeToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _config.DarkMode = ToggleDarkMode.IsChecked == true;
        ApplyWindowMaterial();
        WindowEffects.SetImmersiveDarkMode(this, _config.DarkMode);
        SaveConfig();
    }

    private void OnGlobalHotkeyClick(object sender, RoutedEventArgs e)
    {
        // 正在为某个音频文件录入快捷键/按住键时，不要被工具栏按钮抢走录入状态
        if (_pendingHotkeyTrack != null || _pendingHoldKeyTrack != null)
        {
            ShowStatus("正在为该音频文件录入按键，请先按键（Esc 取消）。", false);
            return;
        }

        // 已经处于"等用户按键"的状态，再点一次就当作取消（与 Esc 等价）
        if (_pendingHotkeyGesture != null)
        {
            CancelHotkeyCapture();
            return;
        }

        _pendingHotkeyGesture = string.Empty;

        // 录入期间必须先注销旧的全局快捷键：否则按下的就是那个已注册的组合，
        // 系统会把它变成 WM_HOTKEY 投递（直接触发总开关），窗口根本收不到按键，
        // 想把它重新设成同一个键就做不到。
        if (!string.IsNullOrWhiteSpace(_config.ToggleHotkey) && _hotkeys.Unregister(_config.ToggleHotkey))
            Log.Info($"重新录入全局快捷键：旧热键 {_config.ToggleHotkey} 已临时注销。");

        SetHotkeyButtonText(RecordingPlaceholder);
        ShowStatus("正在为总开关录入全局快捷键：请按键（Esc 取消，Backspace 清除）", false);
        BeginKeyboardCapture();
    }

    /// <summary>
    /// WPF 的按键事件现在只当作**兜底**：
    /// 正常路径是 <see cref="ShortcutKeyCapture"/> 在窗口过程里读物理按键（不受输入法影响）。
    /// 只有在它没能消费按键时（例如 HwndSource 钩子安装失败），才会走到这里。
    ///
    /// 历史上这里是唯一的实现，靠 <c>CharacterFromVirtualKey</c> 之类的手段
    /// 从已经被输入法污染的 <c>Key</c> 里往回猜，因此时好时坏。
    /// </summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsRecordingShortcut()) return;
        if (_capture.IsCapturing) return;   // 已由消息钩子处理

        // 兜底路径：至少把按键吞掉，别让它跑去点什么
        e.Handled = true;
        Log.Warn($"快捷键录入：消息钩子没有接管按键（WPF Key={e.Key}），走兜底路径。");
    }

    /// <summary>最近一次 TextInput 收到的字符（仅用于诊断日志）。</summary>
    private string _lastInputCharacter = string.Empty;

    private void OnWindowTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        _lastInputCharacter = e.Text;

        // 录入快捷键期间不要把这些字符送进任何输入框
        if (IsRecordingShortcut()) e.Handled = true;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // 快捷键录入统一由 ShortcutKeyCapture（窗口过程钩子）处理
    }

    /// <summary>当前是否处于"等待用户按下快捷键"的状态。</summary>
    private bool IsRecordingShortcut()
        => _pendingHotkeyGesture != null || _pendingHotkeyTrack != null || _pendingHoldKeyTrack != null;

    private void CancelHotkeyCapture()
    {
        // 取消录入时要把"刚才临时注销掉的旧热键"装回去，否则它会一直失效到下次重启：
        // 编辑音频条目时注销的是该条目的热键；编辑全局快捷键时注销的是总开关热键。
        var restore = _pendingHotkeyTrack != null ||
                      (_pendingHotkeyGesture != null && !string.IsNullOrWhiteSpace(_config.ToggleHotkey));

        _capture.End();
        ClearRecordingIndicators();
        HideStatus();

        if (restore) RegisterConfiguredHotkeys();
    }
    /// <summary>
    /// 清掉"正在录入"的显示状态：恢复工具栏全局快捷键按钮的文字，并让列表里那个
    /// 正在录入的条目退出录入态（按钮文字变回它自己的键位）。
    /// </summary>
    private void ClearRecordingIndicators()
    {
        if (_pendingHotkeyTrack != null) _pendingHotkeyTrack.IsRecordingHotkey = false;
        if (_pendingHoldKeyTrack != null) _pendingHoldKeyTrack.IsRecordingHoldKey = false;

        _pendingHotkeyGesture = null;
        _pendingHotkeyTrack = null;
        _pendingHoldKeyTrack = null;
        SetHotkeyButtonText(_config.ToggleHotkey);
    }

    /// <summary>
    /// 进入"等待按键"状态时的统一处理。三条录入路径（总开关 / 音频热键 / 同步按住键）
    /// **必须**都走这里，免得某一条漏掉某一步——早期就出过"音频热键那条好了、
    /// 同步按住键那条还被输入法拦截"的问题。
    ///
    /// 实际读键由 <see cref="ShortcutKeyCapture"/> 在窗口过程钩子里完成，
    /// 这里只负责：把输入法按住、把键盘焦点从按钮上挪开。
    /// </summary>
    private void BeginKeyboardCapture()
    {
        // 让 WPF 的输入管理器也按"没有输入法"来处理
        InputMethod.SetIsInputMethodEnabled(this, false);

        // 焦点交给窗口而不是某个按钮：否则空格/回车会把按钮"点"一下
        Focus();
        Keyboard.Focus(this);

        // 消息钩子 + 输入法抑制；拿到键或取消都会回调 ApplyHotkey / CancelHotkeyCapture
        _capture.Begin(ApplyHotkey, CancelHotkeyCapture);
    }

    private void ApplyHotkey(string gesture)
    {
        // 录入通路在回调**之前**已经 End 了；这里再叫一次是幂等的，
        // 用于覆盖"键盘之外"调用本方法的情况（例如代码里直接清除快捷键）。
        _capture.End();

        // 「同步按住键」录入：只接受**单个按键**（按住键说话本来就是一个键）
        if (_pendingHoldKeyTrack != null)
        {
            var track = _pendingHoldKeyTrack;
            CancelHotkeyCapture();

            if (string.IsNullOrWhiteSpace(gesture))
            {
                track.HoldKey = string.Empty;
                Log.Info($"已清除「{track.DisplayName}」的同步按住键。");
            }
            else
            {
                if (gesture.Contains('+'))
                {
                    ShowStatus("「同步按住键」只支持单个按键（不要带 Ctrl/Alt/Shift）。", false);
                    return;
                }

                // 同步按住键允许在不同音频之间重复，但不能撞上任何一个全局热键
                var conflict = DescribeConflict(gesture, track, GestureKind.HoldKey);
                if (conflict != null)
                {
                    ShowStatus(conflict, false);
                    return;
                }

                track.HoldKey = gesture;
                Log.Info($"已为「{track.DisplayName}」设置同步按住键 {gesture}");
            }

            SaveConfig();
            return;
        }

        if (_pendingHotkeyTrack != null)
        {
            var track = _pendingHotkeyTrack;
            CancelHotkeyCapture();

            if (string.IsNullOrWhiteSpace(gesture))
            {
                track.Hotkey = string.Empty;
            }
            else
            {
                var (ok, error) = RegisterTrackHotkey(track, gesture);
                if (!ok)
                {
                    ShowStatus(error!, false);
                    return;
                }
            }

            // 关键：改完条目热键必须**重建全局热键注册**。
            // 早期这里只存了配置，结果系统里注册的仍是旧键：按新键没反应，
            // 按旧键才会真的播出去（用户反馈的"设了快捷键后按它不播放"）。
            RegisterConfiguredHotkeys();
            SaveConfig();
            RefreshTrackHighlight();
            return;
        }

        // 总开关这一路：空手势 = 用户按了 Backspace / Delete，语义是**清除**，不是"取消"。
        //
        // 早期这里直接 `if (IsNullOrWhiteSpace) return;`，于是空串被丢掉、
        // 而 CancelHotkeyCapture() 里的 restore 判断又发现 _config.ToggleHotkey 还是旧值，
        // 就把旧热键原样装了回去 —— 表现成"按 Backspace 和按 Esc 一模一样"（用户反馈的 bug）。
        //
        // 顺序讲究：**先把配置清空**再收尾，CancelHotkeyCapture() 里的 restore 判断
        // 才会因为"配置里已经没有热键"而不去重新注册旧键；随后这一次
        // RegisterConfiguredHotkeys() 会 UnregisterAll 再全量注册，旧热键就此真正注销。
        if (string.IsNullOrWhiteSpace(gesture))
        {
            _config.ToggleHotkey = string.Empty;
            CancelHotkeyCapture();
            RegisterConfiguredHotkeys();
            SaveConfig();
            Log.Info("已清除「音频处理」总开关的全局快捷键。");
            return;
        }

        CancelHotkeyCapture();
        _config.ToggleHotkey = gesture;
        SetHotkeyButtonText(gesture);
        RegisterConfiguredHotkeys();
        SaveConfig();
    }

    // =============================================================== 设备

    private void OnDeviceDropDownOpened(object sender, EventArgs e) => RefreshDeviceLists(sender as ComboBox);

    /// <summary>
    /// 设备列表发生变化（热插拔 / 默认设备变化）。
    ///
    /// 去抖是必须的：插拔一个 USB 麦克风，系统会连着发好几条通知
    /// （DeviceRemoved + DeviceStateChanged + DefaultDeviceChanged…），
    /// 每次都重建音频流会听到连续的爆音。这里等 350 ms 没有新通知了再处理一次。
    /// </summary>
    private void OnDevicesChanged()
    {
        _deviceChangeDebounce ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        _deviceChangeDebounce.Stop();
        _deviceChangeDebounce.Tick -= OnDeviceChangeDebounceTick;
        _deviceChangeDebounce.Tick += OnDeviceChangeDebounceTick;
        _deviceChangeDebounce.Start();
    }

    private void OnDeviceChangeDebounceTick(object? sender, EventArgs e)
    {
        _deviceChangeDebounce?.Stop();

        // 通知与本机轮询都会走这里；只有轮询能确认"设备集合确实变了"，
        // 通知本身不带这个信息，因此取两者的并集（有一个说是变化就按变化处理）。
        var changed = _pendingDeviceSetChanged;
        _pendingDeviceSetChanged = false;
        ProcessDeviceChange(changed);
    }

    /// <summary>
    /// 轮询兜底：**不依赖系统通知**。
    ///
    /// 为什么必须有它：实测本机环境里 <c>IMMNotificationClient</c> 的回调
    /// 一条都收不到（连主动切换系统默认设备都没有回调，MTA/STA 都一样），
    /// 于是"拔掉再插回"永远不会触发任何自动恢复动作——
    /// 这正是用户反复反馈"热插拔没用、必须手动重选设备"的根因。
    ///
    /// 两条腿走路：系统通知来了就快速响应（350ms 去抖），
    /// 通知不来则由这个 1.5 秒的轻量轮询发现变化。
    /// 轮询只做"枚举 + 比对设备 ID 指纹"，不做任何重建，开销可以忽略。
    /// </summary>
    private void OnDevicePollTick(object? sender, EventArgs e)
    {
        if (_exiting || _loading) return;

        try
        {
            var fingerprint = BuildDeviceFingerprint();
            if (fingerprint == _lastDeviceFingerprint) return;

            Log.Info("设备轮询发现变化（系统通知未到达），按设备变更处理。");
            _lastDeviceFingerprint = fingerprint;
            _pendingDeviceSetChanged = true;
            OnDevicesChanged();
        }
        catch (Exception ex)
        {
            Log.Debug("设备轮询失败（忽略）：" + ex.Message);
        }
    }

    /// <summary>把输入与输出设备集合的 ID 排序拼成一个指纹串，用于比对是否发生变化。</summary>
    private string BuildDeviceFingerprint()
    {
        var ids = new List<string>();
        ids.AddRange(_devices.Enumerate(DataFlow.Capture).Select(d => "c:" + d.Id));
        ids.AddRange(_devices.Enumerate(DataFlow.Render).Select(d => "r:" + d.Id));
        ids.Sort(StringComparer.OrdinalIgnoreCase);
        return string.Join(",", ids);
    }

    /// <summary>
    /// 热插拔处理：让引擎把音频流对齐到"现在插着的设备"，再刷新界面。
    /// 顺序不能反——引擎先恢复好，界面的下拉框才能显示设备已经回到首选那一个。
    /// </summary>
    /// <param name="deviceSetChanged">设备集合是否真的变了（轮询/通知的结论）。</param>
    private void ProcessDeviceChange(bool deviceSetChanged)
    {
        if (_exiting) return;

        try
        {
            _engine.RecoverDevices(deviceSetChanged);
        }
        catch (Exception ex)
        {
            Log.Error("处理设备变化失败", ex);
        }

        RefreshDeviceLists();

        // 首选设备插回来了：把下拉框选回那一个
        RestoreSelectionIfPresent(InputDeviceCombo, _config.Devices.InputDeviceId);
        RestoreSelectionIfPresent(OutputDeviceCombo, _config.Devices.OutputDeviceId);
        RestoreSelectionIfPresent(MonitorDeviceCombo, _config.Devices.MonitorDeviceId);

        // 设备 ID 可能已被引擎按名字兜底纠正过，落盘保存
        SaveConfig();
        ReportDeviceState();
    }

    /// <summary>
    /// 把下拉框选回指定设备（仅当该设备确实插着时）。用于热插拔后自动恢复选择。
    /// </summary>
    private void RestoreSelectionIfPresent(ComboBox combo, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        if (!_devices.IsPresent(deviceId, DeviceFlowOf(combo))) return;
        if (combo.SelectedItem == null ||
            !string.Equals(SelectedDeviceId(combo), deviceId, StringComparison.OrdinalIgnoreCase))
        {
            SelectDeviceById(combo, deviceId);
        }
    }

    /// <summary>设备下拉框对应的数据流方向。</summary>
    private DataFlow DeviceFlowOf(ComboBox combo)
        => ReferenceEquals(combo, InputDeviceCombo) ? DataFlow.Capture : DataFlow.Render;

    /// <summary>在设备栏里把指定 ID 选中（找不到就什么都不做）。</summary>
    private void SelectDeviceById(ComboBox combo, string deviceId)
    {
        var index = combo.Items
            .OfType<ComboBoxItem>()
            .Select((item, i) => (item, i))
            .Where(pair => pair.item.Tag is AudioDevice device &&
                           string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.i)
            .DefaultIfEmpty(-1)
            .First();

        if (index < 0) return;

        _suppressDeviceSelection = true;
        try
        {
            combo.SelectedIndex = index;
            UpdateDeviceComboTooltip(combo);
        }
        finally
        {
            _suppressDeviceSelection = false;
        }
    }

    /// <summary>
    /// 每次设备变化后写一条日志。
    ///
    /// **刻意不做任何用户可见提示**（用户明确要求）：热插拔是自动过程，
    /// 掉线期间用系统默认设备顶着、插回自动切回，不该弹黄条或顶栏徽章打扰。
    /// 排查问题时看日志即可，里面有"当前用的哪台设备"的完整记录。
    /// </summary>
    private void ReportDeviceState()
    {
        var inputMissing = !string.IsNullOrWhiteSpace(_config.Devices.InputDeviceId) &&
                           !_devices.IsPresent(_config.Devices.InputDeviceId, DataFlow.Capture);

        Log.Info($"设备列表已更新：输入「{_devices.DescribeName(_config.Devices.InputDeviceId, DataFlow.Capture)}」" +
                 $"→ 当前使用「{_engine.ActualInputName ?? "无"}」" +
                 $"，输出「{_devices.DescribeName(_config.Devices.OutputDeviceId, DataFlow.Render)}」" +
                 $"，监听「{_devices.DescribeName(_config.Devices.MonitorDeviceId, DataFlow.Render)}」" +
                 $"，流运行中={_engine.IsRunning}，首选缺失={inputMissing}");

        // 设备变化可能让"未检测到 MIXLINE"的前提也变了，顺手刷新一下状态条
        UpdateMixLineStatus();
    }

    private void RefreshDeviceLists(ComboBox? only = null)
    {
        if (only == null || ReferenceEquals(only, InputDeviceCombo))
            FillDeviceCombo(InputDeviceCombo, _devices.Enumerate(DataFlow.Capture), _config.Devices.InputDeviceId);

        if (only == null || ReferenceEquals(only, OutputDeviceCombo))
            FillDeviceCombo(OutputDeviceCombo, _devices.Enumerate(DataFlow.Render), _config.Devices.OutputDeviceId);

        if (only == null || ReferenceEquals(only, MonitorDeviceCombo))
            FillDeviceCombo(MonitorDeviceCombo, _devices.Enumerate(DataFlow.Render), _config.Devices.MonitorDeviceId);
    }

    private void FillDeviceCombo(ComboBox combo, IReadOnlyList<AudioDevice> devices, string? selectedId)
    {
        var previous = _loading;
        _loading = true;
        try
        {
            combo.Items.Clear();
            var itemStyle = (Style)FindResource("FlatComboItem");
            foreach (var device in devices)
            {
                combo.Items.Add(new ComboBoxItem
                {
                    // 刻意**不标**"（默认）"：这里的"默认"指**操作系统**的默认设备。
                    // 按 MateMic 的正确用法，Windows 的默认录音设备应该被设成 MIXLINE Stream
                    // （好让游戏/Discord 拿到处理后的话筒），而软件自己的「输入」必须选**物理麦克风**。
                    // 把系统默认标在列表里只会把用户往错误选项上带，因此不显示
                    // （用户明确要求；诊断工具里仍保留默认标记，那是用来排查的）。
                    Content = device.Name,
                    Tag = device,
                    Style = itemStyle,
                    ToolTip = device.Name,
                });
            }

            var index = devices
                .Select((device, i) => (device, i))
                .Where(pair => string.Equals(pair.device.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.i)
                .DefaultIfEmpty(-1)
                .First();

            if (index < 0)
            {
                // 两种情况都落到这里：
                //   · 配置里选好的设备被拔掉了（当前不可用）；
                //   · **首次安装，三个设备都还没选过**。
                // 后者刻意不做任何自动选择：用户实际在用哪支麦克风/哪个扬声器，
                // 软件是猜不出来的（本机枚举结果里第一个经常是虚拟声卡 CABLE Output），
                // 猜错比不选更糟——用户会以为已经配好了，实际录到的是别的东西。
                // 因此就停在这里，等用户自己从下拉框里选。
                combo.SelectedIndex = -1;
                combo.ToolTip = string.IsNullOrWhiteSpace(selectedId)
                    ? "尚未选择设备，请从列表中选择"
                    : _devices.DescribeName(selectedId, DeviceFlowOf(combo)) + " —— 当前不可用，插回后会自动恢复";
                return;
            }

            combo.SelectedIndex = index;
            UpdateDeviceComboTooltip(combo);
        }
        finally
        {
            _loading = previous;
        }
    }

    /// <summary>
    /// 设备下拉框的悬浮提示 = 当前设备的完整名称。
    /// 设备名可能很长（"Realtek Digital Output (Realtek High Definition Audio)" 有 300 DIP 宽），
    /// 框再宽也不能保证全都放得下，悬停能看到全名才算交代清楚。
    /// </summary>
    private static void UpdateDeviceComboTooltip(ComboBox combo)
    {
        combo.ToolTip = (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? combo.ToolTip;
    }

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 换设备时刷新悬浮提示（显示新设备的完整名称）
        if (sender is ComboBox changed) UpdateDeviceComboTooltip(changed);
        if (_loading) return;

        // 热插拔后我们把下拉框选回首选设备，这不算"用户改了设备"：
        // 配置本来就没变，重入这里只会白重启一次音频流。
        if (_suppressDeviceSelection) return;

        var inputId = SelectedDeviceId(InputDeviceCombo);
        var outputId = SelectedDeviceId(OutputDeviceCombo);
        var monitorId = SelectedDeviceId(MonitorDeviceCombo);

        if (inputId == _config.Devices.InputDeviceId &&
            outputId == _config.Devices.OutputDeviceId &&
            monitorId == _config.Devices.MonitorDeviceId)
            return;

        Log.Info("设备发生切换，正在重启音频流…");
        _engine.Reconfigure(inputId, outputId, monitorId);

        // 只在"切换失败"时提示（这是用户主动操作、失败必须让他知道）。
        // 设备掉线/降级这类自动过程不弹提示，只写日志——见 ReportDeviceState。
        if (!_engine.IsRunning && _engine.LastError != null)
            ShowStatus("音频设备切换失败：" + _engine.LastError, false);

        UpdateMixLineStatus();
        SaveConfig();
    }

    private static string? SelectedDeviceId(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag is AudioDevice device ? device.Id : null;

    private void UpdateMixLineStatus()
    {
        if (_devices.HasMixLineDevice())
        {
            HideStatus();
            return;
        }

        ShowStatus("未检测到 MIXLINE，请先安装并配置 MIXLINE。", true);
    }

    // =============================================================== 左侧处理链

    // 每个模块的展开状态。箭头模板通过 Button.Tag 绑定这些属性来切换朝向
    // （True = 已展开，箭头朝下；False = 已收起，箭头朝左）。
    private bool _gateExpanded;
    private bool _denoiseExpanded;
    private bool _loudnessExpanded;
    private bool _toneExpanded;
    private bool _effectExpanded;
    private bool _gainExpanded;

    public bool GateExpanded { get => _gateExpanded; private set => SetExpanded(ref _gateExpanded, value, nameof(GateExpanded)); }
    public bool DenoiseExpanded { get => _denoiseExpanded; private set => SetExpanded(ref _denoiseExpanded, value, nameof(DenoiseExpanded)); }
    public bool LoudnessExpanded { get => _loudnessExpanded; private set => SetExpanded(ref _loudnessExpanded, value, nameof(LoudnessExpanded)); }
    public bool ToneExpanded { get => _toneExpanded; private set => SetExpanded(ref _toneExpanded, value, nameof(ToneExpanded)); }
    public bool EffectExpanded { get => _effectExpanded; private set => SetExpanded(ref _effectExpanded, value, nameof(EffectExpanded)); }
    public bool GainExpanded { get => _gainExpanded; private set => SetExpanded(ref _gainExpanded, value, nameof(GainExpanded)); }

    private bool _voiceChangerExpanded;

    private bool _aiVoiceExpanded;
    /// <summary>变声模块是否展开。</summary>
    public bool VoiceChangerExpanded { get => _voiceChangerExpanded; private set => SetExpanded(ref _voiceChangerExpanded, value, nameof(VoiceChangerExpanded)); }

    public bool AiVoiceExpanded { get => _aiVoiceExpanded; private set => SetExpanded(ref _aiVoiceExpanded, value, nameof(AiVoiceExpanded)); }

    private void SetExpanded(ref bool field, bool value, string propertyName)
    {
        if (field == value) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    /// <summary>折叠/展开模块内容。箭头朝向由 Tag 绑定自动跟随，无需手动旋转。</summary>
    private void OnExpandClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        // 注意：Button.Tag 已用于绑定展开状态，因此这里按控件名识别模块
        UIElement? panel = button.Name switch
        {
            "ExpandGate" => PanelGate,
            "ExpandDenoise" => PanelDenoise,
            "ExpandLoudness" => PanelLoudness,
            "ExpandTone" => PanelTone,
            "ExpandEffect" => PanelCreative,
            "ExpandGain" => PanelGain,
            "ExpandVoiceChanger" => PanelVoiceChanger,
            "ExpandAiVoice" => PanelAiVoice,
            _ => null,
        };

        if (panel == null) return;

        var expanded = panel.Visibility != Visibility.Visible;
        AnimatePanel(button, panel, expanded);

        switch (button.Name)
        {
            case "ExpandGate":
                GateExpanded = expanded;
                _config.Panels.Gate = expanded;
                break;
            case "ExpandDenoise":
                DenoiseExpanded = expanded;
                _config.Panels.Denoise = expanded;
                break;
            case "ExpandLoudness":
                LoudnessExpanded = expanded;
                _config.Panels.Loudness = expanded;
                break;
            case "ExpandTone":
                ToneExpanded = expanded;
                _config.Panels.Tone = expanded;
                break;
            case "ExpandEffect":
                EffectExpanded = expanded;
                _config.Panels.Effect = expanded;
                break;
            case "ExpandGain":
                GainExpanded = expanded;
                _config.Panels.Gain = expanded;
                break;
            case "ExpandVoiceChanger":
                VoiceChangerExpanded = expanded;
                _config.Panels.VoiceChanger = expanded;
                break;
            case "ExpandAiVoice":
                AiVoiceExpanded = expanded;
                _config.Panels.AiVoice = expanded;
                break;
        }

        SaveConfig();   // 展开状态也持久化，下次启动保持原样
    }

    /// <summary>正在播放展开/收起动画的模块（按触发按钮区分）；重复点击时先让上一段动画收尾。</summary>
    private readonly Dictionary<Button, (FrameworkElement Panel, Storyboard Story)> _panelAnimations = new();

    /// <summary>
    /// 卡片展开/收起的动画（2026-10-04）。
    ///
    /// 做法是动画面板的 <see cref="FrameworkElement.Height"/>（0 ↔ 自然高度），**不是缩放**：
    /// ScaleTransform 只作用于渲染层，布局空间会瞬间让出/占满，兄弟卡片会"跳"一下，反而更难看。
    ///
    /// 自然高度取"收尾态量测"：先按目标状态摆好可见性、再把 Height 置为 NaN 让布局算出真实尺寸，
    /// 读 DesiredSize 后立刻把 Height 固定回 0 并恢复可见性——这样量测不会闪一下。
    /// </summary>
    private void AnimatePanel(Button button, UIElement panelElement, bool expand)
    {
        if (panelElement is not FrameworkElement panel) return;

        // 上一段动画还没播完：立刻收尾（跳到最后状态），避免两段动画互相覆盖留下错误高度
        if (_panelAnimations.TryGetValue(button, out var running))
        {
            running.Story.Remove();
            running.Panel.Height = double.NaN;
            _panelAnimations.Remove(button);
        }

        // ---- 量测自然高度 ----
        //
        // ⚠ 这里是"**收起后再展开只长到几像素（按钮被压成一条）**"的根因
        //   —— 已用 --measureprobe 逐策略验证。
        //
        //   收起动画结束后虽然写了 `panel.Height = double.NaN`，但**动画时钟并没有被移除**，
        //   它继续把 Height 钉在动画终值 0 上（赋 NaN 会被动画时钟覆盖，等于没生效）。
        //   于是量这个元素时它的高度是 0 ⇒ 内容量测高度全被压成 0 ⇒ DesiredSize 只剩 Margin
        //   （实测 ToneGrid 得到 6.4 = Margin 6），动画目标高度就是这个 6.4，
        //   展开只长 6px，看起来就是被压扁的一条。
        //
        //   因此量测前必须 **显式移除动画时钟**：BeginAnimation(HeightProperty, null)。
        //   实测：移除后 DesiredSize=29.6、UpdateLayout 后 ActualHeight=23.2（正确值）。
        var wasVisible = panel.Visibility == Visibility.Visible;
        panel.BeginAnimation(FrameworkElement.HeightProperty, null);   // ★ 关键：摘掉动画时钟
        panel.ClearValue(FrameworkElement.HeightProperty);             // 清掉残留的本地值
        panel.Visibility = Visibility.Visible;
        panel.InvalidateMeasure();
        for (var node = VisualTreeHelper.GetParent(panel); node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement ui) ui.InvalidateMeasure();
        }
        panel.UpdateLayout();

        var target = panel.ActualHeight;
        var source = "ActualHeight";
        if (double.IsNaN(target) || target <= 0.5)
        {
            target = panel.DesiredSize.Height;
            source = "DesiredSize";
        }
        if (double.IsNaN(target) || target <= 0.5)
        {
            // 极端兜底：以"无限高度"单独量一次（DesiredSize 已含自身 Margin，不再重复相加）
            panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            target = panel.DesiredSize.Height;
            source = "无限量测";
        }
        if (double.IsNaN(target) || target <= 0.5)
        {
            target = 1;
            source = "兜底1px";
            // 这条说明量测彻底失败，展开必然不满；打进日志便于以后一眼看出
            Log.Info($"[展开动画] {button.Name}：自然高度量测失败，退回 1px"
                     + $"（ActualHeight={panel.ActualHeight:0.#} DesiredSize={panel.DesiredSize.Height:0.#}"
                     + $" Height属性={(double.IsNaN(panel.Height) ? "NaN" : panel.Height.ToString("0.#"))}）");
        }

        if (IsSelfCheckMode)
        {
            Log.Info($"[展开动画] {button.Name}：目标高={target:0.#}（来源 {source}）"
                     + $"｜量测前可见={wasVisible}｜ActualHeight={panel.ActualHeight:0.#}"
                     + $" DesiredSize={panel.DesiredSize.Height:0.#}");
        }

        panel.Height = 0;                                    // 回到动画起点（此时还没画到屏幕上）
        panel.Visibility = wasVisible ? Visibility.Visible : Visibility.Collapsed;
        panel.UpdateLayout();

        var story = new Storyboard();

        if (expand)
        {
            panel.Visibility = Visibility.Visible;
            var height = new DoubleAnimation(0, target, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(height, panel);
            Storyboard.SetTargetProperty(height, new PropertyPath(nameof(FrameworkElement.Height)));
            story.Children.Add(height);

            // 动画结束：交还给布局（Height 恢复自动），否则窗口变化后高度会被钉死
            story.Completed += (_, _) =>
            {
                panel.Height = double.NaN;
                _panelAnimations.Remove(button);
            };
        }
        else
        {
            var height = new DoubleAnimation(target, 0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };
            Storyboard.SetTarget(height, panel);
            Storyboard.SetTargetProperty(height, new PropertyPath(nameof(FrameworkElement.Height)));
            story.Children.Add(height);

            story.Completed += (_, _) =>
            {
                panel.Visibility = Visibility.Collapsed;
                panel.Height = double.NaN;                   // 收起后必须复位，否则下次量测拿到 0
                _panelAnimations.Remove(button);
            };
        }

        _panelAnimations[button] = (panel, story);
        story.Begin();
    }

    /// <summary>把配置里记录的展开状态应用到各模块面板与箭头。</summary>
    private void SyncExpanderArrows()
    {
        void Apply(UIElement panel, bool expanded)
            => panel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;

        Apply(PanelGate, _config.Panels.Gate);
        Apply(PanelDenoise, _config.Panels.Denoise);
        Apply(PanelLoudness, _config.Panels.Loudness);
        Apply(PanelTone, _config.Panels.Tone);
        Apply(PanelCreative, _config.Panels.Effect);
        Apply(PanelGain, _config.Panels.Gain);
        Apply(PanelVoiceChanger, _config.Panels.VoiceChanger);
        Apply(PanelAiVoice, _config.Panels.AiVoice);

        // 箭头朝向完全由 XAML 里的 Tag 绑定驱动（见 Ui/Theme.xaml 的 ExpanderButton）。
        // 这里**不能**再写 arrow.Tag = true：给已有 OneWay 绑定的依赖属性赋局部值会
        // 直接把绑定顶掉，之后箭头就再也不跟随展开状态了。
        GateExpanded = _config.Panels.Gate;
        DenoiseExpanded = _config.Panels.Denoise;
        LoudnessExpanded = _config.Panels.Loudness;
        ToneExpanded = _config.Panels.Tone;
        EffectExpanded = _config.Panels.Effect;
        GainExpanded = _config.Panels.Gain;
        VoiceChangerExpanded = _config.Panels.VoiceChanger;
        AiVoiceExpanded = _config.Panels.AiVoice;

        // AI 变声界面初值。功能尚未接线（本轮只做界面），但控件状态先与配置一致，
        // 免得将来接线时两边对不上。
        AiVoicePitchSlider.Value = _config.AiVoice.Semitones;
        AiVoiceIndexRateSlider.Value = _config.AiVoice.IndexRate;
        AiVoiceBlockSlider.Value = _config.AiVoice.BlockMs;
        AiVoiceContextSlider.Value = _config.AiVoice.ContextMs;
        AiVoiceCrossfadeSlider.Value = _config.AiVoice.CrossfadeMs;

        // ⚠ 必须在这里扫一次音色/索引目录：原先只有点过「添加 AI 变声」之后才调用
        // RefreshAiVoiceLists()，于是**启动后音色下拉框一直是空的**，
        // 而配置里其实存着选择——用户看到"没选模型却能打开功能"（2026-10-07 反馈）。
        RefreshAiVoiceLists();
        UpdateAiVoiceIndexAvailability();
        UpdateAiVoiceControlAvailability();      // 若配置里 AI 变声是开着的，相应控件应为禁用态
    }

    /// <summary>
    /// 没有加载索引时，"索引占比"**整行**置灰——标签、滑条、数值一起变淡。
    /// （只灰滑条会显得那一行半死不活，整行一致更整齐。）
    /// </summary>
    private void UpdateAiVoiceIndexAvailability()
    {
        if (AiVoiceIndexRateRow == null) return;
        var hasIndex = AiVoiceIndexCombo?.SelectedItem is AiVoiceIndexItem;
        AiVoiceIndexRateRow.IsEnabled = hasIndex;
        AiVoiceIndexRateRow.Opacity = hasIndex ? 1.0 : 0.4;
    }

    private void OnEffectToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _config.NoiseGate.Enabled = ToggleGate.IsChecked == true;
        _config.Denoise.Enabled = ToggleDenoise.IsChecked == true;
        _config.Loudness.Enabled = ToggleLoudness.IsChecked == true;
        _config.Tone.Enabled = ToggleTone.IsChecked == true;
        _config.Effect.Enabled = ToggleEffect.IsChecked == true;
        _config.Gain.Enabled = ToggleGain.IsChecked == true;
        _config.VoiceChanger.Enabled = ToggleVoiceChanger.IsChecked == true;
        _config.AiVoice.Enabled = ToggleAiVoice.IsChecked == true;

        // 开着 AI 变声时禁用"会重建引擎"的控件（见 UpdateAiVoiceControlAvailability）。
        // 关闭 AI 变声的路径走这里；开启的路径由 OnAiVoiceTogglePreview 拦住并单独处理。
        RefreshAiVoiceControlAvailability();

        _engine.UpdateAllParameters();
        SaveConfig();
    }

    /// <summary>
    /// 按"是否需要重建引擎"来启停 AI 变声卡片里的控件。
    ///
    /// **理由（2026-10-08 用户要求）**：音频块 / 上下文 / 交叉淡化 / 音色 / 索引
    /// 每变一次都会重建引擎（约 1.5 秒）。与其做防抖、让用户拖动后等半秒才生效，
    /// 不如**开着 AI 变声时直接禁用这些控件**，要调就先把 AI 变声关掉——
    /// 用户明确、没有等待、也不会误触。
    ///
    /// **不受影响**的：变调与索引占比走热更新（只影响每块计算，不动缓冲几何），
    /// 开着也能实时调。
    /// </summary>
    private void UpdateAiVoiceControlAvailability()
    {
        var running = _config.AiVoice.Enabled;

        // 会重建引擎的：开着 AI 变声时禁用
        foreach (var control in new System.Windows.FrameworkElement[]
                 {
                     AiVoiceBlockSlider, AiVoiceContextSlider, AiVoiceCrossfadeSlider,
                     AiVoiceModelCombo, AiVoiceIndexCombo,
                 })
        {
            control.IsEnabled = !running;
        }

        // 走热更新的：保持可用
        AiVoicePitchSlider.IsEnabled = true;
        AiVoiceIndexRateRow.IsEnabled = !running || _config.AiVoice.IndexFile is { Length: > 0 };
    }

    /// <summary>更新「AI 变声」开关上的加载进度填充条。
    ///
    /// 改的是**裁剪矩形的宽度**而不是填充条自身的宽度：
    /// 填充条宽度恒为轨道全长 38、圆角 10，所以形状永远是完整跑道形。
    /// 若改宽度，进度小时圆角画不出来会成一条竖线、稍大又是一个椭圆 —— 两种都被用户看到过
    ///（2026-10-08）。
    /// </summary>
    private void UpdateAiVoiceLoading(bool loading, int percent)
    {
        ToggleAiVoice.ApplyTemplate();
        if (ToggleAiVoice.Template?.FindName("ProgressFill", ToggleAiVoice) is not Border fill) return;
        if (ToggleAiVoice.Template?.FindName("ProgressClip", ToggleAiVoice) is not RectangleGeometry clip) return;

        fill.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        var target = loading ? 38.0 * Math.Clamp(percent, 0, 100) / 100.0 : 0.0;
        clip.BeginAnimation(RectangleGeometry.RectProperty,
            new RectAnimation(new Rect(0, 0, target, 20), TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        if (!loading)
        {
            clip.BeginAnimation(RectangleGeometry.RectProperty, null);
            clip.Rect = new Rect(0, 0, 0, 20);
        }
    }

    /// <summary>
    /// 「AI 变声」开关的前置拦截。
    ///
    /// 三个作用：
    ///   ① 组件/音色不齐备时直接拦住（不留动画残影）；
    ///   ② 开启时**先不切换开关**，只启动后台加载——加载进度显示在开关上；
    ///   ③ 加载完成后由 LoadingChanged 把它真正打开（旋钮再滑过去）。
    /// 这样"填充"与"滑动"两段动画是**串行**的，不会并列播放（2026-10-08 用户反馈）。
    /// </summary>
    private void OnAiVoiceTogglePreview(object sender, MouseButtonEventArgs e)
    {
        if (_loading) return;

        // 关闭：永远允许
        if (ToggleAiVoice.IsChecked == true) return;

        if (!_engine.AiVoice.Ready(out var reason))
        {
            e.Handled = true;
            ShowStatus("AI 变声暂不可用：" + reason, false);
            return;
        }

        // 开启：拦住默认切换，改为"先加载、完成后再打开"
        e.Handled = true;
        _config.AiVoice.Enabled = true;
        RefreshAiVoiceControlAvailability();     // 立刻禁用会重建引擎的控件
        _engine.UpdateAllParameters();          // 触发后台加载，进度显示在开关上
        SaveConfig();
    }

    /// <summary>在不再触发一轮保存/建链的前提下把开关置位（加载完成后真正打开）。</summary>
    private void SetAiVoiceSwitchWithoutReentry(bool value)
    {
        if (ToggleAiVoice.IsChecked == value) return;
        _loading = true;
        try { ToggleAiVoice.IsChecked = value; }
        finally { _loading = false; }
        RefreshAiVoiceControlAvailability();
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || sender is not Slider slider) return;
        var value = (float)e.NewValue;

        // 这几项会改变缓冲几何 / 模型 ⇒ 命中参数指纹 ⇒ 引擎重建（约 1.5 秒）。
        // 必须防抖，否则拖动经过的每个数值都发起一次重建，直接把软件拖死。
        var needsRebuild = false;

        switch (slider.Tag as string)
        {
            case "GateThreshold":
                _config.NoiseGate.ThresholdDb = value;
                break;
            case "GateRelease":
                _config.NoiseGate.ReleaseMs = value;
                break;
            case "DenoiseStrength":
                _config.Denoise.Strength = value;
                break;
            case "DenoiseWet":
                _config.Denoise.Wet = value;
                break;
            case "LoudnessTarget":
                _config.Loudness.TargetLufs = value;
                break;
            case "LoudnessSpeed":
                _config.Loudness.Speed = (int)Math.Round(value);
                break;
            case "EffectAmount":
                _config.Effect.Amount = value;
                break;
            case "GainDb":
                _config.Gain.GainDb = value;
                break;
            case "VoiceSemitones":
                _config.VoiceChanger.Semitones = value;
                break;
            case "VoiceFormantSemitones":
                _config.VoiceChanger.FormantSemitones = value;
                break;
            case "VoiceGender":
                _config.VoiceChanger.GenderFactor = value;
                break;
            case "VoiceMix":
                _config.VoiceChanger.Mix = value;
                break;
            case "AiVoiceSemitones":
                _config.AiVoice.Semitones = value;
                break;
            case "AiVoiceIndexRate":
                _config.AiVoice.IndexRate = value;      // 只影响每块计算，热更新，不重建
                break;
            case "AiVoiceContextMs":
                _config.AiVoice.ContextMs = (int)Math.Round(value);
                needsRebuild = true;
                break;
            case "AiVoiceCrossfadeMs":
                _config.AiVoice.CrossfadeMs = (int)Math.Round(value);
                needsRebuild = true;
                break;
            default:
                return;
        }

        if (needsRebuild)
        {
            ScheduleAiVoiceApply();          // 停手 500ms 后才真正重建一次
        }
        else
        {
            _engine.UpdateAllParameters();
            ScheduleSave();
        }
    }

    /// <summary>AI 变声开关状态变化后，刷新卡片里控件的可用性（见 UpdateAiVoiceControlAvailability）。</summary>
    private void RefreshAiVoiceControlAvailability()
    {
        if (AiVoiceBlockSlider == null) return;
        UpdateAiVoiceControlAvailability();
    }

    /// <summary>
    /// AI 变声的"参数生效"防抖。
    ///
    /// ⚠ 为什么必须防抖：音频块 / 上下文 / 交叉淡化变化会让引擎**重建**（约 1.5 秒）。
    /// 若在 ValueChanged 里直接重建，拖动滑条经过的每一个数值都会发起一次重建，
    /// 几十次排队叠加会把软件卡死甚至闪退
    ///（2026-10-08 用户实测："上下文滑条跟随我鼠标拖动经过的每一个数值热重载，导致软件卡死闪退"）。
    /// 现在：拖动期间只更新配置与数值显示，停手 500ms 后才真正应用一次。
    /// </summary>
    private void ScheduleAiVoiceApply()
    {
        _aiVoiceApplyTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _aiVoiceApplyTimer.Stop();      // 重新计时：连续拖动期间始终不触发
        _aiVoiceApplyTimer.Tick -= OnAiVoiceApplyTick;
        _aiVoiceApplyTimer.Tick += OnAiVoiceApplyTick;
        _aiVoiceApplyTimer.Start();
        ScheduleSave();
    }

    private void OnAiVoiceApplyTick(object? sender, EventArgs e)
    {
        _aiVoiceApplyTimer?.Stop();
        _engine.UpdateAllParameters();
        SaveConfig();
    }

    private System.Windows.Threading.DispatcherTimer? _aiVoiceApplyTimer;

    /// <summary>窗口关闭时停掉待生效的计时器，避免它对着已释放的引擎调用。</summary>
    private void CancelAiVoiceApply() => _aiVoiceApplyTimer?.Stop();

    /// <summary>按配置勾选预设 chip；Style 为 null（手动 / 未选择）时全部不选中。</summary>
    private void SelectToneChip(ToneStyle? style)
    {
        foreach (var chip in ToneGrid.Children.OfType<RadioButton>())
        {
            chip.IsChecked = style.HasValue
                             && chip.Tag is string tag
                             && Enum.TryParse<ToneStyle>(tag, out var parsed)
                             && parsed == style.Value;
        }
    }

    private void OnToneSelected(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton chip || chip.Tag is not string tag) return;
        if (!Enum.TryParse<ToneStyle>(tag, out var style)) return;

        // 预设 = 把整条曲线写进增益表。此后音频与界面读的都是这一份数据，
        // 不存在"界面显示预设、处理却用别的参数"的可能。
        _config.Tone.Style = style;
        _config.Tone.Gains = EqPreset.GainsOf(style);

        // 显式同步 chip 勾选：真实点击时 RadioButton 已经把自己置位（同组互斥），
        // 但**程序化调用不会** —— 自检就是程序化调用，实测因此出现过
        // "配置已是「沉稳」、界面却没有任何 chip 被勾选"。一行代价换掉这类不一致。
        SelectToneChip(style);

        // 用户主动选了预设即视为要使用该模块
        if (ToggleTone.IsChecked != true) ToggleTone.IsChecked = true;

        ApplyEqGainsToSliders();
        RefreshEqVisuals();
        _engine.UpdateAllParameters();
        SaveConfig();
    }

    // =============================================================== EQ 均衡器

    /// <summary>
    /// 生成 10 段推子与频率标签。
    ///
    /// 放在代码里而不是 XAML：10 段手写出来有近 80 行、全是重复结构，
    /// 而且代码本来就要按索引持有它们（预设联动、重置、启动回填都要逐段赋值）。
    /// </summary>
    private void BuildEqBands()
    {
        var sliderStyle = (Style)FindResource("EqBandSlider");
        var labelStyle = (Style)FindResource("SmallText");

        for (var i = 0; i < EqPreset.BandCount; i++)
        {
            var slider = new Slider
            {
                Style = sliderStyle,
                Minimum = EqPreset.MinGainDb,
                Maximum = EqPreset.MaxGainDb,
                Value = 0,
                Tag = i,                     // 事件里据此知道是哪一段
            };
            slider.ValueChanged += OnEqBandChanged;

            var label = new TextBlock
            {
                Text = EqPreset.LabelOf(i),
                Style = labelStyle,
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };

            // 推子进推子区、标签进标签区：两个 UniformGrid 同列数，所以左右天然对齐；
            // 分开是因为推子区那一层还要叠一个 Canvas 画轨道与填充（见 RedrawEqBars）。
            _eqSliders[i] = slider;
            EqBandGrid.Children.Add(slider);
            EqLabelGrid.Children.Add(label);
        }
    }

    /// <summary>手动拖动某一段：只改这一段，并脱离预设（取消 chip 选中）。</summary>
    private void OnEqBandChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || sender is not Slider slider || slider.Tag is not int index) return;
        if (index < 0 || index >= EqPreset.BandCount) return;

        var gains = EqPreset.Normalize(_config.Tone.Gains);
        gains[index] = (float)Math.Round(e.NewValue, 1);
        _config.Tone.Gains = gains;

        // 手动一动就不再是"那个预设"了：取消勾选，避免界面在撒谎
        if (_config.Tone.Style.HasValue)
        {
            _config.Tone.Style = null;
            SelectToneChip(null);
        }

        if (ToggleTone.IsChecked != true) ToggleTone.IsChecked = true;

        RefreshEqVisuals();
        _engine.UpdateAllParameters();
        ScheduleSave();
    }

    /// <summary>
    /// 重置：全部归零，并清掉预设选中态。
    /// **刻意不弹状态条提示**：重置的结果在界面上一眼就能看到（推子回中间、曲线变平），
    /// 再弹一条黄字反而多余（用户 2026-10-06 要求取消）。
    /// </summary>
    private void OnEqResetClick(object sender, RoutedEventArgs e)
    {
        _config.Tone.Style = null;
        _config.Tone.Gains = EqPreset.Flat();

        SelectToneChip(null);
        ApplyEqGainsToSliders();
        RefreshEqVisuals();
        _engine.UpdateAllParameters();
        SaveConfig();
    }

    /// <summary>把配置里的增益写回推子。**必须屏蔽事件**，否则会与拖动互相触发。</summary>
    private void ApplyEqGainsToSliders()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            var gains = EqPreset.Normalize(_config.Tone.Gains);
            for (var i = 0; i < EqPreset.BandCount; i++) _eqSliders[i].Value = gains[i];
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void OnEqCurveSizeChanged(object sender, SizeChangedEventArgs e) => RefreshEqVisuals();

    /// <summary>曲线 + 推子轨道/填充一起重画（两者共用同一份增益数据，必须同时更新）。</summary>
    private void RefreshEqVisuals()
    {
        RedrawEqCurve();
        RedrawEqBars();
    }

    /// <summary>
    /// EQ 推子的滑块尺寸与轨道宽度。
    /// **必须与 <c>Ui/Theme.Modern.xaml</c> 里 <c>EqBandSlider</c> 的 Thumb 一致**，
    /// 也与水平滑条 <c>FlatSlider</c>（滑块 14、轨道 5）一致 ——
    /// 同一套界面里两种滑条粗细/大小不同会很扎眼（用户 2026-10-06 指出）。
    /// </summary>
    private const double EqThumbSize = 14;
    private const double EqTrackWidth = 5;
    private const double EqThumbHalf = EqThumbSize / 2;

    /// <summary>
    /// 画推子区的轨道与填充条。
    ///
    /// 填充**从中线（0 dB）往滑块长**，而不是从一端填满：增益有正有负，
    /// 从一端填的话 0 dB 也会填掉一半，看起来像"已经调过了"。
    ///
    /// 尺寸必须与水平滑条（<c>FlatSlider</c>）完全一致 —— 轨道 5px、滑块 14px：
    /// 同一套界面里两种滑条粗细/大小不同会很扎眼（用户 2026-10-06 指出）。
    ///
    /// 坐标必须与 EqBandSlider 里 Thumb 的实际行程一致，否则滑块会跑出填充条端点：
    /// 垂直 Slider 的 Thumb 中心从"半个滑块高"走到"高度 − 半个滑块高"，
    /// 即 7 → 77（高度 84、滑块 14），所以振幅 = 高度/2 − 7。
    /// 推子区 Grid 的高度（84）与 Slider 的 Height 相同，两层的 y 原点才对得上。
    /// </summary>
    private void RedrawEqBars()
    {
        var canvas = EqBarCanvas;
        canvas.Children.Clear();

        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        if (width <= 1 || height <= 1) return;

        // 诊断（只在自检里打）：轨道画在这个 Canvas 上，滑块却由 EqBandGrid 布局。
        // 两者宽度必须一致 —— 不一致时每个推子的横向偏移量各不相同，
        // 表现就是"有几个滑块的圆点不在轨道的水平正中间"（用户 2026-10-06 报的问题）。
        var gridWidth = EqBandGrid.ActualWidth;
        if (IsSelfCheckMode && Math.Abs(gridWidth - width) > 0.01)
        {
            Log.Info($"[EQ 轨道] 画布宽 {width:0.##} ≠ 推子区宽 {gridWidth:0.##}"
                     + " —— 轨道是按旧宽度画的，横向上会整体偏移");
        }

        var middle = height / 2;
        var amplitude = middle - EqThumbHalf;   // 与 Thumb 的真实行程对齐
        var slot = width / EqPreset.BandCount;

        // 颜色一律用 SetResourceReference 动态引用（见下面每个图形的注释），
        // 所以这里不再预先取画刷。
        var gains = EqPreset.Normalize(_config.Tone.Gains);

        for (var i = 0; i < EqPreset.BandCount; i++)
        {
            var x = (i + 0.5) * slot;
            var y = middle - gains[i] / EqPreset.MaxGainDb * amplitude;

            var track = new System.Windows.Shapes.Rectangle
            {
                // 尺寸与水平滑条一致：宽 5、圆角 2.5（FlatSlider 的轨道就是 Height=5 / CornerRadius=2.5）
                Width = EqTrackWidth,
                Height = height - EqThumbSize,
                RadiusX = EqTrackWidth / 2,
                RadiusY = EqTrackWidth / 2,
                SnapsToDevicePixels = true,
            };
            // ⚠ 必须用 SetResourceReference（动态引用），不能"取一次画刷再赋值"：
            // 取一次的话，切换深浅主题时**这个图形不会跟着变** ——
            // 滑块在 XAML 模板里用 DynamicResource 会变、轨道却是旧主题的颜色，
            // 表现就是"浅色模式下滑条还是深色的"（用户 2026-10-06 报的问题）。
            track.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TrackBrush");
            Canvas.SetLeft(track, x - EqTrackWidth / 2);
            Canvas.SetTop(track, EqThumbHalf);
            canvas.Children.Add(track);

            var barHeight = Math.Abs(y - middle);
            if (barHeight < 1) continue;   // 0 dB 附近不画，避免留下一个突兀的小方块

            var bar = new System.Windows.Shapes.Rectangle
            {
                Width = EqTrackWidth,
                Height = barHeight,
                RadiusX = EqTrackWidth / 2,
                RadiusY = EqTrackWidth / 2,
                SnapsToDevicePixels = true,
            };
            bar.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush");
            Canvas.SetLeft(bar, x - EqTrackWidth / 2);
            Canvas.SetTop(bar, Math.Min(middle, y));
            canvas.Children.Add(bar);
        }
    }

    /// <summary>
    /// 重画响应曲线：10 个增益点连成折线，叠加一条 0 dB 中线。
    ///
    /// 只是"把推子位置连起来"，不是真正的滤波器频响计算 —— 对图形均衡器来说，
    /// 用户关心的是"我调成了什么形状"，折线已经足够准确，而且拖动时零延迟。
    /// x 取每格中心，与下方推子严格对齐。
    /// </summary>
    private void RedrawEqCurve()
    {
        var canvas = EqCurveCanvas;
        canvas.Children.Clear();

        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        if (width <= 1 || height <= 1 || _eqSliders[0] == null) return;

        var middle = height / 2;
        // 上下各留 4px，保证 ±12 dB 的极值点不会被裁掉
        var amplitude = middle - 4;

        // 与推子轨道同理：这里的图形都由代码创建，颜色**必须动态引用资源**。
        // 取一次画刷再赋值的话，切换深浅主题后这些图形仍是旧主题的颜色。
        var hairline = new System.Windows.Shapes.Line
        {
            X1 = 0, Y1 = middle, X2 = width, Y2 = middle,
            StrokeThickness = 1,
        };
        hairline.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "HairlineBrush");
        canvas.Children.Add(hairline);

        var gains = EqPreset.Normalize(_config.Tone.Gains);
        var points = new PointCollection();
        for (var i = 0; i < EqPreset.BandCount; i++)
        {
            var x = (i + 0.5) * width / EqPreset.BandCount;
            var y = middle - gains[i] / EqPreset.MaxGainDb * amplitude;
            points.Add(new Point(x, y));
        }

        // 先铺一层半透明的粗线当"填充感"，再压一条实线：比单线更容易看出曲线走向
        var glow = new System.Windows.Shapes.Polyline
        {
            Points = points,
            StrokeThickness = 6,
            Opacity = 0.18,
            StrokeLineJoin = PenLineJoin.Round,
        };
        glow.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
        canvas.Children.Add(glow);

        var curve = new System.Windows.Shapes.Polyline
        {
            Points = points,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        };
        curve.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
        canvas.Children.Add(curve);

        // 每段一个小圆点，拖哪一段一眼能对上
        foreach (var point in points)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 5, Height = 5 };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush");
            Canvas.SetLeft(dot, point.X - 2.5);
            Canvas.SetTop(dot, point.Y - 2.5);
            canvas.Children.Add(dot);
        }
    }

    /// <summary>按配置勾选效果器类型；Kind 为 null 时全部不选中。</summary>
    private void SelectEffectChip(CreativeEffectKind? kind)
    {
        foreach (var chip in EffectGrid.Children.OfType<RadioButton>())
        {
            chip.IsChecked = kind.HasValue
                             && chip.Tag is string tag
                             && Enum.TryParse<CreativeEffectKind>(tag, out var parsed)
                             && parsed == kind.Value;
        }
    }

    private void OnCreativeEffectSelected(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton chip || chip.Tag is not string tag) return;
        if (!Enum.TryParse<CreativeEffectKind>(tag, out var kind)) return;
        if (_config.Effect.Kind == kind) return;

        _config.Effect.Kind = kind;
        if (ToggleEffect.IsChecked != true) ToggleEffect.IsChecked = true;

        _engine.UpdateAllParameters();
        SaveConfig();
    }

    // =============================================================== 降噪模型

    // 打开下拉框不再强制重新校验模型：Scan 会为每个模型创建 ONNX 会话，
    // 15 MB 的模型在 UI 线程上做这件事会造成明显卡顿（实测用户反馈"点下拉框就卡死"）。
    // 改用缓存，只有训练完成或显式刷新时才重新扫描。
    private void OnModelDropDownOpened(object sender, EventArgs e)
    {
        if (_modelsScanned) return;
        RefreshModels();
    }
    private bool _modelsScanned;

    private void RefreshModels()
    {
        var previous = _loading;
        _loading = true;
        try
        {
            var models = ModelCatalog.Scan();
            _modelsScanned = true;
            var itemStyle = (Style)FindResource("FlatComboItem");

            ModelCombo.Items.Clear();
            foreach (var model in models)
                ModelCombo.Items.Add(new ComboBoxItem
                {
                    Content = model.Name,
                    Tag = model,
                    Style = itemStyle,
                    ToolTip = model.Error ?? model.Path,
                });

            // 用与启动时同一套解析逻辑挑出当前模型，避免两处规则不一致
            var current = FindConfiguredModel() ?? models.FirstOrDefault();
            if (current == null) return;

            ModelCombo.SelectedIndex = models.ToList().IndexOf(current);
            ApplyModel(current);
        }
        finally
        {
            _loading = previous;
        }
    }

    private void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if ((ModelCombo.SelectedItem as ComboBoxItem)?.Tag is not DenoiseModelInfo info) return;

        // 存**稳定标识**而不是显示名：内置降噪存空串，外部模型存不带扩展名的文件名。
        // 这样以后改文案不会让用户的模型选择失配。
        _config.Denoise.Model = info.IsDefault
            ? ModelCatalog.BuiltInModelKey
            : Path.GetFileNameWithoutExtension(info.Path);
        ApplyModel(info);
        SaveConfig();
    }

    private void ApplyModel(DenoiseModelInfo info)
    {
        try
        {
            if (info.IsDefault)
            {
                _engine.SetDenoiseModel(new SpectralDenoiseModel { Strength = _config.Denoise.Strength });
                Log.Info("降噪模型：内置谱减降噪（默认）");
                return;
            }

            // 先识别模型形态：波形域（RNNoise/DPDFNet 等）与增益曲线域走不同后端
            var validation = ModelCatalog.Validate(info.Path, out var tensorInfo, out var kind);
            if (validation != null)
            {
                ShowStatus($"模型 {info.Name} 无法使用：{validation}", false);
                _engine.SetDenoiseModel(new SpectralDenoiseModel { Strength = _config.Denoise.Strength });
                return;
            }

            // 按模型形态选择后端
            if (kind == DenoiseModelKind.SpectralStreaming)
            {
                _engine.SetDenoiseModel(new DpdfNetDenoiseModel(info.Path, info.Name));
                Log.Info($"降噪模型已载入（频谱域流式）：{info.Name} — {tensorInfo}");
                return;
            }

            if (kind == DenoiseModelKind.Waveform)
            {
                _engine.SetDenoiseModel(new OnnxWaveformDenoiseModel(info.Path, info.Name));
                Log.Info($"降噪模型已载入（波形域）：{info.Name} — {tensorInfo}");
                return;
            }

            if (ModelCatalog.TryExtractProfile(info.Path, out var gains, out var message))
            {
                _engine.SetDenoiseModel(new OnnxProfileDenoiseModel(info.Name, gains, _config.Denoise.Strength));
                Log.Info($"降噪模型已载入（增益曲线域）：{info.Name}（{gains.Length} 频点）");
            }
            else
            {
                ShowStatus($"模型 {info.Name} 无法使用：{message}", false);
                _engine.SetDenoiseModel(new SpectralDenoiseModel { Strength = _config.Denoise.Strength });
            }
        }
        catch (Exception ex)
        {
            Log.Error("载入降噪模型失败", ex);
            ShowStatus("模型载入失败：" + ex.Message, false);
        }
    }

    private void OnOpenModelsFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ConfigStore.ModelsDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = ConfigStore.ModelsDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowStatus("打开模型文件夹失败：" + ex.Message, false);
        }
    }

    /// <summary>
    /// 使用指南：分页显示**内置**的说明（正文在仓库的 Assets\使用指南.txt，随程序集编译进来）。
    /// </summary>
    // ================= AI 变声界面（本轮先做界面，推理功能随后接线） =================

    /// <summary>AI 变声音色模型下拉项。</summary>
    private sealed record AiVoiceItem(string FileName)
    {
        public override string ToString() => FileName;
    }

    /// <summary>AI 变声索引下拉项。</summary>
    private sealed record AiVoiceIndexItem(string FileName)
    {
        public override string ToString() => FileName;
    }

    private void OnAiVoiceModelDropDownOpened(object sender, EventArgs e)
    {
        if (_loading) return;
        var keep = (AiVoiceModelCombo.SelectedItem as AiVoiceItem)?.FileName;
        AiVoiceModelCombo.Items.Clear();
        foreach (var f in ListFiles(ConfigStore.AiVoicesDirectory, "*.onnx"))
            AiVoiceModelCombo.Items.Add(new AiVoiceItem(Path.GetFileName(f)));
        if (keep != null)
            AiVoiceModelCombo.SelectedItem = AiVoiceModelCombo.Items.Cast<AiVoiceItem>()
                .FirstOrDefault(i => i.FileName == keep);
    }

    private void OnAiVoiceModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _config.AiVoice.VoiceModel = (AiVoiceModelCombo.SelectedItem as AiVoiceItem)?.FileName;
        SaveConfig();
    }

    private void OnAiVoiceIndexDropDownOpened(object sender, EventArgs e)
    {
        if (_loading) return;
        var keep = (AiVoiceIndexCombo.SelectedItem as AiVoiceIndexItem)?.FileName;
        AiVoiceIndexCombo.Items.Clear();
        foreach (var f in ListFiles(ConfigStore.AiIndexDirectory, "*.index"))
            AiVoiceIndexCombo.Items.Add(new AiVoiceIndexItem(Path.GetFileName(f)));
        if (keep != null)
            AiVoiceIndexCombo.SelectedItem = AiVoiceIndexCombo.Items.Cast<AiVoiceIndexItem>()
                .FirstOrDefault(i => i.FileName == keep);
        UpdateAiVoiceIndexAvailability();
    }

    private void OnAiVoiceIndexSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 没有索引时"索引占比"置灰不可调（用户要求）
        UpdateAiVoiceIndexAvailability();
        if (_loading) return;
        _config.AiVoice.IndexFile = (AiVoiceIndexCombo.SelectedItem as AiVoiceIndexItem)?.FileName;
        SaveConfig();
    }

    private void OnOpenAiVoicesFolderClick(object sender, RoutedEventArgs e)
        => OpenFolder(ConfigStore.AiVoicesDirectory, "音色模型");

    private void OnOpenAiIndexFolderClick(object sender, RoutedEventArgs e)
        => OpenFolder(ConfigStore.AiIndexDirectory, "索引");

    /// <summary>
    /// 音频块长度变动：记配置 + **重建引擎**。
    ///
    /// ⚠ 必须调 UpdateAllParameters：块长决定了缓冲几何与 StreamingRvc 的块大小，
    /// 只写 config 是不生效的（2026-10-08 用户反馈"音频块这个滑条是不是没功能，
    /// 我调了没效果"）。其它滑条走 OnSliderChanged，其末尾本来就有这一步，唯独这里漏了。
    /// </summary>
    private void OnAiVoicePerfChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _config.AiVoice.BlockMs = (int)Math.Round(e.NewValue);
        // 同样要防抖：块长变化会重建引擎（见 ScheduleAiVoiceApply 的说明）
        ScheduleAiVoiceApply();
    }

    private void OnAddAiVoiceClick(object sender, RoutedEventArgs e)
    {
        // 复用 DialogHost 的现成样式（标题栏/描边/按钮），内容用 AiComponentPanel
        DialogHost.ShowCustom(this, "添加 AI 变声", new AiComponentPanel());
        // 对话框里可能刚放进组件或音色，回来刷新一下下拉与索引可用性
        RefreshAiVoiceLists();
    }

    /// <summary>重新扫描音色与索引目录，把已保存的选择对上号。</summary>
    private void RefreshAiVoiceLists()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            AiVoiceModelCombo.Items.Clear();
            foreach (var f in ListFiles(ConfigStore.AiVoicesDirectory, "*.onnx"))
                AiVoiceModelCombo.Items.Add(new AiVoiceItem(Path.GetFileName(f)));
            AiVoiceModelCombo.SelectedItem = AiVoiceModelCombo.Items.Cast<AiVoiceItem>()
                .FirstOrDefault(i => i.FileName == _config.AiVoice.VoiceModel);

            AiVoiceIndexCombo.Items.Clear();
            foreach (var f in ListFiles(ConfigStore.AiIndexDirectory, "*.index"))
                AiVoiceIndexCombo.Items.Add(new AiVoiceIndexItem(Path.GetFileName(f)));
            AiVoiceIndexCombo.SelectedItem = AiVoiceIndexCombo.Items.Cast<AiVoiceIndexItem>()
                .FirstOrDefault(i => i.FileName == _config.AiVoice.IndexFile);
        }
        finally { _loading = wasLoading; }
        UpdateAiVoiceIndexAvailability();
    }

    private static IEnumerable<string> ListFiles(string directory, string pattern)
        => Directory.Exists(directory)
            ? Directory.GetFiles(directory, pattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            : Enumerable.Empty<string>();

    private void OpenFolder(string directory, string what)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus($"打开{what}文件夹失败：" + ex.Message, false);
        }
    }

    private void OnUsageGuideClick(object sender, RoutedEventArgs e)        => DialogHost.ShowGuide(this, "MateMic 使用指南", GuideCatalog.Pages);

    /// <summary>
    /// 关于：把"必须声明的东西"集中在一处 —— 界面字体（MiSans 的授权要求）、
    /// 内置降噪模型、用户自备模型的许可归属、第三方组件。版本号与工具栏显示同一个来源。
    ///
    /// ⚠ 这段是**功能性对话框**（许可与署名），不是"使用说明"，所以仍留在代码里；
    /// 使用说明已全部搬进使用指南。
    /// </summary>
    private void OnAboutClick(object sender, RoutedEventArgs e)
        => DialogHost.Info(this, "关于 MateMic", AboutText());

    private static string AboutText()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        var text = version == null ? "v?" : "v" + version.ToString(3);

        return
            $"MateMic {text}   MIT 许可\n\n" +
            "降噪模型   DPDFNet / gtcrn\n" +
            "界面字体   MiSans\n" +
            "第三方组件   NAudio / NWaves / ONNX Runtime";
    }

    // =============================================================== 播放器

    private void LoadTracksFromConfig()
    {
        _tracks.Clear();
        foreach (var track in _config.Player.Tracks)
        {
            if (string.IsNullOrWhiteSpace(track.Path)) continue;
            _tracks.Add(new TrackViewModel(track) { HoldKeyFeatureEnabled = _config.Player.EnableHoldKey });
        }

        TrackList.Items.Refresh();
    }

    private void OnAddFilesClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "添加音频文件",
            Multiselect = true,
            Filter = "音频文件|*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac;*.ogg|所有文件|*.*",
        };

        if (dialog.ShowDialog(this) != true) return;

        AddAudioFiles(dialog.FileNames);
    }

    // =============================================================== 拖放与组件

    /// <summary>拖放支持的音频扩展名（与「添加音频文件」对话框的过滤器保持一致）。</summary>
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac", ".ogg",
    };

    private static bool IsAudioFile(string path) => AudioExtensions.Contains(Path.GetExtension(path));

    /// <summary>把拖入的路径摊平成文件（文件夹只取顶层，避免误扫整个磁盘）。</summary>
    private static IEnumerable<string> ExpandDroppedPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path)) yield return file;
            }
            else if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>统一入口：拖进窗口的音频文件进播放列表（文件夹只取顶层）。</summary>
    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped || dropped.Length == 0) return;
        e.Handled = true;

        var files = ExpandDroppedPaths(dropped).ToList();
        var audio = files.Where(IsAudioFile).ToList();

        if (audio.Count > 0)
        {
            var added = AddAudioFiles(audio);
            Log.Info($"拖入音频 {audio.Count} 个，新增 {added} 个到播放列表");
        }
    }

    /// <summary>把音频文件加进播放列表（对话框与拖放共用）。返回真正新增的数量。</summary>
    private int AddAudioFiles(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var file in paths)
        {
            if (!File.Exists(file)) continue;
            if (_tracks.Any(t => string.Equals(t.FilePath, file, StringComparison.OrdinalIgnoreCase))) continue;

            var model = new PlayerTrack
            {
                Path = file,
                DisplayName = Path.GetFileName(file),
                Hotkey = string.Empty,
            };
            _config.Player.Tracks.Add(model);
            _tracks.Add(new TrackViewModel(model));
            added++;
        }

        if (added > 0)
        {
            TrackList.Items.Refresh();
            SaveConfig();
        }

        return added;
    }

    private void OnTrackHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not TrackViewModel track) return;

        ClearRecordingIndicators();
        _pendingHotkeyTrack = track;
        _pendingHotkeyGesture = string.Empty;
        track.IsRecordingHotkey = true;

        // 录入期间先把旧热键放开：否则用户按下它与旧键相同的组合时，系统会把它当成
        // "已经注册的热键"直接吞掉（窗口收不到按键），想重新设成同一个键就做不到。
        // Esc 取消（CancelHotkeyCapture）或正常录完都会重新注册回去。
        if (!string.IsNullOrWhiteSpace(track.Hotkey) && _hotkeys.Unregister(track.Hotkey))
            Log.Info($"重新录入「{track.DisplayName}」的快捷键：旧热键 {track.Hotkey} 已临时注销。");

        ShowStatus($"正在为「{track.DisplayName}」录入快捷键：请按键（Esc 取消，Backspace 清除）", false);
        BeginKeyboardCapture();
    }

    /// <summary>为某个音频文件录入「同步按住键」。</summary>
    private void OnTrackHoldKeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not TrackViewModel track) return;

        if (!_config.Player.EnableHoldKey)
        {
            ShowStatus("请先打开播放器面板里的「同步按住键」总开关（注意反作弊风险）。", false);
            return;
        }

        ClearRecordingIndicators();
        _pendingHoldKeyTrack = track;
        track.IsRecordingHoldKey = true;
        ShowStatus($"正在为「{track.DisplayName}」录入同步按住键：请按**一个**按键" +
                   "（Esc 取消，Backspace 清除）。播放前会自动按下、播放结束后松开。", false);
        BeginKeyboardCapture();
    }

    /// <summary>
    /// 「同步按住键」总开关。风险确认已在 <see cref="OnHoldKeyTogglePreview"/> 里做完，
    /// 这里只负责把开关的新状态落到配置与列表中。
    ///
    /// **刻意不弹状态条**：开关本身就是反馈（蓝=开、灰=关），
    /// 打开后列表里每条也立刻多出「设同步键」按钮，再弹一条黄字纯属重复
    /// （用户 2026-10-07 要求去掉，与之前取消「重置」提示是同一个理由）。
    /// </summary>
    private void OnHoldKeyToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var enabled = ToggleHoldKey.IsChecked == true;
        _config.Player.EnableHoldKey = enabled;
        foreach (var track in _tracks) track.HoldKeyFeatureEnabled = enabled;

        if (!enabled) _keyboard.Release();

        SaveConfig();
    }

    /// <summary>
    /// 「同步按住键」开关的**前置拦截**：没确认过风险就不让开关真的拨过去。
    ///
    /// ⚠ 为什么不在 Checked 事件里"先拨到开 → 弹窗 → 用户取消 → 再设回 false"：
    /// 开关的**底色**是模板 Trigger 的 `Setter` 驱动的，设回 false 会立刻变回灰；
    /// 但**滑块位置是 Storyboard 动画**驱动的（`EnterActions`/`ExitActions` 各跑一个），
    /// 实测这样"拨过去再拨回来"之后动画会残留，滑块停在"开"的那一头 ——
    /// 于是出现"底色是灰的、滑块却在右边"这种自相矛盾的样子
    /// （用户 2026-10-06 报的问题，已复现并抓图）。
    ///
    /// 在**切换发生之前**下载这次点击，IsChecked 从头到尾都是 false，不会产生那个中间状态。
    /// 开关是 `Focusable=False` 的（见 `Theme.Modern.xaml` 的 `Switch` 样式），键盘切不动，所以只需拦鼠标。
    ///
    /// ⚠ 确认之后**必须自己把 IsChecked 拨过去**：这里弹的是模态对话框，
    /// 用户点"确认继续"时鼠标左键已经在对话框上抬起了，而 CheckBox 要等到
    /// `MouseLeftButtonUp` 才切换 —— 那个事件永远不会再来，不手动拨就开不了。
    /// </summary>
    private void OnHoldKeyTogglePreview(object sender, MouseButtonEventArgs e)
    {
        if (_loading) return;

        // 已经开着 = 这次点击是要"关掉"，不需要再确认
        if (_config.Player.EnableHoldKey) return;

        if (!ConfirmHoldKeyRisk())
        {
            // 用户没有确认风险：吃掉这次点击，开关保持关闭、也不会动
            e.Handled = true;
            return;
        }

        // 确认了：手动拨过去（会走 Checked → OnHoldKeyToggled 把配置落地）
        ToggleHoldKey.IsChecked = true;
        e.Handled = true;
    }

    private void OnHoldKeyRiskClick(object sender, RoutedEventArgs e) => ShowHoldKeyRisk(false);

    /// <summary>
    /// 「同步按住键」的风险说明。
    ///
    /// ⚠ 这是**功能性提示**（开启前的风险确认），不是"使用说明"，所以留在代码里：
    /// 它必须跟着功能走，不该被外部文件改掉之后失去效力。
    /// 使用说明那些内容已经全部搬进使用指南。
    /// </summary>
    private const string HoldKeyRiskTitle = "同步按住键 · 风险说明";

    private const string HoldKeyRiskText =
        """
        请不要在反作弊运行时使用此功能！
        请不要在反作弊运行时使用此功能！
        请不要在反作弊运行时使用此功能！

        同步按住键功能使用 Win32 SendInput 向系统注入按键事件
        SendInput 注入带有 LLKHF_INJECTED 标记，属于软件模拟输入
        此种行为是否违规由反作弊的策略决定
        此功能不包含任何作弊功能，仅在用户播放音频时通过模拟输入同步按住一个用户设置的按键
        请遵守游戏或第三方平台的用户协议及相关规则
        因开启此功能导致被反作弊封禁，本软件开发者及关联方不承担任何责任

        请认真阅读以上内容，点击下方确认继续按钮将视为已阅读并了解相应风险
        并自行承担由此产生的一切后果
        """;

    /// <summary>确认框的主按钮文案（用户指定：把"继续"写清楚，避免顺手点过）。</summary>
    private const string HoldKeyRiskConfirmText = "我已阅读并了解风险，确认继续";

    /// <summary>开启前的确认框；返回 true 表示用户接受风险。</summary>
    private bool ConfirmHoldKeyRisk()
        => DialogHost.Ask(this, HoldKeyRiskTitle, HoldKeyRiskText,
                          "取消", HoldKeyRiskConfirmText);

    private void ShowHoldKeyRisk(bool warning)
    {
        // warning 为真表示"刚被启用"，此时配置未必已落盘，所以直接按已启用来显示
        var state = warning || _config.Player.EnableHoldKey ? "当前开关：已启用。" : "当前开关：未启用。";
        var text = HoldKeyRiskText + "\n\n" + state;

        if (warning) DialogHost.Warn(this, HoldKeyRiskTitle, text);
        else DialogHost.Info(this, HoldKeyRiskTitle, text);
    }

    private (bool Ok, string? Error) RegisterTrackHotkey(TrackViewModel track, string gesture)
    {
        // 冲突在这里先查一遍（只判重、不注册）。真正的注册统一交给
        // RegisterConfiguredHotkeys()：它先 UnregisterAll 再全量注册一遍。
        //
        // 早期这里直接 _hotkeys.Register，随后 RegisterConfiguredHotkeys 又注册了同一个键，
        // 于是第二次必然拿到"已被其他程序占用"——明明刚设成功却弹一条失败提示（用户反馈的 bug）。
        var conflict = DescribeConflict(gesture, track, GestureKind.Hotkey);
        if (conflict != null) return (false, conflict);

        track.Hotkey = gesture;
        Log.Info($"已为 {track.DisplayName} 绑定全局快捷键 {gesture}");
        return (true, null);
    }

    // --------------------------------------------------------------- 按键冲突检查

    private enum GestureKind
    {
        /// <summary>全局热键：进程内必须唯一（系统同时只允许一个注册）。</summary>
        Hotkey,

        /// <summary>同步按住键：不同音频之间允许重复，但不能和任何全局热键撞。</summary>
        HoldKey,
    }

    /// <summary>比较手势文本：忽略大小写与空格差异（"Ctrl + A" 与 "ctrl+a" 视为同一个键）。</summary>
    private static bool SameGesture(string a, string b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
           string.Equals(a.Replace(" ", string.Empty), b.Replace(" ", string.Empty),
               StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 检查一个按键有没有被别处占用，返回给用户看的提示；没有冲突返回 null。
    ///
    /// 规则（用户 2026-09 第三轮明确）：
    ///   · 全局热键（总开关热键 + 各音频条目的播放热键）彼此**不能重复**；
    ///   · 同步按住键**允许**在不同音频之间重复，但**不能**等于任何一个全局热键
    ///     （否则按下去会同时触发播放热键，必然会互相干扰）；
    ///   · 同一条目自己的两个键也不能撞在一起。
    /// </summary>
    private string? DescribeConflict(string gesture, TrackViewModel? editing, GestureKind kind)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return null;

        // 1) 与总开关热键比较。
        //    例外：正在重设总开关热键、且新键就是它自己原来的值——那不是冲突。
        //    少了这个例外，"想再确认/重录一遍同一个键"会被自己挡住。
        if (SameGesture(gesture, _config.ToggleHotkey) &&
            !(kind == GestureKind.Hotkey && editing == null))
        {
            return kind == GestureKind.Hotkey
                ? $"{gesture} 已经是「音频处理」总开关的全局快捷键，请换一个。"
                : $"{gesture} 已经是「音频处理」总开关的全局快捷键，同步按住键不能再用它。";
        }

        foreach (var track in _tracks)
        {
            var isSelf = ReferenceEquals(track, editing);

            // 2) 与其它音频条目的播放热键比较：任何情况下都不能重复。
            //    例外：正在**重录自己那一条**，且新键就是它原来的键——不算冲突。
            if (SameGesture(gesture, track.Hotkey) && !(isSelf && kind == GestureKind.Hotkey))
            {
                if (isSelf)
                    return $"{gesture} 已经是该音频自己的播放快捷键，请换一个。";
                return $"{gesture} 已被音频「{track.DisplayName}」用作播放快捷键，请换一个。";
            }

            // 3) 与同步按住键比较：只在"当前设的是全局热键"时才冲突。
            //    不需要上面的例外——同一个条目改成同一个值会被 Model 的等价判断挡掉。
            if (kind == GestureKind.Hotkey && SameGesture(gesture, track.HoldKey))
            {
                return $"{gesture} 已被音频「{track.DisplayName}」用作同步按住键，" +
                       "全局快捷键不能和它相同（按键会同时触发两者）。";
            }
        }

        return null;
    }

    /// <summary>
    /// 全量重建全局热键：先全部注销，再按当前配置注册一遍。
    /// 所有"改热键"的路径最终都只走这里，避免同一个键被注册两次而误报"已被其他程序占用"。
    /// 注册失败时把配置里的值清掉并提示，保证界面与系统状态一致。
    /// </summary>
    private void RegisterConfiguredHotkeys()
    {
        _hotkeys.UnregisterAll();

        if (!string.IsNullOrWhiteSpace(_config.ToggleHotkey))
        {
            // 冲突检查：不与任何音频条目的播放热键/同步按住键重复
            var conflict = DescribeConflict(_config.ToggleHotkey, null, GestureKind.Hotkey);
            if (conflict != null)
            {
                ShowStatus(conflict, false);
                SetHotkeyButtonText(string.Empty);
                _config.ToggleHotkey = string.Empty;
            }
        }

        if (!string.IsNullOrWhiteSpace(_config.ToggleHotkey))
        {
            var result = _hotkeys.Register(_config.ToggleHotkey, ToggleProcessing_Click);
            if (!result.Success)
            {
                ShowStatus(result.Error ?? "总开关快捷键注册失败，请更换。", false);
                SetHotkeyButtonText(string.Empty);
                _config.ToggleHotkey = string.Empty;
            }
        }

        foreach (var track in _tracks.Where(t => !string.IsNullOrWhiteSpace(t.Hotkey)).ToList())
        {
            var result = _hotkeys.Register(track.Hotkey, () => PlayTrack(track));
            if (!result.Success)
                ShowStatus($"「{track.DisplayName}」的快捷键 {track.Hotkey} 注册失败：{result.Error}", false);
        }
    }

    private void PlayTrack(TrackViewModel track)
    {
        if (!File.Exists(track.FilePath))
        {
            Log.Warn("播放失败，文件已不存在：" + track.FilePath);
            return;
        }

        // 全局热键可能在任意线程触发，统一回 UI 线程处理
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // 再点一次正在播放的条目 = 停止（列表里没有单独的停止按钮）
            if (string.Equals(_player.CurrentPath, track.FilePath, StringComparison.OrdinalIgnoreCase))
                _player.Stop();
            else
                _player.Play(track.FilePath);

            RefreshTrackHighlight();
        }));
    }

    private void RefreshTrackHighlight()
    {
        var current = _player.CurrentPath;
        foreach (var track in _tracks)
            track.IsCurrent = current != null && string.Equals(track.FilePath, current, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 播放状态变化：驱动「同步按住键」。
    ///   · Started  → 按下该音频配置的按键（若总开关打开且配置了按键）
    ///   · Finished / Stopped / Interrupted → 松开
    /// 松开用「当前是否真的按着」兜底，不依赖事件配对，避免异常路径下按键被永久按住。
    /// </summary>
    private void HandlePlaybackState(PlaybackState state)
    {
        if (state == PlaybackState.Started)
        {
            var started = _tracks.FirstOrDefault(t =>
                string.Equals(t.FilePath, _player.CurrentPath, StringComparison.OrdinalIgnoreCase));

            if (!_config.Player.EnableHoldKey || started == null || string.IsNullOrWhiteSpace(started.HoldKey))
            {
                // 上一条的按住键可能还按着（换曲到一条没配按键的音频），这里兜底松开
                if (_keyboard.IsHolding) _keyboard.Release();
                return;
            }

            _keyboard.Press(started.HoldKey);
            return;
        }

        // 换曲打断：新条目马上会 Started，由它决定是按住还是松开，这里不动
        if (state == PlaybackState.Interrupted) return;

        if (_keyboard.IsHolding) _keyboard.Release();
    }

    private void OnTrackRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is TrackViewModel track)
            PlayTrack(track);
    }

    /// <summary>从播放列表移除一个音频文件（同时注销它的全局快捷键）。</summary>
    private void OnTrackRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not TrackViewModel track) return;

        var confirm = DialogHost.Confirm(this, "移除音频文件",
            $"确定要从列表移除「{track.DisplayName}」吗？\n（只移除列表条目，不会删除磁盘上的文件）");
        if (!confirm) return;

        if (ReferenceEquals(_pendingHotkeyTrack, track)) _pendingHotkeyTrack = null;

        // ⚠ 正在播放的就是这一条时**必须先停掉**。
        // 列表里唯一的停止入口是"再点一次同一条"（PlayTrack 里那个 toggle），
        // 条目一旦移除，那个入口就没了 —— 声音会一直放下去，
        // 用户除了关程序没有任何办法停（用户 2026-10-06 报的问题）。
        // Stop() 会走 StateChanged → HandlePlaybackState，把「同步按住键」之类一起收尾。
        if (string.Equals(_player.CurrentPath, track.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            _player.Stop();
            Log.Info("正在播放的条目被移除，已停止播放：" + track.FilePath);
        }

        _tracks.Remove(track);
        _config.Player.Tracks.Remove(track.Model);
        TrackList.Items.Refresh();
        RegisterConfiguredHotkeys();   // 释放该条目占用的全局快捷键
        SaveConfig();
        Log.Info("已从播放列表移除：" + track.FilePath);
    }

    private void OnAudioMonitorToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _engine.SetAudioMonitor(ToggleAudioMonitor.IsChecked == true);
        SaveConfig();
    }

    private void OnLoopToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _config.Player.Loop = ToggleLoop.IsChecked == true;
        _player.LoopEnabled = _config.Player.Loop;
        SaveConfig();
    }

    private void OnPlayerVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _config.Player.Volume = (float)e.NewValue;
        _player.SetVolumePercent(_config.Player.Volume);
        ScheduleSave();
    }

    // =============================================================== 状态条 / 窗口

    /// <summary>
    /// 状态条自动消失的时间。早期没有这个定时器：一条"同步按住键已关闭"之类的提示会一直挂在那里，
    /// 直到下一条提示把它顶掉——用户会觉得提示赖着不走。
    /// </summary>
    private static readonly TimeSpan StatusAutoHideDelay = TimeSpan.FromSeconds(4);

    private DispatcherTimer? _statusTimer;

    /// <summary>
    /// 显示状态条。
    /// <paramref name="showGuideButton"/> = true 表示这是一条"需要用户处理"的提示
    /// （目前只有"未检测到 MIXLINE"），它带「查看设置指南」按钮并且**不自动消失**；
    /// 其余提示都是普通反馈，4 秒后自动收起。
    /// </summary>
    private void ShowStatus(string message, bool showGuideButton)
    {
        StatusText.Text = message;
        StatusBar.Visibility = Visibility.Visible;
        StatusActionButton.Visibility = showGuideButton ? Visibility.Visible : Visibility.Collapsed;
        _statusAction = showGuideButton ? ShowMixLineGuide : null;
        Log.Info("[状态] " + message);

        _statusTimer?.Stop();
        if (showGuideButton) return;   // 需要用户处理的提示保留，直到问题消失或被别的提示顶掉

        _statusTimer ??= new DispatcherTimer { Interval = StatusAutoHideDelay };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer!.Stop();
            HideStatus();
        };
        _statusTimer.Start();
    }

    private void HideStatus()
    {
        _statusTimer?.Stop();
        StatusBar.Visibility = Visibility.Collapsed;
        _statusAction = null;
    }

    private void OnStatusActionClick(object sender, RoutedEventArgs e) => _statusAction?.Invoke();

    /// <summary>
    /// MIXLINE 接法：内容就是使用指南里的「MIXLINE 接法」那一页，
    /// 所以直接打开使用指南，不再单独维护一份重复的文案。
    /// </summary>
    private void ShowMixLineGuide()
        => DialogHost.ShowGuide(this, "MateMic 使用指南", GuideCatalog.Pages);

    /// <summary>
    /// 关闭主窗口。打开「关闭到托盘」时收进托盘（托盘双击恢复），否则真正退出。
    /// 早期版本是「最小化到托盘」，最小化本来就应该只进任务栏，这里改回直觉行为。
    /// </summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting) return;

        if (_config.CloseToTray)
        {
            // 关闭窗口 = 收进托盘，不再弹系统提示（托盘双击即可恢复）
            e.Cancel = true;
            SaveWindowPlacement();
            SaveConfig();

            // 如果此时正卡在"录入快捷键"状态，那条热键是临时注销掉的：
            // 收进托盘后窗口收不到按键，不装回去就会一直失效到下次重启。
            // 三条录入路径都要覆盖（早期这里漏了「同步按住键」，它不会注销热键，
            // 但会一直留着"等待按键"的状态和低层钩子）。
            if (IsRecordingShortcut())
            {
                _capture.End();   // 顺手卸载低层键盘钩子
                _pendingHotkeyTrack = null;
                _pendingHotkeyGesture = null;
                _pendingHoldKeyTrack = null;
                SetHotkeyButtonText(_config.ToggleHotkey);
                RegisterConfiguredHotkeys();
            }

            Hide();
            Log.Info("关闭主窗口：已收进系统托盘（「关闭到托盘」为开）。");
            return;
        }

        ExitApplication();
    }

    /// <summary>
    /// 最小化**不再**收进托盘：最小化就是最小化到任务栏，只有关闭按钮才走托盘。
    /// 这条路径现在只做日志，将来若要恢复"最小化到托盘"可以在这里加。
    /// </summary>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && !_exiting)
            Log.Debug("窗口已最小化到任务栏。");
    }

    /// <summary>
    /// 弹出托盘右键菜单（Windows 11 风格的自绘窗口，见 <see cref="TrayMenuWindow"/>）。
    /// 三个菜单项直接复用既有动作方法，「界面开关 / 全局快捷键 / 托盘菜单」三条路径因此行为完全一致
    /// （总开关那条连提示音效也一起走 OnProcessingToggled）。
    /// </summary>
    private void ShowTrayMenu()
    {
        var menu = TrayMenuWindow.Open(_config.AudioProcessingEnabled);
        menu.ShowMainRequested += (_, _) => RestoreFromTray();
        menu.ToggleProcessingRequested += (_, _) => ToggleProcessing_Click();
        menu.ExitRequested += (_, _) => ExitApplication();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();

        // 从托盘恢复时窗口句柄可能已经重建（跨屏 / DPI 变化），把录入钩子重新挂上，
        // 否则"收进托盘再恢复"之后快捷键录入会静默失效。
        _capture.Reattach(new WindowInteropHelper(this).Handle);
    }

    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;

        // 退出前先收掉可能还开着的托盘菜单，避免留下一个无主的空窗口
        TrayMenuWindow.CloseOpen();
        SaveWindowPlacement();
        SaveConfig();
        _timer.Stop();
        _saveTimer?.Stop();
        // 退出前务必松开「同步按住键」，否则目标程序里会留下一个一直按着的按键
        _keyboard.Dispose();
        _capture.Dispose();
        _hotkeys.Dispose();
        _tray.Dispose();
        _player.Dispose();
        CancelAiVoiceApply();       // 先停掉待生效的"AI 变声参数应用"，避免它调用已释放的引擎
        _engine.Dispose();
        _devices.Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>
    /// 只保存窗口**位置**（尺寸固定，不再保存；配置里那两个尺寸字段保留是为了兼容旧配置）。
    /// </summary>
    private void SaveWindowPlacement()
    {
        if (WindowState != WindowState.Normal) return;
        _config.WindowLeft = Left;
        _config.WindowTop = Top;
    }

    // =============================================================== 配置持久化

    private void ScheduleSave()
    {
        if (_saveTimer == null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _saveTimer.Tick += OnSaveTick;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnSaveTick(object? sender, EventArgs e)
    {
        _saveTimer?.Stop();
        SaveConfig();
    }

    private void SaveConfig()
    {
        SaveWindowPlacement();
        _config.Player.Tracks = _tracks.Select(t => t.Model).ToList();
        _store.Save(_config);
    }
}
