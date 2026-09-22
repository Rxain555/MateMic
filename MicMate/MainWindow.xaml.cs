using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MicMate.Audio;
using MicMate.Core;
using MicMate.Denoise;
using MicMate.Dsp;
using MicMate.ViewModels;
using MicMate.Windows;
using NAudio.CoreAudioApi;

using Rectangle = System.Windows.Shapes.Rectangle;

namespace MicMate;

public partial class MainWindow : Window, System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));

    private const int SpectrumBars = 48;
    private const string HotkeyPlaceholder = "点击输入快捷键";

    private readonly ConfigStore _store = new();
    private readonly AppConfig _config;
    private readonly DeviceService _devices = new();
    private readonly AudioEngine _engine;
    private readonly FilePlayerService _player = new();
    private readonly HotkeyService _hotkeys = new();
    private readonly TrayIconService _tray = new();
    private readonly DispatcherTimer _timer;
    private DispatcherTimer? _saveTimer;

    private readonly Rectangle[] _inputBars = new Rectangle[SpectrumBars];
    private readonly Rectangle[] _outputBars = new Rectangle[SpectrumBars];
    private readonly float[] _inputBands = new float[SpectrumBars];
    private readonly float[] _outputBands = new float[SpectrumBars];
    private readonly List<TrackViewModel> _tracks = new();

    private bool _loading = true;
    private bool _exiting;
    private string? _pendingHotkeyGesture;
    private TrackViewModel? _pendingHotkeyTrack;
    private Action? _statusAction;

    public MainWindow()
    {
        _config = _store.Load();
        _engine = new AudioEngine(_devices, _config);

        InitializeComponent();

        ApplyWindowIcon();
        RestoreWindowPlacement();
        VersionText.Text = App.VersionText;
        BuildSpectrum(InputSpectrumCanvas, _inputBars);
        BuildSpectrum(OutputSpectrumCanvas, _outputBars);

        TrackList.ItemsSource = _tracks;
        LoadTracksFromConfig();

        _devices.DevicesChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            RefreshDeviceLists();
            UpdateMixLineStatus();
        }));

        _player.StateChanged += (_, _) => Dispatcher.BeginInvoke(new Action(RefreshTrackHighlight));


        _player.LoopEnabled = _config.Player.Loop;
        _player.SetVolumePercent(_config.Player.Volume);
        _engine.MicMixer.SetPlayer(_player.Output);
        _engine.MicMixer.PlayerEnabled = _config.Player.MonitorTogether;

        Loaded += OnLoaded;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnRenderTick;
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

    /// <summary>按配置里的模型名在当前模型列表中查找。</summary>
    private DenoiseModelInfo? FindConfiguredModel()
    {
        if (string.IsNullOrWhiteSpace(_config.Denoise.Model)) return null;

        return ModelCatalog.Scan().FirstOrDefault(m =>
            string.Equals(m.Name, _config.Denoise.Model, StringComparison.Ordinal) ||
            string.Equals(Path.GetFileNameWithoutExtension(m.Path), _config.Denoise.Model,
                StringComparison.Ordinal));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _hotkeys.Attach(this);
            _tray.Show("MicMate");
            _tray.ShowRequested += (_, _) => RestoreFromTray();
            _tray.ToggleProcessingRequested += (_, _) => Dispatcher.BeginInvoke(new Action(ToggleProcessing_Click));
            _tray.ExitRequested += (_, _) => Dispatcher.BeginInvoke(new Action(ExitApplication));

            RefreshDeviceLists();
            ApplyConfigToControls();
            RefreshModels();

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

            // 降噪已开启时给出明确提示，便于确认"重启后到底有没有生效"
            if (_config.Denoise.Enabled && _config.AudioProcessingEnabled)
                ShowStatus($"降噪已启用：{_engine.DenoiseModelName}", false);



            RegisterConfiguredHotkeys();
            RefreshTrackHighlight();
            _timer.Start();
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

    private void RestoreWindowPlacement()    {
        Width = Math.Max(MinWidth, _config.WindowWidth);
        Height = Math.Max(MinHeight, _config.WindowHeight);
        if (double.IsNaN(_config.WindowLeft) || double.IsNaN(_config.WindowTop)) return;

        var virtualWidth = SystemParameters.VirtualScreenWidth;
        var virtualHeight = SystemParameters.VirtualScreenHeight;
        if (_config.WindowLeft > -50 && _config.WindowLeft < virtualWidth - 100 &&
            _config.WindowTop > -50 && _config.WindowTop < virtualHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _config.WindowLeft;
            Top = _config.WindowTop;
        }
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
            ToggleTray.IsChecked = _config.MinimizeToTray;

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

            TogglePlayerMonitor.IsChecked = _config.Player.MonitorTogether;
            ToggleLoop.IsChecked = _config.Player.Loop;
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
        for (var i = 0; i < bars.Length; i++)
        {
            var bar = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xA8)),
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

    private static void LayoutSpectrum(Canvas canvas, Rectangle[] bars)
    {
        var available = canvas.ActualWidth;
        if (available <= 1) return;

        var slot = available / bars.Length;
        var width = Math.Max(2, slot * 0.62);
        for (var i = 0; i < bars.Length; i++)
        {
            bars[i].Width = width;
            Canvas.SetLeft(bars[i], i * slot + (slot - width) / 2);
        }
    }

    private static void UpdateBarHeights(Canvas canvas, Rectangle[] bars, float[] values)
    {
        var height = canvas.ActualHeight;
        if (height <= 1) return;

        for (var i = 0; i < bars.Length && i < values.Length; i++)
        {
            var value = Math.Clamp(values[i], 0f, 1f);
            var barHeight = Math.Max(1f, value * height);
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
    /// 自检专用：把全部模块展开。默认状态是所有模块折叠、所有开关关闭，
    /// 因此截图校验展开态时需要显式调用（每个模块点三次：展开→折叠→再展开，验证可来回切换）。
    /// </summary>
    public void ExpandAllForSelfCheck()
    {
        foreach (var button in new[] { ExpandGate, ExpandDenoise, ExpandLoudness, ExpandTone, ExpandEffect, ExpandGain })
        {
            OnExpandClick(button, new RoutedEventArgs());
            OnExpandClick(button, new RoutedEventArgs());
            OnExpandClick(button, new RoutedEventArgs());
        }
    }

    /// <summary>自检专用：向两路分析器注入合成频谱与电平，用于离线校验渲染链路。</summary>
    public void FeedSelfCheckSignal(float phase)
    {
        const int frames = 2048;
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
        _config.MinimizeToTray = ToggleTray.IsChecked == true;
        SaveConfig();
    }

    private void OnGlobalHotkeyClick(object sender, RoutedEventArgs e)
    {
        // 正在为某个音频文件录入快捷键时，不要被工具栏按钮抢走录入状态
        if (_pendingHotkeyTrack != null)
        {
            ShowStatus("正在为该音频文件录入快捷键，请先按键（Esc 取消）。", false);
            return;
        }

        _pendingHotkeyGesture = string.Empty;
        _pendingHotkeyTrack = null;
        HotkeyButton.Content = "按下快捷键…";
        ShowStatus("正在为总开关录入全局快捷键：请按键（Esc 取消，Backspace 清除）", false);
        Focus();
        Keyboard.Focus(this);
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_pendingHotkeyGesture == null && _pendingHotkeyTrack == null) return;

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        Log.Debug($"快捷键录入：WPF Key={e.Key}，采用 Key={key}，修饰键={Keyboard.Modifiers}");

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        if (key == Key.Escape)
        {
            CancelHotkeyCapture();
            return;
        }

        if (key == Key.Back || key == Key.Delete)
        {
            ApplyHotkey(string.Empty);
            return;
        }

        // 先走常规映射（字母/数字/功能键/常见符号键）
        var gesture = HotkeyService.FromWpfKey(key, Keyboard.Modifiers);

        // 常规映射失败时，才走"由虚拟键解字符 → 反查物理键"的兜底路径。
        // 中文输入法下按 `、`『』等符号键时，WPF 只给出 ImeProcessed，
        // 这条路能把它们还原成物理按键（例如 `、` → `\`）。
        if (string.IsNullOrEmpty(gesture))
        {
            var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
            var typed = HotkeyService.CharacterFromVirtualKey(virtualKey);
            var symbol = HotkeyService.SymbolFromCharacter(typed);
            var fromLast = HotkeyService.SymbolFromCharacter(_lastInputCharacter);

            Log.Info($"符号键录入：WPF Key={key}，虚拟键={virtualKey}，解出字符=「{typed}」" +
                     $"（U+{(typed.Length > 0 ? ((int)typed[0]).ToString("X4") : "----")}）" +
                     $"，映射结果=「{symbol}」，最近输入字符=「{_lastInputCharacter}」→「{fromLast}」");

            if (string.IsNullOrEmpty(symbol)) symbol = fromLast;
            if (!string.IsNullOrEmpty(symbol))
            {
                var prefix = HotkeyService.ModifierPrefix(Keyboard.Modifiers);
                gesture = string.IsNullOrEmpty(prefix) ? symbol : prefix + " + " + symbol;
            }
        }

        if (string.IsNullOrEmpty(gesture))
        {
            ShowStatus($"无法识别这个按键（WPF Key={key}）。请改用字母 / 数字 / 功能键，或告诉我上面日志里显示的字符。", false);
            return;
        }

        ApplyHotkey(gesture);
    }

    /// <summary>最近一次 TextInput 收到的字符（用于中文输入法下的符号键识别）。</summary>
    private string _lastInputCharacter = string.Empty;

    private void OnWindowTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        _lastInputCharacter = e.Text;

        // 录入快捷键期间不要把这些字符送进任何输入框
        if (_pendingHotkeyGesture != null || _pendingHotkeyTrack != null)
            e.Handled = true;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // 快捷键录入统一由 PreviewKeyDown 处理
    }

    private void CancelHotkeyCapture()
    {
        _pendingHotkeyGesture = null;
        _pendingHotkeyTrack = null;
        SetHotkeyButtonText(_config.ToggleHotkey);
        HideStatus();
    }

    private void ApplyHotkey(string gesture)
    {
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

            SaveConfig();
            RefreshTrackHighlight();
            return;
        }

        CancelHotkeyCapture();
        if (string.IsNullOrWhiteSpace(gesture)) return;

        _config.ToggleHotkey = gesture;
        SetHotkeyButtonText(gesture);
        RegisterConfiguredHotkeys();
        SaveConfig();
    }

    // =============================================================== 设备

    private void OnDeviceDropDownOpened(object sender, EventArgs e) => RefreshDeviceLists(sender as ComboBox);

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
                .DefaultIfEmpty(devices.Count > 0 ? 0 : -1)
                .First();

            combo.SelectedIndex = index;
        }
        finally
        {
            _loading = previous;
        }
    }

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        var inputId = SelectedDeviceId(InputDeviceCombo);
        var outputId = SelectedDeviceId(OutputDeviceCombo);
        var monitorId = SelectedDeviceId(MonitorDeviceCombo);

        if (inputId == _config.Devices.InputDeviceId &&
            outputId == _config.Devices.OutputDeviceId &&
            monitorId == _config.Devices.MonitorDeviceId)
            return;

        Log.Info("设备发生切换，正在重启音频流…");
        _engine.Reconfigure(inputId, outputId, monitorId);
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
        void Apply(UIElement panel, Button arrow, bool expanded)
        {
            panel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            if (expanded) arrow.Tag = true;
        }

        Apply(PanelGate, ExpandGate, _config.Panels.Gate);
        Apply(PanelDenoise, ExpandDenoise, _config.Panels.Denoise);
        Apply(PanelLoudness, ExpandLoudness, _config.Panels.Loudness);
        Apply(ToneGrid, ExpandTone, _config.Panels.Tone);
        Apply(PanelCreative, ExpandEffect, _config.Panels.Effect);
        Apply(PanelGain, ExpandGain, _config.Panels.Gain);

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

            var current = models.FirstOrDefault(m =>
                              string.Equals(m.Name, _config.Denoise.Model, StringComparison.Ordinal)) ??
                          models.FirstOrDefault(m =>
                              string.Equals(Path.GetFileNameWithoutExtension(m.Path), _config.Denoise.Model,
                                  StringComparison.Ordinal));

            if (current == null)
            {
                current = models[0];
                if (!string.IsNullOrWhiteSpace(_config.Denoise.Model) &&
                    _config.Denoise.Model != ModelCatalog.DefaultModelName)
                {
                    Log.Warn($"当前降噪模型 {_config.Denoise.Model} 不存在，已回退到默认模型。");
                    ShowStatus($"当前降噪模型已被删除，已自动回退到「{current.Name}」。", false);
                }

                _config.Denoise.Model = current.Name;
            }

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

        _config.Denoise.Model = info.Name;
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

    // =============================================================== 播放器

    private void LoadTracksFromConfig()
    {
        _tracks.Clear();
        foreach (var track in _config.Player.Tracks)
        {
            if (string.IsNullOrWhiteSpace(track.Path)) continue;
            _tracks.Add(new TrackViewModel(track));
        }

        TrackList.Items.Refresh();
        UpdateTrackListHint();
    }

    /// <summary>列表为空时显示提示文案（效果图里的文件名是示例内容，不作为默认值）。</summary>
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

        _pendingHotkeyTrack = track;
        _pendingHotkeyGesture = string.Empty;
        // 按钮宽度固定，因此这里用简短文案（完整信息走状态栏），避免把工具栏顶高
        HotkeyButton.Content = "按下快捷键…";
        ShowStatus($"正在为「{track.DisplayName}」录入快捷键：请按键（Esc 取消，Backspace 清除）", false);
        Focus();
        Keyboard.Focus(this);
    }

    private (bool Ok, string? Error) RegisterTrackHotkey(TrackViewModel track, string gesture)
    {
        var result = _hotkeys.Register(gesture, () => PlayTrack(track));
        if (!result.Success) return (false, result.Error);

        track.Hotkey = gesture;
        Log.Info($"已为 {track.DisplayName} 绑定全局快捷键 {gesture}");
        return (true, null);
    }

    private void RegisterConfiguredHotkeys()
    {
        _hotkeys.UnregisterAll();

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

    private void OnTrackRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is TrackViewModel track)
            PlayTrack(track);
    }

    /// <summary>从播放列表移除一个音频文件（同时注销它的全局快捷键）。</summary>
    private void OnTrackRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not TrackViewModel track) return;

        var confirm = MessageBox.Show(this,
            $"确定要从列表移除「{track.DisplayName}」吗？\n（只移除列表条目，不会删除磁盘上的文件）",
            "移除音频文件", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        if (ReferenceEquals(_pendingHotkeyTrack, track)) _pendingHotkeyTrack = null;
        _tracks.Remove(track);
        _config.Player.Tracks.Remove(track.Model);
        TrackList.Items.Refresh();
        UpdateTrackListHint();
        RegisterConfiguredHotkeys();   // 释放该条目占用的全局快捷键
        SaveConfig();
        Log.Info("已从播放列表移除：" + track.FilePath);
    }

    private void OnPlayerMonitorToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _config.Player.MonitorTogether = TogglePlayerMonitor.IsChecked == true;
        _engine.MicMixer.PlayerEnabled = _config.Player.MonitorTogether;
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

    private void ShowStatus(string message, bool showGuideButton)
    {
        StatusText.Text = message;
        StatusBar.Visibility = Visibility.Visible;
        StatusActionButton.Visibility = showGuideButton ? Visibility.Visible : Visibility.Collapsed;
        _statusAction = showGuideButton ? ShowMixLineGuide : null;
        Log.Info("[状态] " + message);
    }

    private void HideStatus() => StatusBar.Visibility = Visibility.Collapsed;

    private void OnStatusActionClick(object sender, RoutedEventArgs e) => _statusAction?.Invoke();

    private void ShowMixLineGuide()
    {
        const string guide =
            "MIXLINE 配合设置步骤\n\n" +
            "1. 安装并打开 MIXLINE。\n" +
            "2. 在 MIXLINE 中新建一个输入通道，选择 MicMate 的主输出设备（即「扬声器 (MIXLINE)」）作为输入源。\n" +
            "3. 将该通道路由到 MIXLINE Stream（虚拟麦克风）。\n" +
            "4. 在 Windows 声音设置中，把默认录音设备设为 MIXLINE Stream。\n" +
            "5. 回到 MicMate，把「输出」选择为 MIXLINE 的虚拟播放设备。\n" +
            "6. 在语音软件（Discord / 微信 / QQ / 游戏语音）中把麦克风选为 MIXLINE Stream。\n\n" +
            "提示：MicMate 自身不创建虚拟声卡，必须配合 MIXLINE 使用。";

        MessageBox.Show(this, guide, "MIXLINE 设置指南", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting) return;

        if (_config.MinimizeToTray)
        {
            // 关闭窗口 = 收进托盘，不再弹系统提示（托盘双击即可恢复）
            e.Cancel = true;
            SaveWindowPlacement();
            SaveConfig();
            Hide();
            return;
        }

        ExitApplication();
    }

    private void OnWindowStateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _config.MinimizeToTray && !_exiting)
        {
            // 最小化 = 收进托盘，不弹任何系统提示
            Hide();
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;

        SaveWindowPlacement();
        SaveConfig();
        _timer.Stop();
        _saveTimer?.Stop();
        _hotkeys.Dispose();
        _tray.Dispose();
        _player.Dispose();
        _engine.Dispose();
        _devices.Dispose();
        Application.Current.Shutdown();
    }

    private void SaveWindowPlacement()
    {
        if (WindowState != WindowState.Normal) return;
        _config.WindowWidth = Width;
        _config.WindowHeight = Height;
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
