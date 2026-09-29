using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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

    private readonly Rectangle[] _inputBars = new Rectangle[SpectrumBars];
    private readonly Rectangle[] _outputBars = new Rectangle[SpectrumBars];
    private readonly float[] _inputBands = new float[SpectrumBars];
    private readonly float[] _outputBands = new float[SpectrumBars];
    private readonly List<TrackViewModel> _tracks = new();

    private bool _loading = true;
    private bool _exiting;
    private string? _pendingHotkeyGesture;
    private TrackViewModel? _pendingHotkeyTrack;
    private TrackViewModel? _pendingHoldKeyTrack;
    private Action? _statusAction;

    /// <summary>开机自启（命令行 --autostart）时置 true：启动完成后直接收进托盘，不弹主窗口。</summary>
    public bool StartMinimizedToTray { get; init; }

    public MainWindow()
    {
        _config = _store.Load();
        _engine = new AudioEngine(_devices, _config);

        InitializeComponent();

        // 主题必须在窗口第一次渲染之前定下来，否则会先闪一下浅色。
        ApplyThemeToResources();

        ApplyWindowIcon();
        ApplyCaptionIcon();
        RestoreWindowPlacement();
        VersionText.Text = App.VersionText;
        BuildSpectrum(InputSpectrumCanvas, _inputBars);
        BuildSpectrum(OutputSpectrumCanvas, _outputBars);

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
            _tray.ToggleProcessingRequested += (_, _) => Dispatcher.BeginInvoke(new Action(ToggleProcessing_Click));
            _tray.ExitRequested += (_, _) => Dispatcher.BeginInvoke(new Action(ExitApplication));

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
                    Hide();
                    Log.Info("以 --autostart 启动：已直接最小化到托盘。");
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
    private static bool IsSelfCheckMode => App.IsSelfCheckRun;

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
            SelectToneChip(_config.Tone.Style);

            ToggleEffect.IsChecked = _config.Effect.Enabled;
            SelectEffectChip(_config.Effect.Kind);
            EffectAmountSlider.Value = _config.Effect.Amount;

            ToggleGain.IsChecked = _config.Gain.Enabled;
            GainSlider.Value = _config.Gain.GainDb;

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

    private void BuildSpectrum(Canvas canvas, Rectangle[] bars)
    {
        canvas.Children.Clear();
        // 每根柱子两片：浅灰底槽（给出"刻度"参照）+ 前景条（真正的电平）。
        // 只有前景条会改高度，底槽固定铺满，视觉上更容易看出起伏幅度。
        for (var i = 0; i < bars.Length; i++)
        {
            var track = new Rectangle
            {
                // 兜底色（资源缺失时用）；紧接着改成资源引用，
                // 这样切换深色模式时底槽颜色会自动跟着变
                Fill = new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xEE)),
                RadiusX = 1,
                RadiusY = 1,
                Height = 3,
                Width = 4,
            };
            track.SetResourceReference(Rectangle.FillProperty, "SpectrumSlotBrush");
            canvas.Children.Add(track);

            var bar = new Rectangle
            {
                Fill = BarBrush(),
                RadiusX = 1,
                RadiusY = 1,
                Height = 1,
                Width = 4,
            };
            bars[i] = bar;
            canvas.Children.Add(bar);
        }

        canvas.SizeChanged += (_, _) => LayoutSpectrum(canvas, bars);
        LayoutSpectrum(canvas, bars);
    }

    /// <summary>
    /// 频谱柱的填充：从底部的青色渐变到顶部的主题蓝（静态画刷，不随每帧变化，
    /// 因此不会给 30 fps 的渲染循环增加开销）。
    /// </summary>
    private static Brush BarBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 1),
            EndPoint = new Point(0.5, 0),
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x4F, 0xC3, 0xC9), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x2F, 0x80, 0xED), 1));
        brush.Freeze();
        return brush;
    }

    private static void LayoutSpectrum(Canvas canvas, Rectangle[] bars)
    {
        var available = canvas.ActualWidth;
        if (available <= 1) return;

        var slot = available / bars.Length;
        var width = Math.Max(2, slot * 0.62);
        var height = Math.Max(3, canvas.ActualHeight);
        foreach (var child in canvas.Children.OfType<Rectangle>())
        {
            child.Width = width;
            child.Height = height;
        }

        for (var i = 0; i < bars.Length; i++)
        {
            var left = i * slot + (slot - width) / 2;
            bars[i].Height = Math.Max(2f, bars[i].Height);
            Canvas.SetLeft(bars[i], left);
            Canvas.SetTop(bars[i], 0);

            // 底槽与前景条成对添加（见 BuildSpectrum），位置始终一致
            var index = canvas.Children.IndexOf(bars[i]);
            if (index > 0 && canvas.Children[index - 1] is Rectangle track)
            {
                Canvas.SetLeft(track, left);
                Canvas.SetTop(track, 0);
            }
        }
    }

    private static void UpdateBarHeights(Canvas canvas, Rectangle[] bars, float[] values)
    {
        var height = canvas.ActualHeight;
        if (height <= 1) return;

        for (var i = 0; i < bars.Length && i < values.Length; i++)
        {
            var value = Math.Clamp(values[i], 0f, 1f);
            // 最低也保留 2px：完全贴底时看不出"这里有一根柱子"
            var barHeight = Math.Max(2f, value * height);
            bars[i].Height = barHeight;
            Canvas.SetTop(bars[i], height - barHeight);
        }
    }

    // =============================================================== 渲染循环

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return;

        _engine.UpdateAnalysis();
        _engine.InputSpectrum.CopyBands(_inputBands, _inputBands.Length);
        _engine.OutputSpectrum.CopyBands(_outputBands, _outputBands.Length);

        UpdateBarHeights(InputSpectrumCanvas, _inputBars, _inputBands);
        UpdateBarHeights(OutputSpectrumCanvas, _outputBars, _outputBands);
        UpdateLevelMeter();
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
            (ExpandTone, ToneGrid),
            (ExpandEffect, PanelCreative),
            (ExpandGain, PanelGain),
        };

        foreach (var (button, panel) in panels)
        {
            if (panel.Visibility != Visibility.Visible)
                OnExpandClick(button, new RoutedEventArgs());
        }
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

    private void UpdateLevelMeter()
    {
        var level = Math.Clamp(_engine.OutputSpectrum.Rms * 3.2f, 0f, 1f);
        var peak = Math.Clamp(_engine.OutputSpectrum.Peak * 3.2f, 0f, 1f);
        if (LevelMask.Parent is not FrameworkElement track) return;

        var width = track.ActualWidth;
        if (width <= 0) return;

        LevelMask.HorizontalAlignment = HorizontalAlignment.Right;
        LevelMask.Width = width * (1 - level);
        LevelNeedle.Margin = new Thickness(Math.Clamp(width * peak, 0, Math.Max(0, width - 2)), 0, 0, 0);
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
        ToggleSoundPlayer.Play(_config.AudioProcessingEnabled);
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
                    Content = device.IsDefault ? device.Name + "（默认）" : device.Name,
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
            "ExpandTone" => ToneGrid,
            "ExpandEffect" => PanelCreative,
            "ExpandGain" => PanelGain,
            _ => null,
        };

        if (panel == null) return;

        var expanded = panel.Visibility != Visibility.Visible;
        panel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;

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
        }

        SaveConfig();   // 展开状态也持久化，下次启动保持原样
    }

    /// <summary>把配置里记录的展开状态应用到各模块面板与箭头。</summary>
    private void SyncExpanderArrows()
    {
        void Apply(UIElement panel, bool expanded)
            => panel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;

        Apply(PanelGate, _config.Panels.Gate);
        Apply(PanelDenoise, _config.Panels.Denoise);
        Apply(PanelLoudness, _config.Panels.Loudness);
        Apply(ToneGrid, _config.Panels.Tone);
        Apply(PanelCreative, _config.Panels.Effect);
        Apply(PanelGain, _config.Panels.Gain);

        // 箭头朝向完全由 XAML 里的 Tag 绑定驱动（见 Ui/Theme.xaml 的 ExpanderButton）。
        // 这里**不能**再写 arrow.Tag = true：给已有 OneWay 绑定的依赖属性赋局部值会
        // 直接把绑定顶掉，之后箭头就再也不跟随展开状态了。
        GateExpanded = _config.Panels.Gate;
        DenoiseExpanded = _config.Panels.Denoise;
        LoudnessExpanded = _config.Panels.Loudness;
        ToneExpanded = _config.Panels.Tone;
        EffectExpanded = _config.Panels.Effect;
        GainExpanded = _config.Panels.Gain;
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

        _engine.UpdateAllParameters();
        SaveConfig();
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || sender is not Slider slider) return;
        var value = (float)e.NewValue;

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
            default:
                return;
        }

        _engine.UpdateAllParameters();
        ScheduleSave();
    }

    /// <summary>按配置勾选音色风格；Style 为 null 时全部不选中。</summary>
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

        _config.Tone.Style = style;
        // 用户主动选了预设即视为要使用该模块
        if (ToggleTone.IsChecked != true) ToggleTone.IsChecked = true;

        _engine.UpdateAllParameters();
        SaveConfig();
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
    /// 使用指南：把"三个设备到底该选什么"和基本操作讲清楚。
    /// 这是新人最容易卡住的地方——输入/输出/监听三栏在没有 MIXLINE 概念之前完全无从下手。
    /// </summary>
    private void OnUsageGuideClick(object sender, RoutedEventArgs e)
    {
        const string guide =
            "一、Windows 声音设置\n\n" +
            "将麦克风 MIXLINE Stream 设为默认输入设备。\n\n" +
            "二、设备选择\n\n" +
            "输入选择：实际在用的物理麦克风\n" +
            "输出选择：扬声器 (MIXLINE)\n" +
            "监听选择：实际在用的物理扬声器\n\n" +
            "三、MIXLINE 中\n\n" +
            "添加输入：MateMic\n" +
            "添加输出：MIXLINE Stream\n" +
            "将 MateMic 节点连接至 MIXLINE Stream 节点";

        DialogHost.Info(this, "MateMic 使用指南", guide);
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
        UpdateTrackListHint();
    }

    /// <summary>
    /// 列表为空时显示提示文案（效果图里的文件名是示例内容，不作为默认值）。
    /// </summary>
    private void UpdateTrackListHint()
        => TrackListEmptyHint.Visibility = _tracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnAddFilesClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "添加音频文件",
            Multiselect = true,
            Filter = "音频文件|*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac;*.ogg|所有文件|*.*",
        };

        if (dialog.ShowDialog(this) != true) return;

        foreach (var file in dialog.FileNames)
        {
            if (_tracks.Any(t => string.Equals(t.FilePath, file, StringComparison.OrdinalIgnoreCase))) continue;

            var model = new PlayerTrack
            {
                Path = file,
                DisplayName = Path.GetFileName(file),
                Hotkey = string.Empty,
            };
            _config.Player.Tracks.Add(model);
            _tracks.Add(new TrackViewModel(model));
        }

        TrackList.Items.Refresh();
        UpdateTrackListHint();
        SaveConfig();
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

    /// <summary>「同步按住键」总开关。开启前先弹一次风险说明，用户确认后才生效。</summary>
    private void OnHoldKeyToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var enabled = ToggleHoldKey.IsChecked == true;
        if (enabled && !_config.Player.EnableHoldKey && !ConfirmHoldKeyRisk())
        {
            // 用户没有确认风险：把开关拨回去（不会再次触发本方法，因为 _loading 期间忽略回调）
            _loading = true;
            ToggleHoldKey.IsChecked = false;
            _loading = false;
            return;
        }

        _config.Player.EnableHoldKey = enabled;
        foreach (var track in _tracks) track.HoldKeyFeatureEnabled = enabled;

        if (!enabled) _keyboard.Release();

        UpdateTrackListHint();
        ShowStatus(enabled
            ? "「同步按住键」已启用：现在可以为每个音频文件设置一个自动按住的按键。"
            : "「同步按住键」已关闭。", false);
        SaveConfig();
    }

    private void OnHoldKeyRiskClick(object sender, RoutedEventArgs e) => ShowHoldKeyRisk(false);

    /// <summary>开启前的确认框；返回 true 表示用户接受风险。</summary>
    private bool ConfirmHoldKeyRisk()
    {
        return DialogHost.Confirm(this,
            "同步按住键 · 风险说明",
            HoldKeyRiskText + "\n\n是否启用「同步按住键」？");
    }

    private void ShowHoldKeyRisk(bool warning)
    {
        var text = HoldKeyRiskText + (warning ? "\n\n当前开关：已启用。" : "\n\n当前开关：" + (_config.Player.EnableHoldKey ? "已启用。" : "未启用。"));
        if (warning) DialogHost.Warn(this, "同步按住键 · 风险说明", text);
        else DialogHost.Info(this, "同步按住键 · 风险说明", text);
    }

    private const string HoldKeyRiskText =
        "「同步按住键」会用 Win32 SendInput 向系统注入按键事件（按下 / 松开）：\n" +
        "播放某个音频文件之前自动按下你指定的按键，播放结束后自动松开。\n" +
        "典型用法：把该键设成游戏或语音软件里「按键说话」的那个键，\n" +
        "这样按快捷键播放语音包时就不必再手动按住说话键。\n\n" +
        "关于反作弊：\n" +
        "· SendInput 注入的事件带有 LLKHF_INJECTED 标记，属于「软件模拟输入」。\n" +
        "· 内核级反作弊（Riot Vanguard、Easy Anti-Cheat、BattlEye、FACEIT 等）有能力识别这类事件；\n" +
        "  是否判定为违规由厂商的策略决定，【这个风险无法排除】。\n" +
        "· 本功能只发送你自己录入的那一个按键，不连发、不循环、不读写任何其它进程、不注入代码。\n" +
        "· 如果游戏对模拟输入查得很严，请不要使用本功能；\n" +
        "  可改用硬件级方案（键盘宏 / 脚踏开关 / 手柄映射）达到同样的效果。\n\n" +
        "因此该功能默认关闭，需要你自己开启。";

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
        _tracks.Remove(track);
        _config.Player.Tracks.Remove(track.Model);
        TrackList.Items.Refresh();
        UpdateTrackListHint();
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

    private void ShowMixLineGuide()
    {
        const string guide =
            "MIXLINE 配合设置步骤\n\n" +
            "1. 安装并打开 MIXLINE。\n" +
            "2. 在 MIXLINE 中新建一个输入通道，选择 MateMic 的主输出设备（即「扬声器 (MIXLINE)」）作为输入源。\n" +
            "3. 将该通道路由到 MIXLINE Stream（虚拟麦克风）。\n" +
            "4. 在 Windows 声音设置中，把默认录音设备设为 MIXLINE Stream。\n" +
            "5. 回到 MateMic，把「输出」选择为 MIXLINE 的虚拟播放设备。\n" +
            "6. 在语音软件（Discord / 微信 / QQ / 游戏语音）中把麦克风选为 MIXLINE Stream。\n\n" +
            "提示：MateMic 自身不创建虚拟声卡，必须配合 MIXLINE 使用。";

        DialogHost.Info(this, "MIXLINE 设置指南", guide);
    }

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
