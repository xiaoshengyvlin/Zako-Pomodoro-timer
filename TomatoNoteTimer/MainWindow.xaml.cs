using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using TomatoNoteTimer.Dialogs;
using TomatoNoteTimer.Models;
using TomatoNoteTimer.Services;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaPoint = System.Windows.Point;
using MsOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfApplication = System.Windows.Application;
using WpfMessageBox = System.Windows.MessageBox;

namespace TomatoNoteTimer;

public partial class MainWindow : Window
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "TomatoNoteTimer";
    private const string DefaultPresetAudioFileName = "音乐预设.mp3";
    private const string DefaultIconFileName = "icon.ico";
    private const string DefaultNoteText = "人生若只如初见，何事秋风悲画扇";
    private const string RepositoryUrl = "https://github.com/xiaoshengyvlin/Zako-Pomodoro-timer";
    private const int WmHotKey = 0x0312;
    private const int HotkeyIdBase = 3000;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private static readonly int[] QuickPresetMinutes = { 1, 5, 10, 15, 25, 30 };
    private static readonly HotkeyAction[] SupportedHotkeyActions =
    {
        HotkeyAction.StartTimer,
        HotkeyAction.PauseTimer,
        HotkeyAction.ResetTimer,
        HotkeyAction.ToggleSimpleMode,
        HotkeyAction.ToggleTopMost,
        HotkeyAction.ToggleFixedMode
    };
    private const long MemoryTrimWorkingSetThresholdBytes = 45L * 1024 * 1024;
    private const long MemoryTrimPrivateThresholdBytes = 130L * 1024 * 1024;
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private enum TimerPhase
    {
        Work,
        Rest
    }

    private enum HotkeyAction
    {
        StartTimer = 1,
        PauseTimer = 2,
        ResetTimer = 3,
        ToggleSimpleMode = 4,
        ToggleTopMost = 5,
        ToggleFixedMode = 6
    }

    private readonly DispatcherTimer _countdownTimer;
    private readonly DispatcherTimer _noteRotationTimer;
    private readonly DispatcherTimer _memoryGuardTimer;
    private MediaPlayer? _loopPlayer;
    private MediaPlayer? _endPlayer;
    private readonly ConfigService _configService;

    private AppState _state;
    private WinForms.NotifyIcon? _notifyIcon;
    private WinForms.ContextMenuStrip? _leftPresetMenu;
    private Drawing.Icon? _trayIcon;
    private bool _isRunning;
    private bool _isExiting;
    private bool _loopSourceLoaded;
    private string _activeLoopPath = string.Empty;
    private int _remainingSeconds;

    private List<TimerPhase> _phaseSequence = new();
    private TimerPhase _currentPhase = TimerPhase.Work;
    private int _phaseIndexInCycle;
    private int _currentCycle = 1;
    private int _totalCycles = 1;
    private bool _sessionChainActive;

    private WinForms.ToolStripMenuItem? _topMostItem;
    private WinForms.ToolStripMenuItem? _fixedModeItem;
    private WinForms.ToolStripMenuItem? _rotationEnabledItem;
    private WinForms.ToolStripMenuItem? _webOpenEnabledItem;
    private WinForms.ToolStripMenuItem? _autoStartItem;
    private WinForms.ToolStripMenuItem? _minimizeToTrayItem;
    private WinForms.ToolStripMenuItem? _loopAudioItem;
    private WinForms.ToolStripMenuItem? _endAudioItem;
    private WinForms.ToolStripMenuItem? _restEndAudioItem;
    private WinForms.ToolStripMenuItem? _loopAudioEnabledItem;
    private WinForms.ToolStripMenuItem? _workEndAudioEnabledItem;
    private WinForms.ToolStripMenuItem? _restEndAudioEnabledItem;
    private WinForms.ToolStripMenuItem? _workSessionEnabledItem;
    private WinForms.ToolStripMenuItem? _restSessionEnabledItem;
    private WinForms.ToolStripMenuItem? _simpleModeItem;
    private readonly Dictionary<string, WinForms.ToolStripMenuItem> _backgroundModeItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, HotkeyAction> _registeredHotkeys = new();
    private double _lastAppliedTransparencyPercent = -1;
    private bool _suppressCheckCallbacks;
    private bool _suspendedForTray;
    private bool _noteRotationRunningBeforeSuspend;
    private DateTime _lastMemoryTrimUtc = DateTime.MinValue;
    private bool _memoryPressureMode;
    private bool _isEndAudioPlaying;
    private IntPtr _windowHandle;
    private HwndSource? _windowSource;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += MainWindow_SourceInitialized;

        _configService = new ConfigService(AppContext.BaseDirectory);
        _state = _configService.Load();
        InitializeWorkflowStateFromConfig();
        EnsureDefaultEndAudioPaths();

        _countdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _countdownTimer.Tick += CountdownTimer_Tick;

        _noteRotationTimer = new DispatcherTimer();
        _noteRotationTimer.Tick += NoteRotationTimer_Tick;

        _memoryGuardTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _memoryGuardTimer.Tick += MemoryGuardTimer_Tick;
        _memoryGuardTimer.Start();

        LoadWindowIcon();
        ApplyStateToUi();
        ConfigureNoteRotationTimer();
        InitializeTrayIcon();

        _state.App.AutoStart = IsAutoStartEnabled();
        _configService.AppendLog("应用启动");
    }

    private void InitializeWorkflowStateFromConfig()
    {
        _phaseSequence = BuildPhaseSequence();
        _totalCycles = Math.Clamp(_state.Timer.LoopCount, 1, 99);
        _sessionChainActive = _state.Runtime.SessionChainActive;

        _currentPhase = ParsePhase(_state.Runtime.CurrentPhase, _phaseSequence[0]);
        _phaseIndexInCycle = _phaseSequence.FindIndex(x => x == _currentPhase);
        if (_phaseIndexInCycle < 0)
        {
            _phaseIndexInCycle = 0;
            _currentPhase = _phaseSequence[0];
        }

        int runtimeTotal = Math.Clamp(_state.Runtime.TotalCycles, 1, 99);
        _totalCycles = _sessionChainActive ? runtimeTotal : _totalCycles;
        _currentCycle = Math.Clamp(_state.Runtime.CurrentCycle, 1, _totalCycles);

        if (!_sessionChainActive)
        {
            _currentCycle = 1;
            _phaseIndexInCycle = 0;
            _currentPhase = _phaseSequence[0];
        }

        int defaultSeconds = GetDurationForPhase(_currentPhase);
        _remainingSeconds = _state.Runtime.RemainingSeconds > 0
            ? _state.Runtime.RemainingSeconds
            : defaultSeconds;

        if (_remainingSeconds < 0)
        {
            _remainingSeconds = defaultSeconds;
        }

        _state.Timer.DurationSeconds = _state.Timer.WorkDurationSeconds;
        _state.Timer.DurationMinutes = Math.Max(1, _state.Timer.WorkDurationSeconds / 60);
    }

    private List<TimerPhase> BuildPhaseSequence()
    {
        var sequence = new List<TimerPhase>();
        if (_state.Timer.EnableWorkSession)
        {
            sequence.Add(TimerPhase.Work);
        }

        if (_state.Timer.EnableRestSession)
        {
            sequence.Add(TimerPhase.Rest);
        }

        if (sequence.Count == 0)
        {
            _state.Timer.EnableWorkSession = true;
            sequence.Add(TimerPhase.Work);
        }

        return sequence;
    }

    private static TimerPhase ParsePhase(string? raw, TimerPhase fallback)
    {
        return raw?.Trim().ToLowerInvariant() switch
        {
            "work" => TimerPhase.Work,
            "rest" => TimerPhase.Rest,
            _ => fallback
        };
    }

    private int GetDurationForPhase(TimerPhase phase)
    {
        return phase == TimerPhase.Work
            ? Math.Max(1, _state.Timer.WorkDurationSeconds)
            : Math.Max(1, _state.Timer.RestDurationSeconds);
    }

    private static string GetPhaseText(TimerPhase phase)
    {
        return phase == TimerPhase.Work ? "工作时间" : "休息时间";
    }

    private void EnsureDefaultEndAudioPaths()
    {
        string defaultPath = EnsurePresetAudioFile();
        if (string.IsNullOrWhiteSpace(defaultPath) || !File.Exists(defaultPath))
        {
            return;
        }

        bool changed = false;
        bool originalUseCustomWork = _state.Timer.UseCustomEndAudio;
        bool originalUseCustomRest = _state.Timer.UseCustomRestEndAudio;
        _state.Timer.EndAudioPath = ResolveAudioPath(_state.Timer.EndAudioPath, defaultPath, ref changed, out bool useCustomWork);
        _state.Timer.UseCustomEndAudio = useCustomWork;

        _state.Timer.RestEndAudioPath = ResolveAudioPath(_state.Timer.RestEndAudioPath, defaultPath, ref changed, out bool useCustomRest);
        _state.Timer.UseCustomRestEndAudio = useCustomRest;

        if (originalUseCustomWork != _state.Timer.UseCustomEndAudio ||
            originalUseCustomRest != _state.Timer.UseCustomRestEndAudio)
        {
            changed = true;
        }

        if (changed)
        {
            _configService.Save(_state);
        }
    }

    private string ResolveAudioPath(string configuredPath, string defaultPath, ref bool changed, out bool useCustom)
    {
        string normalizedConfigured = configuredPath?.Trim() ?? string.Empty;
        if (string.Equals(normalizedConfigured, DefaultPresetAudioFileName, StringComparison.OrdinalIgnoreCase))
        {
            useCustom = false;
            if (!IsPathMatch(normalizedConfigured, defaultPath))
            {
                changed = true;
            }
            return defaultPath;
        }

        string resolved = ResolveConfiguredAudioPath(normalizedConfigured);
        string legacyDefaultPath = Path.Combine(AppContext.BaseDirectory, DefaultPresetAudioFileName);
        if (!string.IsNullOrWhiteSpace(resolved) &&
            File.Exists(resolved) &&
            IsPathMatch(resolved, legacyDefaultPath))
        {
            useCustom = false;
            if (!IsPathMatch(resolved, defaultPath))
            {
                changed = true;
            }
            return defaultPath;
        }

        if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved))
        {
            useCustom = false;
            if (!IsPathMatch(normalizedConfigured, defaultPath))
            {
                changed = true;
            }
            return defaultPath;
        }

        useCustom = !IsPathMatch(resolved, defaultPath);
        if (!string.Equals(configuredPath, resolved, StringComparison.OrdinalIgnoreCase))
        {
            changed = true;
        }

        return resolved;
    }

    private static string ResolveConfiguredAudioPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(path))
        {
            return path;
        }

        string candidate = Path.Combine(AppContext.BaseDirectory, path);
        return File.Exists(candidate) ? candidate : path;
    }

    private string EnsurePresetAudioFile()
    {
        string outputDirectory = Path.Combine(_configService.AudioDirectory, "countdown_end");
        Directory.CreateDirectory(outputDirectory);

        string outputPath = Path.Combine(outputDirectory, DefaultPresetAudioFileName);
        if (File.Exists(outputPath))
        {
            return outputPath;
        }

        Stream? stream = TryOpenEmbeddedResource(DefaultPresetAudioFileName);
        if (stream is null)
        {
            return string.Empty;
        }

        try
        {
            using (stream)
            using (var file = File.Create(outputPath))
            {
                stream.CopyTo(file);
            }
            return outputPath;
        }
        catch (UnauthorizedAccessException ex)
        {
            _configService.AppendLog($"写入默认音频失败: {ex.Message}");
            return string.Empty;
        }
        catch (IOException ex)
        {
            _configService.AppendLog($"写入默认音频失败: {ex.Message}");
            return string.Empty;
        }
    }

    private static Stream? TryOpenEmbeddedResource(string fileName)
    {
        string? resourceName = typeof(MainWindow)
            .Assembly
            .GetManifestResourceNames()
            .FirstOrDefault(x => x.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return null;
        }

        return typeof(MainWindow).Assembly.GetManifestResourceStream(resourceName);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyVisualEffects();
        ApplyResponsiveLayout();
        UpdateCountdownText();
        UpdateNotesText();
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(TrimMemoryUsage));
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(_windowHandle);
        _windowSource?.AddHook(WindowProc);
        _ = TryApplyConfiguredHotkeys(showDialogOnFailure: false);

        ApplyVisualEffects();
        ApplyResponsiveLayout();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout();
    }

    private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_state.App.FixedMode)
        {
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_state.App.FixedMode)
        {
            return;
        }

        double factor = e.Delta > 0 ? 1.06 : 0.94;
        int nextWidth = (int)Math.Clamp(Width * factor, 140, 960);
        int nextHeight = (int)Math.Clamp(Height * factor, 88, 720);

        Width = nextWidth;
        Height = nextHeight;

        _state.App.WindowWidth = nextWidth;
        _state.App.WindowHeight = nextHeight;
        SaveState();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isExiting && _state.App.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
            SuspendWindowForTray();
            _notifyIcon?.ShowBalloonTip(1200, "番茄钟便签", "已最小化到托盘。", WinForms.ToolTipIcon.Info);
            return;
        }

        UnregisterAllHotkeys();
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WindowProc);
            _windowSource = null;
        }

        PersistRuntimeState();
        DisposeTrayIcon();
    }

    private void InitializeTrayIcon()
    {
        _notifyIcon = new WinForms.NotifyIcon();
        _trayIcon = LoadTrayIcon();

        _notifyIcon.Icon = _trayIcon;
        _notifyIcon.Visible = true;
        _notifyIcon.Text = "番茄钟便签";
        _notifyIcon.MouseUp += NotifyIcon_MouseUp;
        _notifyIcon.ContextMenuStrip = BuildTrayMenu();
        _leftPresetMenu = BuildLeftPresetMenu();
    }

    private void NotifyIcon_MouseUp(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button != WinForms.MouseButtons.Left)
        {
            return;
        }

        DispatchToUi(ShowLeftPresetMenu);
    }

    private void ShowLeftPresetMenu()
    {
        if (_leftPresetMenu is null)
        {
            return;
        }

        _leftPresetMenu.Show(WinForms.Cursor.Position);
    }

    private WinForms.ContextMenuStrip BuildLeftPresetMenu()
    {
        var menu = new WinForms.ContextMenuStrip();
        foreach (int minutes in QuickPresetMinutes)
        {
            menu.Items.Add(CreateMenuItem($"{minutes} 分钟（设置并开始）", () => ApplyWorkPreset(minutes, startImmediately: true)));
        }

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(CreateMenuItem("显示/隐藏窗口", ToggleWindowVisibility));
        return menu;
    }

    private WinForms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new WinForms.ContextMenuStrip();

        menu.Items.Add(CreateMenuItem("显示/隐藏窗口", ToggleWindowVisibility));
        menu.Items.Add(CreateMenuItem("开始倒计时", StartTimer));
        menu.Items.Add(CreateMenuItem("暂停倒计时", PauseTimer));
        menu.Items.Add(CreateMenuItem("重置倒计时", ResetTimer));
        menu.Items.Add(new WinForms.ToolStripSeparator());

        var timerPlanMenu = new WinForms.ToolStripMenuItem("时间与循环");
        timerPlanMenu.DropDownItems.Add(CreateMenuItem("设置工作时间（时:分:秒）", ConfigureWorkDuration));
        timerPlanMenu.DropDownItems.Add(CreateMenuItem("设置休息时间（时:分:秒）", ConfigureRestDuration));
        timerPlanMenu.DropDownItems.Add(CreateMenuItem("设置循环次数", ConfigureLoopCount));
        _workSessionEnabledItem = CreateCheckMenuItem("启用工作时间", _state.Timer.EnableWorkSession, ToggleWorkSession);
        _restSessionEnabledItem = CreateCheckMenuItem("启用休息时间", _state.Timer.EnableRestSession, ToggleRestSession);
        timerPlanMenu.DropDownItems.Add(_workSessionEnabledItem);
        timerPlanMenu.DropDownItems.Add(_restSessionEnabledItem);
        timerPlanMenu.DropDownItems.Add(new WinForms.ToolStripSeparator());
        foreach (int minutes in QuickPresetMinutes)
        {
            timerPlanMenu.DropDownItems.Add(CreateMenuItem($"工作预设 {minutes} 分钟", () => ApplyWorkPreset(minutes, startImmediately: false)));
        }
        menu.Items.Add(timerPlanMenu);

        var timerAudioMenu = new WinForms.ToolStripMenuItem("音频设置");
        _loopAudioEnabledItem = CreateCheckMenuItem("启用过程音频", _state.Timer.UseLoopAudio, ToggleLoopAudioEnabled);
        _workEndAudioEnabledItem = CreateCheckMenuItem("启用工作结束音频", _state.Timer.EnableWorkEndAudio, ToggleWorkEndAudioEnabled);
        _restEndAudioEnabledItem = CreateCheckMenuItem("启用休息结束音频", _state.Timer.EnableRestEndAudio, ToggleRestEndAudioEnabled);
        _loopAudioItem = CreateStateMenuItem("选择过程音频", SelectLoopAudio);
        _endAudioItem = CreateStateMenuItem("选择工作结束音频", SelectWorkEndAudio);
        _restEndAudioItem = CreateStateMenuItem("选择休息结束音频", SelectRestEndAudio);
        timerAudioMenu.DropDownItems.Add(_loopAudioEnabledItem);
        timerAudioMenu.DropDownItems.Add(_workEndAudioEnabledItem);
        timerAudioMenu.DropDownItems.Add(_restEndAudioEnabledItem);
        timerAudioMenu.DropDownItems.Add(new WinForms.ToolStripSeparator());
        timerAudioMenu.DropDownItems.Add(_loopAudioItem);
        timerAudioMenu.DropDownItems.Add(_endAudioItem);
        timerAudioMenu.DropDownItems.Add(_restEndAudioItem);
        timerAudioMenu.DropDownItems.Add(new WinForms.ToolStripSeparator());
        timerAudioMenu.DropDownItems.Add(CreateMenuItem("取消过程音频", CancelLoopAudio));
        timerAudioMenu.DropDownItems.Add(CreateMenuItem("取消工作结束音频", CancelWorkEndAudio));
        timerAudioMenu.DropDownItems.Add(CreateMenuItem("取消休息结束音频", CancelRestEndAudio));
        menu.Items.Add(timerAudioMenu);

        var notesMenu = new WinForms.ToolStripMenuItem("便签设置");
        notesMenu.DropDownItems.Add(CreateMenuItem("编辑便签段落", EditNotes));
        _rotationEnabledItem = CreateCheckMenuItem("启用便签定时切换", _state.Notes.EnableRotation, ToggleNoteRotation);
        notesMenu.DropDownItems.Add(_rotationEnabledItem);
        notesMenu.DropDownItems.Add(CreateMenuItem("设置便签切换秒数", ConfigureRotationSeconds));
        notesMenu.DropDownItems.Add(CreateMenuItem("设置便签字体", ConfigureNoteFont));
        notesMenu.DropDownItems.Add(CreateMenuItem("设置便签字号", ConfigureNoteFontSize));
        notesMenu.DropDownItems.Add(CreateMenuItem("设置便签文字颜色", ConfigureNotesTextColor));
        menu.Items.Add(notesMenu);

        var appearanceMenu = new WinForms.ToolStripMenuItem("外观设置");
        _simpleModeItem = CreateCheckMenuItem("简洁模式（仅倒计时）", _state.App.SimpleMode, ToggleSimpleMode);
        appearanceMenu.DropDownItems.Add(_simpleModeItem);
        appearanceMenu.DropDownItems.Add(CreateMenuItem("设置倒计时字体", ConfigureCountdownFont));
        appearanceMenu.DropDownItems.Add(CreateMenuItem("设置倒计时字号", ConfigureCountdownFontSize));
        appearanceMenu.DropDownItems.Add(CreateMenuItem("设置倒计时颜色", ConfigureCountdownTextColor));
        appearanceMenu.DropDownItems.Add(CreateMenuItem("设置背景颜色", ConfigureBackgroundColor));
        menu.Items.Add(appearanceMenu);
        menu.Items.Add(BuildBackgroundEffectMenu());
        menu.Items.Add(BuildTransparencyMenu());

        var webMenu = new WinForms.ToolStripMenuItem("网页任务");
        webMenu.DropDownItems.Add(CreateMenuItem("设置定时打开网页URL", ConfigureWebUrl));
        _webOpenEnabledItem = CreateCheckMenuItem("工作倒计时结束打开网页", _state.WebTask.EnableOnTimerComplete, ToggleWebOpenOnComplete);
        webMenu.DropDownItems.Add(_webOpenEnabledItem);
        menu.Items.Add(webMenu);

        var systemMenu = new WinForms.ToolStripMenuItem("系统设置");
        _topMostItem = CreateCheckMenuItem("窗口置顶", _state.App.TopMost, ToggleTopMost);
        _fixedModeItem = CreateCheckMenuItem("固定模式（不可拖拽）", _state.App.FixedMode, ToggleFixedMode);
        systemMenu.DropDownItems.Add(_topMostItem);
        systemMenu.DropDownItems.Add(_fixedModeItem);
        _autoStartItem = CreateCheckMenuItem("开机自启动", _state.App.AutoStart, ToggleAutoStart);
        _minimizeToTrayItem = CreateCheckMenuItem("关闭时最小化到托盘", _state.App.MinimizeToTrayOnClose, ToggleMinimizeToTrayOnClose);
        systemMenu.DropDownItems.Add(_autoStartItem);
        systemMenu.DropDownItems.Add(_minimizeToTrayItem);
        menu.Items.Add(systemMenu);
        menu.Items.Add(BuildHotkeyMenu());

        menu.Items.Add(CreateMenuItem("github仓库", OpenGithubRepository));
        menu.Items.Add(CreateMenuItem("打开配置目录", OpenConfigDirectory));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(CreateMenuItem("退出程序", ExitApplication));

        UpdateAudioMenuChecks();
        UpdateTimerModeChecks();
        UpdateBackgroundEffectChecks();
        return menu;
    }

    private WinForms.ToolStripMenuItem BuildQuickPresetMenu(string title, bool startImmediately)
    {
        var menu = new WinForms.ToolStripMenuItem(title);
        foreach (int minutes in QuickPresetMinutes)
        {
            menu.DropDownItems.Add(CreateMenuItem($"{minutes} 分钟", () => ApplyWorkPreset(minutes, startImmediately)));
        }
        return menu;
    }

    private void ApplyWorkPreset(int minutes, bool startImmediately)
    {
        int seconds = Math.Max(1, minutes * 60);
        _state.Timer.WorkDurationSeconds = seconds;
        _state.Timer.DurationSeconds = seconds;
        _state.Timer.DurationMinutes = Math.Max(1, seconds / 60);
        _state.Timer.EnableWorkSession = true;
        UpdateTimerModeChecks();

        ResetTimer();
        SaveState();

        if (startImmediately)
        {
            StartTimer();
        }
    }

    private WinForms.ToolStripMenuItem BuildTransparencyMenu()
    {
        var menu = new WinForms.ToolStripMenuItem("透明度（0~100，越高越透明）");

        foreach (int value in Enumerable.Range(0, 21).Select(i => i * 5))
        {
            var item = new WinForms.ToolStripMenuItem($"{value}%")
            {
                CheckOnClick = true,
                Checked = Math.Abs(_state.App.Transparency - value) < 0.5,
                Tag = value
            };

            item.Click += (_, _) =>
            {
                if (item.Tag is not int selected)
                {
                    return;
                }

                foreach (WinForms.ToolStripItem child in menu.DropDownItems)
                {
                    if (child is WinForms.ToolStripMenuItem menuItem)
                    {
                        menuItem.Checked = ReferenceEquals(menuItem, item);
                    }
                }

                _state.App.Transparency = selected;
                ApplyVisualEffects();
                SaveState();
            };

            menu.DropDownItems.Add(item);
        }

        return menu;
    }

    private WinForms.ToolStripMenuItem BuildBackgroundEffectMenu()
    {
        var menu = new WinForms.ToolStripMenuItem("背景效果");
        AddBackgroundEffectItem(menu, "毛玻璃（增强质感）", "Blur");
        AddBackgroundEffectItem(menu, "磨砂（增强质感）", "Frosted");
        AddBackgroundEffectItem(menu, "苹果风（边缘毛玻璃）", "Apple");
        return menu;
    }

    private void AddBackgroundEffectItem(WinForms.ToolStripMenuItem parent, string text, string mode)
    {
        var item = new WinForms.ToolStripMenuItem(text)
        {
            CheckOnClick = true,
            Tag = mode
        };
        item.Click += (_, _) =>
        {
            _state.App.BackgroundEffect = mode;
            UpdateBackgroundEffectChecks();
            ApplyVisualEffects();
            SaveState();
        };
        _backgroundModeItems[mode] = item;
        parent.DropDownItems.Add(item);
    }

    private void UpdateBackgroundEffectChecks()
    {
        foreach ((string key, WinForms.ToolStripMenuItem item) in _backgroundModeItems)
        {
            item.Checked = string.Equals(_state.App.BackgroundEffect, key, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void UpdateAudioMenuChecks()
    {
        if (_loopAudioEnabledItem is not null && _loopAudioEnabledItem.Checked != _state.Timer.UseLoopAudio)
        {
            SetMenuChecked(_loopAudioEnabledItem, _state.Timer.UseLoopAudio);
        }

        if (_workEndAudioEnabledItem is not null && _workEndAudioEnabledItem.Checked != _state.Timer.EnableWorkEndAudio)
        {
            SetMenuChecked(_workEndAudioEnabledItem, _state.Timer.EnableWorkEndAudio);
        }

        if (_restEndAudioEnabledItem is not null && _restEndAudioEnabledItem.Checked != _state.Timer.EnableRestEndAudio)
        {
            SetMenuChecked(_restEndAudioEnabledItem, _state.Timer.EnableRestEndAudio);
        }

        if (_loopAudioItem is not null)
        {
            _loopAudioItem.Checked = !string.IsNullOrWhiteSpace(_state.Timer.LoopAudioPath) &&
                                     File.Exists(_state.Timer.LoopAudioPath);
        }

        if (_endAudioItem is not null)
        {
            _endAudioItem.Checked = _state.Timer.UseCustomEndAudio &&
                                     !string.IsNullOrWhiteSpace(_state.Timer.EndAudioPath) &&
                                     File.Exists(_state.Timer.EndAudioPath);
        }

        if (_restEndAudioItem is not null)
        {
            _restEndAudioItem.Checked = _state.Timer.UseCustomRestEndAudio &&
                                        !string.IsNullOrWhiteSpace(_state.Timer.RestEndAudioPath) &&
                                        File.Exists(_state.Timer.RestEndAudioPath);
        }
    }

    private void UpdateTimerModeChecks()
    {
        if (_workSessionEnabledItem is not null && _workSessionEnabledItem.Checked != _state.Timer.EnableWorkSession)
        {
            SetMenuChecked(_workSessionEnabledItem, _state.Timer.EnableWorkSession);
        }

        if (_restSessionEnabledItem is not null && _restSessionEnabledItem.Checked != _state.Timer.EnableRestSession)
        {
            SetMenuChecked(_restSessionEnabledItem, _state.Timer.EnableRestSession);
        }
    }

    private WinForms.ToolStripMenuItem BuildFontSizeMenu()
    {
        var menu = new WinForms.ToolStripMenuItem("便签字号");
        var sizes = new[] { 12.0, 16.0, 20.0, 24.0, 28.0, 32.0 };

        foreach (double size in sizes)
        {
            var item = new WinForms.ToolStripMenuItem(size.ToString("0"))
            {
                CheckOnClick = true,
                Checked = Math.Abs(_state.Notes.FontSize - size) < 0.01
            };

            item.Click += (_, _) =>
            {
                foreach (WinForms.ToolStripItem child in menu.DropDownItems)
                {
                    if (child is WinForms.ToolStripMenuItem menuItem)
                    {
                        menuItem.Checked = ReferenceEquals(menuItem, item);
                    }
                }

                _state.Notes.FontSize = size;
                UpdateNotesText();
                SaveState();
            };

            menu.DropDownItems.Add(item);
        }

        return menu;
    }

    private WinForms.ToolStripMenuItem BuildHotkeyMenu()
    {
        return CreateMenuItem("快捷键设置", OpenHotkeySettingsDialog);
    }

    private void OpenHotkeySettingsDialog()
    {
        var dialog = new HotkeySettingsDialog(CloneHotkeySettings(_state.App.Hotkeys));
        if (IsVisible)
        {
            dialog.Owner = this;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        bool? result = dialog.ShowDialog();
        if (result != true)
        {
            return;
        }

        HotkeySettings previous = CloneHotkeySettings(_state.App.Hotkeys);
        _state.App.Hotkeys = CloneHotkeySettings(dialog.Hotkeys);
        _state.App.Hotkeys.Normalize();

        if (!TryApplyConfiguredHotkeys(showDialogOnFailure: true))
        {
            _state.App.Hotkeys = previous;
            _ = TryApplyConfiguredHotkeys(showDialogOnFailure: false);
            return;
        }

        SaveState();
        _configService.AppendLog("快捷键设置已更新");
    }

    private static HotkeySettings CloneHotkeySettings(HotkeySettings? source)
    {
        source ??= new HotkeySettings();
        return new HotkeySettings
        {
            StartTimer = source.StartTimer,
            PauseTimer = source.PauseTimer,
            ResetTimer = source.ResetTimer,
            ToggleSimpleMode = source.ToggleSimpleMode,
            ToggleTopMost = source.ToggleTopMost,
            ToggleFixedMode = source.ToggleFixedMode
        };
    }

    private bool TryApplyConfiguredHotkeys(bool showDialogOnFailure)
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return true;
        }

        UnregisterAllHotkeys();

        bool changed = false;
        var errors = new List<string>();
        var usedCombinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (HotkeyAction action in SupportedHotkeyActions)
        {
            string raw = GetConfiguredHotkey(action);
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (!TryParseHotkey(raw, out uint modifiers, out uint virtualKey, out string normalized, out string parseError))
            {
                errors.Add($"{GetHotkeyActionTitle(action)}：{parseError}");
                continue;
            }

            if (!string.Equals(raw, normalized, StringComparison.Ordinal))
            {
                SetConfiguredHotkey(action, normalized);
                changed = true;
            }

            if (!usedCombinations.Add(normalized))
            {
                errors.Add($"{GetHotkeyActionTitle(action)}：与其他动作重复 ({normalized})");
                continue;
            }

            int hotkeyId = HotkeyIdBase + (int)action;
            if (!RegisterHotKey(_windowHandle, hotkeyId, modifiers, virtualKey))
            {
                int errorCode = Marshal.GetLastWin32Error();
                errors.Add($"{GetHotkeyActionTitle(action)}：注册失败（错误码 {errorCode}）");
                continue;
            }

            _registeredHotkeys[hotkeyId] = action;
        }

        if (errors.Count > 0)
        {
            UnregisterAllHotkeys();
            string message = string.Join(Environment.NewLine, errors);
            _configService.AppendLog($"快捷键注册失败: {message.Replace(Environment.NewLine, " | ")}");
            if (showDialogOnFailure)
            {
                WpfMessageBox.Show(this, $"快捷键设置未生效：{Environment.NewLine}{message}", "快捷键注册失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return false;
        }

        if (changed)
        {
            _configService.Save(_state);
        }
        return true;
    }

    private void UnregisterAllHotkeys()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            _registeredHotkeys.Clear();
            return;
        }

        foreach (int id in _registeredHotkeys.Keys.ToList())
        {
            _ = UnregisterHotKey(_windowHandle, id);
        }

        _registeredHotkeys.Clear();
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        _ = hwnd;
        _ = lParam;
        if (msg != WmHotKey)
        {
            return IntPtr.Zero;
        }

        int id = wParam.ToInt32();
        if (_registeredHotkeys.TryGetValue(id, out HotkeyAction action))
        {
            ExecuteHotkeyAction(action);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ExecuteHotkeyAction(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.StartTimer:
                StartTimer();
                break;
            case HotkeyAction.PauseTimer:
                PauseTimer();
                break;
            case HotkeyAction.ResetTimer:
                ResetTimer();
                break;
            case HotkeyAction.ToggleSimpleMode:
            {
                bool enabled = !_state.App.SimpleMode;
                ToggleSimpleMode(enabled);
                if (_simpleModeItem is not null)
                {
                    SetMenuChecked(_simpleModeItem, enabled);
                }
                break;
            }
            case HotkeyAction.ToggleTopMost:
            {
                bool enabled = !_state.App.TopMost;
                ToggleTopMost(enabled);
                if (_topMostItem is not null)
                {
                    SetMenuChecked(_topMostItem, enabled);
                }
                break;
            }
            case HotkeyAction.ToggleFixedMode:
            {
                bool enabled = !_state.App.FixedMode;
                ToggleFixedMode(enabled);
                if (_fixedModeItem is not null)
                {
                    SetMenuChecked(_fixedModeItem, enabled);
                }
                break;
            }
        }

        _configService.AppendLog($"快捷键触发: {GetHotkeyActionTitle(action)}");
    }

    private static bool TryParseHotkey(string raw, out uint modifiers, out uint virtualKey, out string normalized, out string error)
    {
        modifiers = 0;
        virtualKey = 0;
        normalized = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "快捷键不能为空。";
            return false;
        }

        string[] tokens = raw.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            error = "快捷键至少需要一个修饰键和一个主键，例如 Ctrl+Alt+S。";
            return false;
        }

        string? keyToken = null;
        foreach (string token in tokens)
        {
            if (TryParseModifierToken(token, out uint modifier))
            {
                if ((modifiers & modifier) != 0)
                {
                    error = $"修饰键重复：{token}";
                    return false;
                }

                modifiers |= modifier;
                continue;
            }

            if (keyToken is not null)
            {
                error = "只能设置一个主键。";
                return false;
            }

            keyToken = token;
        }

        if (modifiers == 0)
        {
            error = "必须包含 Ctrl/Alt/Shift/Win 中至少一个修饰键。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(keyToken))
        {
            error = "缺少主键。";
            return false;
        }

        if (!TryParseMainKey(keyToken, out Key key, out string keyName))
        {
            error = $"不支持的主键：{keyToken}";
            return false;
        }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
        {
            error = "无法识别主键。";
            return false;
        }

        virtualKey = (uint)vk;
        normalized = BuildNormalizedHotkey(modifiers, keyName);
        return true;
    }

    private static bool TryParseModifierToken(string token, out uint modifier)
    {
        modifier = 0;
        string normalized = token.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "ctrl":
            case "control":
            case "ctl":
            case "控制":
            case "控制键":
                modifier = ModControl;
                return true;
            case "alt":
            case "选项":
            case "选项键":
                modifier = ModAlt;
                return true;
            case "shift":
            case "上档":
            case "上档键":
                modifier = ModShift;
                return true;
            case "win":
            case "windows":
            case "meta":
            case "徽标":
            case "徽标键":
                modifier = ModWin;
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseMainKey(string token, out Key key, out string display)
    {
        key = Key.None;
        display = string.Empty;

        string normalized = token.Trim();
        if (normalized.Length == 1)
        {
            char c = char.ToUpperInvariant(normalized[0]);
            if (c is >= 'A' and <= 'Z')
            {
                key = (Key)Enum.Parse(typeof(Key), c.ToString());
                display = c.ToString();
                return true;
            }

            if (c is >= '0' and <= '9')
            {
                key = c switch
                {
                    '0' => Key.D0,
                    '1' => Key.D1,
                    '2' => Key.D2,
                    '3' => Key.D3,
                    '4' => Key.D4,
                    '5' => Key.D5,
                    '6' => Key.D6,
                    '7' => Key.D7,
                    '8' => Key.D8,
                    _ => Key.D9
                };
                display = c.ToString();
                return true;
            }
        }

        string upper = normalized.ToUpperInvariant();
        switch (upper)
        {
            case "ESC":
            case "ESCAPE":
                key = Key.Escape;
                display = "Esc";
                return true;
            case "SPACE":
            case "SPACEBAR":
            case "空格":
                key = Key.Space;
                display = "Space";
                return true;
            case "ENTER":
            case "RETURN":
            case "回车":
                key = Key.Enter;
                display = "Enter";
                return true;
            case "TAB":
                key = Key.Tab;
                display = "Tab";
                return true;
            case "UP":
            case "上":
                key = Key.Up;
                display = "Up";
                return true;
            case "DOWN":
            case "下":
                key = Key.Down;
                display = "Down";
                return true;
            case "LEFT":
            case "左":
                key = Key.Left;
                display = "Left";
                return true;
            case "RIGHT":
            case "右":
                key = Key.Right;
                display = "Right";
                return true;
            case "HOME":
                key = Key.Home;
                display = "Home";
                return true;
            case "END":
                key = Key.End;
                display = "End";
                return true;
            case "PAGEUP":
            case "PGUP":
                key = Key.PageUp;
                display = "PageUp";
                return true;
            case "PAGEDOWN":
            case "PGDN":
                key = Key.PageDown;
                display = "PageDown";
                return true;
            case "INSERT":
            case "INS":
                key = Key.Insert;
                display = "Insert";
                return true;
            case "DELETE":
            case "DEL":
                key = Key.Delete;
                display = "Delete";
                return true;
        }

        if (upper.StartsWith("F", StringComparison.Ordinal) &&
            int.TryParse(upper[1..], out int fIndex) &&
            fIndex is >= 1 and <= 24)
        {
            key = (Key)((int)Key.F1 + (fIndex - 1));
            display = $"F{fIndex}";
            return true;
        }

        if (Enum.TryParse(normalized, ignoreCase: true, out Key parsed) &&
            parsed != Key.None &&
            parsed is not Key.LeftCtrl and not Key.RightCtrl and not Key.LeftAlt and not Key.RightAlt and not Key.LeftShift and not Key.RightShift and not Key.LWin and not Key.RWin)
        {
            key = parsed;
            display = parsed.ToString();
            return true;
        }

        return false;
    }

    private static string BuildNormalizedHotkey(uint modifiers, string keyName)
    {
        var parts = new List<string>();
        if ((modifiers & ModControl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & ModAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & ModShift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & ModWin) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(keyName);
        return string.Join("+", parts);
    }

    private bool IsHotkeyConflict(HotkeyAction action, string candidate, out string conflictAction)
    {
        foreach (HotkeyAction other in SupportedHotkeyActions)
        {
            if (other == action)
            {
                continue;
            }

            string value = GetConfiguredHotkey(other);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (TryParseHotkey(value, out _, out _, out string normalized, out _) &&
                string.Equals(normalized, candidate, StringComparison.OrdinalIgnoreCase))
            {
                conflictAction = GetHotkeyActionTitle(other);
                return true;
            }
        }

        conflictAction = string.Empty;
        return false;
    }

    private string GetConfiguredHotkey(HotkeyAction action)
    {
        return action switch
        {
            HotkeyAction.StartTimer => _state.App.Hotkeys.StartTimer,
            HotkeyAction.PauseTimer => _state.App.Hotkeys.PauseTimer,
            HotkeyAction.ResetTimer => _state.App.Hotkeys.ResetTimer,
            HotkeyAction.ToggleSimpleMode => _state.App.Hotkeys.ToggleSimpleMode,
            HotkeyAction.ToggleTopMost => _state.App.Hotkeys.ToggleTopMost,
            HotkeyAction.ToggleFixedMode => _state.App.Hotkeys.ToggleFixedMode,
            _ => string.Empty
        };
    }

    private void SetConfiguredHotkey(HotkeyAction action, string value)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        switch (action)
        {
            case HotkeyAction.StartTimer:
                _state.App.Hotkeys.StartTimer = normalized;
                break;
            case HotkeyAction.PauseTimer:
                _state.App.Hotkeys.PauseTimer = normalized;
                break;
            case HotkeyAction.ResetTimer:
                _state.App.Hotkeys.ResetTimer = normalized;
                break;
            case HotkeyAction.ToggleSimpleMode:
                _state.App.Hotkeys.ToggleSimpleMode = normalized;
                break;
            case HotkeyAction.ToggleTopMost:
                _state.App.Hotkeys.ToggleTopMost = normalized;
                break;
            case HotkeyAction.ToggleFixedMode:
                _state.App.Hotkeys.ToggleFixedMode = normalized;
                break;
        }
    }

    private static string GetHotkeyActionTitle(HotkeyAction action)
    {
        return action switch
        {
            HotkeyAction.StartTimer => "开启倒计时",
            HotkeyAction.PauseTimer => "暂停倒计时",
            HotkeyAction.ResetTimer => "重置倒计时",
            HotkeyAction.ToggleSimpleMode => "进入/退出简洁模式",
            HotkeyAction.ToggleTopMost => "窗口置顶开关",
            HotkeyAction.ToggleFixedMode => "窗口固定开关",
            _ => action.ToString()
        };
    }

    private WinForms.ToolStripMenuItem CreateMenuItem(string text, Action action)
    {
        var item = new WinForms.ToolStripMenuItem(text);
        item.Click += (_, _) => DispatchToUi(action);
        return item;
    }

    private WinForms.ToolStripMenuItem CreateStateMenuItem(string text, Action action)
    {
        var item = new WinForms.ToolStripMenuItem(text)
        {
            CheckOnClick = false
        };
        item.Click += (_, _) => DispatchToUi(action);
        return item;
    }

    private WinForms.ToolStripMenuItem CreateCheckMenuItem(string text, bool initialValue, Action<bool> onChanged)
    {
        var item = new WinForms.ToolStripMenuItem(text)
        {
            CheckOnClick = true,
            Checked = initialValue
        };
        item.CheckedChanged += (_, _) =>
        {
            if (_suppressCheckCallbacks)
            {
                return;
            }

            DispatchToUi(() => onChanged(item.Checked));
        };
        return item;
    }

    private void ToggleWindowVisibility()
    {
        if (IsVisible)
        {
            Hide();
            SuspendWindowForTray();
            return;
        }

        Show();
        ResumeWindowFromTray();
        ApplyVisualEffects();
        ApplyResponsiveLayout();
        Activate();
    }

    private void StartTimer()
    {
        if (_isRunning)
        {
            return;
        }

        if (!_sessionChainActive)
        {
            BeginSessionChain();
        }
        else if (_remainingSeconds <= 0)
        {
            _remainingSeconds = GetDurationForPhase(_currentPhase);
        }

        ReleaseEndAudioSource();
        _isRunning = true;
        _countdownTimer.Start();
        ResumeOrStartLoopAudio();
        UpdateCountdownText();
        PersistRuntimeState();
        _configService.AppendLog($"倒计时开始: {GetPhaseText(_currentPhase)} 第{_currentCycle}/{_totalCycles}轮");
    }

    private void BeginSessionChain()
    {
        _phaseSequence = BuildPhaseSequence();
        _totalCycles = Math.Clamp(_state.Timer.LoopCount, 1, 99);
        _currentCycle = 1;
        _phaseIndexInCycle = 0;
        _currentPhase = _phaseSequence[0];
        _remainingSeconds = GetDurationForPhase(_currentPhase);
        _sessionChainActive = true;
    }

    private void PauseTimer()
    {
        if (!_isRunning)
        {
            return;
        }

        _isRunning = false;
        _countdownTimer.Stop();
        _loopPlayer?.Pause();

        PersistRuntimeState();
        _configService.AppendLog("倒计时暂停");
    }

    private void ResetTimer()
    {
        _isRunning = false;
        _countdownTimer.Stop();
        _sessionChainActive = false;
        _phaseSequence = BuildPhaseSequence();
        _totalCycles = Math.Clamp(_state.Timer.LoopCount, 1, 99);
        _currentCycle = 1;
        _phaseIndexInCycle = 0;
        _currentPhase = _phaseSequence[0];
        _remainingSeconds = GetDurationForPhase(_currentPhase);

        StopAllAudio();
        UpdateCountdownText();
        PersistRuntimeState();
        _configService.AppendLog("倒计时重置");
    }

    private void CountdownTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isRunning)
        {
            return;
        }

        _remainingSeconds = Math.Max(0, _remainingSeconds - 1);
        UpdateCountdownText();
        PersistRuntimeState();

        if (_remainingSeconds == 0)
        {
            CompleteCountdown();
        }
    }

    private void ResumeOrStartLoopAudio()
    {
        if (_currentPhase != TimerPhase.Work)
        {
            ReleaseLoopAudioSource();
            return;
        }

        if (!_state.Timer.UseLoopAudio)
        {
            return;
        }

        string loopPath = _state.Timer.LoopAudioPath;
        if (string.IsNullOrWhiteSpace(loopPath) || !File.Exists(loopPath))
        {
            return;
        }

        if (!_loopSourceLoaded || !string.Equals(_activeLoopPath, loopPath, StringComparison.OrdinalIgnoreCase))
        {
            MediaPlayer player = EnsureLoopPlayer();
            player.Open(new Uri(loopPath));
            _activeLoopPath = loopPath;
            _loopSourceLoaded = true;
        }

        EnsureLoopPlayer().Play();
    }

    private void StopAllAudio()
    {
        ReleaseLoopAudioSource();
        ReleaseEndAudioSource();
    }

    private void CompleteCountdown()
    {
        TimerPhase completedPhase = _currentPhase;

        _countdownTimer.Stop();
        _isRunning = false;
        ReleaseLoopAudioSource();

        _remainingSeconds = 0;
        UpdateCountdownText();
        PersistRuntimeState();

        if (completedPhase == TimerPhase.Work)
        {
            OpenWebPageIfNeeded();
        }

        PlayEndAudio(completedPhase);
        _configService.AppendLog($"{GetPhaseText(completedPhase)}结束: 第{_currentCycle}/{_totalCycles}轮");

        if (TryMoveToNextPhase())
        {
            StartPhaseAutomatically();
        }
        else
        {
            _sessionChainActive = false;
            PersistRuntimeState();
            _configService.AppendLog($"倒计时循环完成: 共{_totalCycles}轮");
        }

        SaveState();
    }

    private bool TryMoveToNextPhase()
    {
        if (!_sessionChainActive)
        {
            return false;
        }

        if (_phaseIndexInCycle + 1 < _phaseSequence.Count)
        {
            _phaseIndexInCycle++;
            _currentPhase = _phaseSequence[_phaseIndexInCycle];
            _remainingSeconds = GetDurationForPhase(_currentPhase);
            return true;
        }

        if (_currentCycle < _totalCycles)
        {
            _currentCycle++;
            _phaseIndexInCycle = 0;
            _currentPhase = _phaseSequence[0];
            _remainingSeconds = GetDurationForPhase(_currentPhase);
            return true;
        }

        return false;
    }

    private void StartPhaseAutomatically()
    {
        if (_remainingSeconds <= 0)
        {
            return;
        }

        _isRunning = true;
        _countdownTimer.Start();
        ResumeOrStartLoopAudio();
        UpdateCountdownText();
        PersistRuntimeState();
        _configService.AppendLog($"进入{GetPhaseText(_currentPhase)}: 第{_currentCycle}/{_totalCycles}轮");
    }

    private void PlayEndAudio(TimerPhase phase)
    {
        bool enabled = phase == TimerPhase.Work ? _state.Timer.EnableWorkEndAudio : _state.Timer.EnableRestEndAudio;
        if (!enabled)
        {
            _isEndAudioPlaying = false;
            return;
        }

        string endPath = phase == TimerPhase.Work ? _state.Timer.EndAudioPath : _state.Timer.RestEndAudioPath;
        if (!string.IsNullOrWhiteSpace(endPath) && File.Exists(endPath))
        {
            ReleaseEndAudioSource();
            MediaPlayer player = EnsureEndPlayer();
            player.Open(new Uri(endPath));
            player.Play();
            _isEndAudioPlaying = true;
            return;
        }

        _isEndAudioPlaying = false;
        SystemSounds.Beep.Play();
    }

    private void OpenWebPageIfNeeded()
    {
        if (!_state.WebTask.EnableOnTimerComplete)
        {
            return;
        }

        if (!Uri.TryCreate(_state.WebTask.Url, UriKind.Absolute, out Uri? uri))
        {
            _configService.AppendLog("网页任务未执行：URL无效");
            return;
        }

        var info = new ProcessStartInfo(uri.ToString())
        {
            UseShellExecute = true
        };

        try
        {
            _ = Process.Start(info);
            _configService.AppendLog($"已打开网页: {uri}");
        }
        catch (Win32Exception ex)
        {
            _configService.AppendLog($"打开网页失败: {ex.Message}");
        }
    }

    private void ConfigureWorkDuration()
    {
        string defaultDuration = FormatDuration(_state.Timer.WorkDurationSeconds);
        if (!TryPrompt("工作倒计时", "请输入时:分:秒（例如 01:25:30）", defaultDuration, out string value))
        {
            return;
        }

        if (!TryParseDuration(value, out int seconds) || seconds < 1)
        {
            WpfMessageBox.Show(this, "请输入正确格式：时:分:秒（例如 00:25:00）。", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _state.Timer.WorkDurationSeconds = seconds;
        _state.Timer.DurationSeconds = seconds;
        _state.Timer.DurationMinutes = Math.Max(1, seconds / 60);
        ResetTimer();
        SaveState();
    }

    private void ConfigureRestDuration()
    {
        string defaultDuration = FormatDuration(_state.Timer.RestDurationSeconds);
        if (!TryPrompt("休息倒计时", "请输入时:分:秒（例如 00:05:00）", defaultDuration, out string value))
        {
            return;
        }

        if (!TryParseDuration(value, out int seconds) || seconds < 1)
        {
            WpfMessageBox.Show(this, "请输入正确格式：时:分:秒（例如 00:05:00）。", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _state.Timer.RestDurationSeconds = seconds;
        ResetTimer();
        SaveState();
    }

    private void ConfigureLoopCount()
    {
        if (!TryPrompt("循环次数", "请输入循环次数（1~99）", _state.Timer.LoopCount.ToString(), out string value))
        {
            return;
        }

        if (!int.TryParse(value, out int count) || count < 1 || count > 99)
        {
            WpfMessageBox.Show(this, "请输入 1~99 的整数。", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _state.Timer.LoopCount = count;
        ResetTimer();
        SaveState();
    }

    private void SelectLoopAudio()
    {
        string selected = PickAudioFile();
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        _state.Timer.LoopAudioPath = selected;
        _state.Timer.UseLoopAudio = true;
        _loopSourceLoaded = false;
        UpdateAudioMenuChecks();
        SaveState();
    }

    private void SelectWorkEndAudio()
    {
        string selected = PickAudioFile();
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        _state.Timer.EndAudioPath = selected;
        _state.Timer.UseCustomEndAudio = true;
        _state.Timer.EnableWorkEndAudio = true;
        UpdateAudioMenuChecks();
        SaveState();
    }

    private void SelectRestEndAudio()
    {
        string selected = PickAudioFile();
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        _state.Timer.RestEndAudioPath = selected;
        _state.Timer.UseCustomRestEndAudio = true;
        _state.Timer.EnableRestEndAudio = true;
        UpdateAudioMenuChecks();
        SaveState();
    }

    private void ToggleLoopAudioEnabled(bool enabled)
    {
        _state.Timer.UseLoopAudio = enabled;
        if (!enabled)
        {
            ReleaseLoopAudioSource();
        }

        UpdateAudioMenuChecks();
        SaveState();
    }

    private void ToggleWorkEndAudioEnabled(bool enabled)
    {
        _state.Timer.EnableWorkEndAudio = enabled;
        if (!enabled)
        {
            ReleaseEndAudioSource();
        }

        UpdateAudioMenuChecks();
        SaveState();
    }

    private void ToggleRestEndAudioEnabled(bool enabled)
    {
        _state.Timer.EnableRestEndAudio = enabled;
        UpdateAudioMenuChecks();
        SaveState();
    }

    private void CancelLoopAudio()
    {
        _state.Timer.LoopAudioPath = string.Empty;
        _state.Timer.UseLoopAudio = false;
        ReleaseLoopAudioSource();
        UpdateAudioMenuChecks();
        SaveState();
    }

    private void CancelWorkEndAudio()
    {
        _state.Timer.EnableWorkEndAudio = false;
        _state.Timer.UseCustomEndAudio = false;
        _state.Timer.EndAudioPath = string.Empty;
        ReleaseEndAudioSource();
        UpdateAudioMenuChecks();
        SaveState();
    }

    private void CancelRestEndAudio()
    {
        _state.Timer.EnableRestEndAudio = false;
        _state.Timer.UseCustomRestEndAudio = false;
        _state.Timer.RestEndAudioPath = string.Empty;
        UpdateAudioMenuChecks();
        SaveState();
    }

    private void EditNotes()
    {
        var dialog = new NotesEditorDialog(_state.NotesContent.Segments)
        {
            Owner = this
        };

        bool? result = dialog.ShowDialog();
        if (result != true)
        {
            return;
        }

        _state.NotesContent.Segments = dialog.Segments;
        _state.NotesContent.CurrentIndex = 0;
        UpdateNotesText();
        ConfigureNoteRotationTimer();
        SaveState();
    }

    private void ConfigureNoteFont()
    {
        using var dialog = new WinForms.FontDialog
        {
            ShowColor = false,
            ShowEffects = false
        };

        dialog.Font = new Drawing.Font(_state.Notes.FontFamily, (float)_state.Notes.FontSize);

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        _state.Notes.FontFamily = dialog.Font.FontFamily.Name;
        _state.Notes.FontSize = dialog.Font.Size;

        UpdateNotesText();
        SaveState();
    }

    private void ConfigureCountdownFont()
    {
        using var dialog = new WinForms.FontDialog
        {
            ShowColor = false,
            ShowEffects = false
        };

        dialog.Font = new Drawing.Font(_state.App.CountdownFontFamily, (float)_state.App.CountdownFontSize, Drawing.FontStyle.Bold);
        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        _state.App.CountdownFontFamily = dialog.Font.FontFamily.Name;
        _state.App.CountdownFontSize = Math.Clamp(dialog.Font.Size, 10, 60);
        ApplyTextAppearance();
        ApplyResponsiveLayout();
        SaveState();
    }

    private void ConfigureNoteFontSize()
    {
        if (!TryPrompt("便签字号", "请输入便签字号（10~132）", _state.Notes.FontSize.ToString("0"), out string value))
        {
            return;
        }

        if (!double.TryParse(value, out double fontSize) || fontSize < 10 || fontSize > 132)
        {
            WpfMessageBox.Show(this, "请输入 10~132 的数字。", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _state.Notes.FontSize = fontSize;
        UpdateNotesText();
        SaveState();
    }

    private void ConfigureCountdownFontSize()
    {
        if (!TryPrompt("倒计时字号", "请输入倒计时字号（10~60）", _state.App.CountdownFontSize.ToString("0"), out string value))
        {
            return;
        }

        if (!double.TryParse(value, out double fontSize) || fontSize < 10 || fontSize > 60)
        {
            WpfMessageBox.Show(this, "请输入 10~60 的数字。", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _state.App.CountdownFontSize = fontSize;
        ApplyResponsiveLayout();
        SaveState();
    }

    private void ConfigureNotesTextColor()
    {
        if (!TrySelectColor("选择便签文字颜色", _state.App.NotesTextColor, out string colorHex))
        {
            return;
        }

        _state.App.NotesTextColor = colorHex;
        ApplyTextAppearance();
        SaveState();
    }

    private void ConfigureCountdownTextColor()
    {
        if (!TrySelectColor("选择倒计时文字颜色", _state.App.CountdownTextColor, out string colorHex))
        {
            return;
        }

        _state.App.CountdownTextColor = colorHex;
        ApplyTextAppearance();
        SaveState();
    }

    private void ConfigureBackgroundColor()
    {
        if (!TrySelectColor("选择背景主色", _state.App.BackgroundColor, out string colorHex))
        {
            return;
        }

        _state.App.BackgroundColor = colorHex;
        ApplyVisualEffects();
        SaveState();
    }

    private void ToggleNoteRotation(bool enabled)
    {
        _state.Notes.EnableRotation = enabled;
        ConfigureNoteRotationTimer();
        SaveState();
    }

    private void ConfigureRotationSeconds()
    {
        if (!TryPrompt("便签切换", "请输入切换间隔秒数（>=2）", _state.Notes.RotationSeconds.ToString(), out string value))
        {
            return;
        }

        if (!int.TryParse(value, out int seconds) || seconds < 2)
        {
            WpfMessageBox.Show(this, "请输入大于等于 2 的整数秒数。", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _state.Notes.RotationSeconds = seconds;
        ConfigureNoteRotationTimer();
        SaveState();
    }

    private void ConfigureWebUrl()
    {
        if (!TryPrompt("定时打开网页", "请输入完整 URL（例：https://example.com）", _state.WebTask.Url, out string value))
        {
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            WpfMessageBox.Show(this, "URL 格式无效。", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _state.WebTask.Url = value;
        SaveState();
    }

    private void ToggleWebOpenOnComplete(bool enabled)
    {
        _state.WebTask.EnableOnTimerComplete = enabled;
        SaveState();
    }

    private void ToggleWorkSession(bool enabled)
    {
        if (!enabled && !_state.Timer.EnableRestSession)
        {
            _state.Timer.EnableWorkSession = true;
            if (_workSessionEnabledItem is not null)
            {
                SetMenuChecked(_workSessionEnabledItem, true);
            }
            WpfMessageBox.Show(this, "工作时间和休息时间至少要保留一个。", "设置提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _state.Timer.EnableWorkSession = enabled;
        UpdateTimerModeChecks();
        ResetTimer();
        SaveState();
    }

    private void ToggleRestSession(bool enabled)
    {
        if (!enabled && !_state.Timer.EnableWorkSession)
        {
            _state.Timer.EnableRestSession = true;
            if (_restSessionEnabledItem is not null)
            {
                SetMenuChecked(_restSessionEnabledItem, true);
            }
            WpfMessageBox.Show(this, "工作时间和休息时间至少要保留一个。", "设置提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _state.Timer.EnableRestSession = enabled;
        UpdateTimerModeChecks();
        ResetTimer();
        SaveState();
    }

    private void ToggleSimpleMode(bool enabled)
    {
        _state.App.SimpleMode = enabled;
        ApplyResponsiveLayout();
        SaveState();
    }

    private void ToggleTopMost(bool enabled)
    {
        _state.App.TopMost = enabled;
        Topmost = enabled;
        SaveState();
    }

    private void ToggleFixedMode(bool enabled)
    {
        _state.App.FixedMode = enabled;
        ResizeMode = enabled ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        ApplyVisualEffects();
        ApplyResponsiveLayout();
        SaveState();
    }

    private void ToggleAutoStart(bool enabled)
    {
        try
        {
            SetAutoStart(enabled);
            _state.App.AutoStart = enabled;
            SaveState();
        }
        catch (UnauthorizedAccessException ex)
        {
            if (_autoStartItem is not null)
            {
                SetMenuChecked(_autoStartItem, !enabled);
            }
            _configService.AppendLog($"开机自启动设置失败: {ex.Message}");
            WpfMessageBox.Show(this, "当前权限不足，无法设置开机自启动。", "权限不足", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ToggleMinimizeToTrayOnClose(bool enabled)
    {
        _state.App.MinimizeToTrayOnClose = enabled;
        SaveState();
    }

    private void OpenConfigDirectory()
    {
        _ = Process.Start(new ProcessStartInfo
        {
            FileName = _configService.ConfigDirectory,
            UseShellExecute = true
        });
    }

    private void OpenGithubRepository()
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = RepositoryUrl,
                UseShellExecute = true
            });
            _configService.AppendLog($"已打开仓库: {RepositoryUrl}");
        }
        catch (Win32Exception ex)
        {
            _configService.AppendLog($"打开仓库失败: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            _configService.AppendLog($"打开仓库失败: {ex.Message}");
        }
    }

    private void ExitApplication()
    {
        _isExiting = true;
        SaveState();
        Close();
        WpfApplication.Current.Shutdown();
    }

    private void NoteRotationTimer_Tick(object? sender, EventArgs e)
    {
        if (!_state.Notes.EnableRotation || _state.NotesContent.Segments.Count <= 1)
        {
            return;
        }

        _state.NotesContent.CurrentIndex = (_state.NotesContent.CurrentIndex + 1) % _state.NotesContent.Segments.Count;
        UpdateNotesText();
        PersistRuntimeState();
    }

    private void LoopPlayer_MediaEnded(object? sender, EventArgs e)
    {
        if (!_isRunning || _currentPhase != TimerPhase.Work)
        {
            return;
        }

        if (sender is MediaPlayer player)
        {
            player.Position = TimeSpan.Zero;
            player.Play();
        }
    }

    private void LoopPlayer_MediaFailed(object? sender, ExceptionEventArgs e)
    {
        _configService.AppendLog($"过程音频播放失败: {e.ErrorException.Message}");
        ReleaseLoopAudioSource();
    }

    private void EndPlayer_MediaEnded(object? sender, EventArgs e)
    {
        ReleaseEndAudioSource();
    }

    private void EndPlayer_MediaFailed(object? sender, ExceptionEventArgs e)
    {
        _configService.AppendLog($"尾声音频播放失败: {e.ErrorException.Message}");
        ReleaseEndAudioSource();
    }

    private void ConfigureNoteRotationTimer()
    {
        _noteRotationTimer.Stop();
        _noteRotationTimer.Interval = TimeSpan.FromSeconds(Math.Max(2, _state.Notes.RotationSeconds));

        if (_state.Notes.EnableRotation && _state.NotesContent.Segments.Count > 1)
        {
            _noteRotationTimer.Start();
        }
    }

    private void MemoryGuardTimer_Tick(object? sender, EventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        Process process = Process.GetCurrentProcess();
        long workingSet = process.WorkingSet64;
        long privateBytes = process.PrivateMemorySize64;
        if (workingSet < MemoryTrimWorkingSetThresholdBytes && privateBytes < MemoryTrimPrivateThresholdBytes)
        {
            return;
        }

        if ((DateTime.UtcNow - _lastMemoryTrimUtc) < TimeSpan.FromSeconds(12))
        {
            return;
        }

        _lastMemoryTrimUtc = DateTime.UtcNow;
        if (!_memoryPressureMode)
        {
            _memoryPressureMode = true;
            ApplyVisualEffects();
        }

        if (_isEndAudioPlaying)
        {
            return;
        }

        if (!_isRunning)
        {
            ReleaseLoopAudioSource();
            ReleaseEndAudioSource();
        }
        TrimMemoryUsage();
    }

    private void LoadWindowIcon()
    {
        Stream? embeddedStream = TryOpenEmbeddedResource(DefaultIconFileName);
        if (embeddedStream is not null)
        {
            using (embeddedStream)
            {
                Icon = BitmapFrame.Create(embeddedStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            }
            return;
        }

        string iconPath = Path.Combine(AppContext.BaseDirectory, DefaultIconFileName);
        if (File.Exists(iconPath))
        {
            Icon = BitmapFrame.Create(new Uri(iconPath));
        }
    }

    private Drawing.Icon LoadTrayIcon()
    {
        Stream? embeddedStream = TryOpenEmbeddedResource(DefaultIconFileName);
        if (embeddedStream is not null)
        {
            using (embeddedStream)
            using (var icon = new Drawing.Icon(embeddedStream))
            {
                return (Drawing.Icon)icon.Clone();
            }
        }

        string iconPath = Path.Combine(AppContext.BaseDirectory, DefaultIconFileName);
        if (File.Exists(iconPath))
        {
            return new Drawing.Icon(iconPath);
        }

        return Drawing.SystemIcons.Application;
    }

    private void ApplyStateToUi()
    {
        Topmost = _state.App.TopMost;
        Width = Math.Clamp(_state.App.WindowWidth, 140, 960);
        Height = Math.Clamp(_state.App.WindowHeight, 88, 720);
        ResizeMode = _state.App.FixedMode ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;

        ApplyTextAppearance();
        UpdateCountdownText();
        UpdateNotesText();
        ApplyResponsiveLayout();
        UpdateTimerModeChecks();
    }

    private void ApplyVisualEffects()
    {
        double transparencyPercent = Math.Clamp(_state.App.Transparency, 0, 100);
        if (Math.Abs(_lastAppliedTransparencyPercent - transparencyPercent) > 0.001)
        {
            _lastAppliedTransparencyPercent = transparencyPercent;
            _configService.AppendLog($"视觉渲染模式: CompatibilityLayered, transparency={transparencyPercent:0}%");
        }

        double opacityRatio = 1.0 - (transparencyPercent / 100.0);
        opacityRatio = Math.Clamp(opacityRatio, 0.0, 1.0);

        Background = MediaBrushes.Transparent;
        AppleEdgeOverlay.Visibility = Visibility.Collapsed;
        MaterialOverlay.Visibility = Visibility.Collapsed;
        PanelBackground.Effect = null;

        if (_state.App.FixedMode)
        {
            PanelBackground.Background = MediaBrushes.Transparent;
            PanelBackground.BorderBrush = MediaBrushes.Transparent;
            RootBorder.Effect = null;
            return;
        }

        if (_memoryPressureMode)
        {
            RootBorder.Effect = null;
        }
        else
        {
            RootBorder.Effect = new DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 0,
                Opacity = 0.24,
                Color = Color.FromArgb(140, 0, 0, 0)
            };
        }

        Color baseColor = ParseMediaColor(_state.App.BackgroundColor, Color.FromRgb(255, 255, 255));
        if (_memoryPressureMode)
        {
            ApplyFrostedMode(baseColor, opacityRatio);
            PanelBackground.Effect = null;
            return;
        }

        switch (_state.App.BackgroundEffect)
        {
            case "Apple":
                ApplyAppleMode(baseColor, opacityRatio);
                break;
            case "Blur":
                ApplyBlurMode(baseColor, opacityRatio);
                break;
            default:
                ApplyFrostedMode(baseColor, opacityRatio);
                break;
        }
    }

    private void ApplyFrostedMode(Color baseColor, double opacityRatio)
    {
        Color top = BlendColor(baseColor, Color.FromRgb(255, 255, 255), 0.68);
        Color middle = BlendColor(baseColor, Color.FromRgb(237, 244, 252), 0.50);
        Color bottom = BlendColor(baseColor, Color.FromRgb(214, 226, 241), 0.22);

        byte aTop = (byte)Math.Clamp((int)(opacityRatio * 230), 0, 230);
        byte aMid = (byte)Math.Clamp((int)(opacityRatio * 198), 0, 198);
        byte aBottom = (byte)Math.Clamp((int)(opacityRatio * 174), 0, 174);
        byte borderAlpha = (byte)Math.Clamp((int)(opacityRatio * 205), 0, 220);

        var fill = new LinearGradientBrush
        {
            StartPoint = new MediaPoint(0, 0),
            EndPoint = new MediaPoint(0, 1)
        };
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(aTop, top.R, top.G, top.B), 0.0));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(aMid, middle.R, middle.G, middle.B), 0.52));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(aBottom, bottom.R, bottom.G, bottom.B), 1.0));

        PanelBackground.Background = fill;
        PanelBackground.BorderBrush = new SolidColorBrush(Color.FromArgb(borderAlpha, 247, 251, 255));

        var overlay = new LinearGradientBrush
        {
            StartPoint = new MediaPoint(0, 0),
            EndPoint = new MediaPoint(1, 1)
        };
        overlay.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Clamp((int)(opacityRatio * 95), 0, 95), 255, 255, 255), 0.0));
        overlay.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Clamp((int)(opacityRatio * 40), 0, 40), 233, 242, 252), 0.48));
        overlay.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Clamp((int)(opacityRatio * 72), 0, 72), 196, 213, 233), 1.0));
        MaterialOverlay.Background = overlay;
        MaterialOverlay.Visibility = Visibility.Visible;
    }

    private void ApplyBlurMode(Color baseColor, double opacityRatio)
    {
        Color top = BlendColor(baseColor, Color.FromRgb(255, 255, 255), 0.75);
        Color middle = BlendColor(baseColor, Color.FromRgb(232, 241, 252), 0.46);
        Color bottom = BlendColor(baseColor, Color.FromRgb(197, 213, 232), 0.28);

        byte aTop = (byte)Math.Clamp((int)(opacityRatio * 214), 0, 214);
        byte aMid = (byte)Math.Clamp((int)(opacityRatio * 186), 0, 186);
        byte aBottom = (byte)Math.Clamp((int)(opacityRatio * 164), 0, 164);
        byte borderAlpha = (byte)Math.Clamp((int)(opacityRatio * 212), 0, 220);

        var fill = new LinearGradientBrush
        {
            StartPoint = new MediaPoint(0, 0),
            EndPoint = new MediaPoint(0, 1)
        };
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(aTop, top.R, top.G, top.B), 0.0));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(aMid, middle.R, middle.G, middle.B), 0.56));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(aBottom, bottom.R, bottom.G, bottom.B), 1.0));

        PanelBackground.Background = fill;
        PanelBackground.BorderBrush = new SolidColorBrush(Color.FromArgb(borderAlpha, 250, 253, 255));
        PanelBackground.Effect = new BlurEffect
        {
            Radius = Math.Clamp((12 * opacityRatio) + 5, 3, 20)
        };

        var overlay = new RadialGradientBrush
        {
            Center = new MediaPoint(0.28, 0.18),
            GradientOrigin = new MediaPoint(0.28, 0.18),
            RadiusX = 1.05,
            RadiusY = 1.05
        };
        overlay.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Clamp((int)(opacityRatio * 110), 0, 110), 255, 255, 255), 0.0));
        overlay.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Clamp((int)(opacityRatio * 42), 0, 42), 242, 248, 255), 0.58));
        overlay.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Clamp((int)(opacityRatio * 88), 0, 88), 188, 205, 224), 1.0));
        MaterialOverlay.Background = overlay;
        MaterialOverlay.Visibility = Visibility.Visible;
    }

    private void ApplyAppleMode(Color baseColor, double opacityRatio)
    {
        byte edgeAlpha = (byte)Math.Clamp((int)(opacityRatio * 210), 0, 210);
        byte borderAlpha = (byte)Math.Clamp((int)(opacityRatio * 198), 0, 220);

        PanelBackground.Background = MediaBrushes.Transparent;
        PanelBackground.BorderBrush = new SolidColorBrush(Color.FromArgb(borderAlpha, 239, 246, 255));

        var edgeBrush = new RadialGradientBrush
        {
            Center = new MediaPoint(0.5, 0.5),
            GradientOrigin = new MediaPoint(0.5, 0.5),
            RadiusX = 0.85,
            RadiusY = 0.85
        };
        edgeBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, baseColor.R, baseColor.G, baseColor.B), 0.45));
        edgeBrush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(edgeAlpha / 2), baseColor.R, baseColor.G, baseColor.B), 0.72));
        edgeBrush.GradientStops.Add(new GradientStop(Color.FromArgb(edgeAlpha, baseColor.R, baseColor.G, baseColor.B), 1.0));

        AppleEdgeOverlay.Background = edgeBrush;
        AppleEdgeOverlay.Visibility = Visibility.Visible;
    }

    private static Color BlendColor(Color source, Color target, double ratio)
    {
        double clamped = Math.Clamp(ratio, 0, 1);
        byte r = (byte)Math.Clamp((int)Math.Round(source.R + ((target.R - source.R) * clamped)), 0, 255);
        byte g = (byte)Math.Clamp((int)Math.Round(source.G + ((target.G - source.G) * clamped)), 0, 255);
        byte b = (byte)Math.Clamp((int)Math.Round(source.B + ((target.B - source.B) * clamped)), 0, 255);
        return Color.FromRgb(r, g, b);
    }

    private static double QuantizeFontSize(double value)
    {
        return Math.Round(value);
    }

    private void ApplyResponsiveLayout()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        double widthScale = ActualWidth / 380.0;
        double heightScale = ActualHeight / 210.0;
        double scale = Math.Clamp(Math.Max(widthScale, heightScale), 0.40, 4.20);

        if (_state.App.SimpleMode)
        {
            NotesDisplayText.Visibility = Visibility.Collapsed;
            NotesDisplayText.Margin = new Thickness(0);
            CountdownText.VerticalAlignment = System.Windows.VerticalAlignment.Center;
            CountdownText.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            CountdownText.Margin = new Thickness(0);
            int textLength = Math.Max(5, CountdownText.Text?.Length ?? 5);
            double availableWidth = Math.Max(24, RootBorder.ActualWidth - 8);
            double availableHeight = Math.Max(24, RootBorder.ActualHeight - 8);
            double widthDriven = availableWidth / Math.Max(2.2, textLength * 0.58);
            double heightDriven = availableHeight * 0.82;
            CountdownText.FontSize = QuantizeFontSize(Math.Clamp(Math.Min(widthDriven, heightDriven), 12, 280));
            return;
        }

        NotesDisplayText.Visibility = Visibility.Visible;
        CountdownText.VerticalAlignment = VerticalAlignment.Bottom;
        CountdownText.Margin = new Thickness(0, 0, 0, 4);

        CountdownText.FontSize = QuantizeFontSize(Math.Clamp(_state.App.CountdownFontSize * scale, 14, 180));
        NotesDisplayText.FontSize = QuantizeFontSize(Math.Clamp(_state.Notes.FontSize * scale, 10, 132));

        double bottomGap = Math.Clamp((CountdownText.FontSize * 0.78) + (8 * scale), 24, 260);
        NotesDisplayText.Margin = new Thickness(8, 8, 8, bottomGap);
    }

    private void UpdateCountdownText()
    {
        if (GetDurationForPhase(_currentPhase) < 3600)
        {
            int minutes = _remainingSeconds / 60;
            int seconds = _remainingSeconds % 60;
            CountdownText.Text = $"{minutes:00}:{seconds:00}";
        }
        else
        {
            int hours = _remainingSeconds / 3600;
            int mins = (_remainingSeconds % 3600) / 60;
            int secs = _remainingSeconds % 60;
            CountdownText.Text = $"{hours:00}:{mins:00}:{secs:00}";
        }

        if (_state.App.SimpleMode)
        {
            ApplyResponsiveLayout();
        }
    }

    private void UpdateNotesText()
    {
        if (_state.NotesContent.Segments.Count == 0)
        {
            _state.NotesContent.Segments.Add(DefaultNoteText);
            _state.NotesContent.CurrentIndex = 0;
        }

        if (_state.NotesContent.CurrentIndex >= _state.NotesContent.Segments.Count)
        {
            _state.NotesContent.CurrentIndex = 0;
        }

        string raw = _state.NotesContent.Segments[_state.NotesContent.CurrentIndex] ?? string.Empty;
        ApplyNotesLayoutRules(raw);
        ApplyTextAppearance();
        ApplyResponsiveLayout();
    }

    private void ApplyNotesLayoutRules(string rawText)
    {
        string normalized = rawText.Replace("\r\n", "\n").Replace('\r', '\n');
        List<string> lines = normalized
            .Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (lines.Count <= 1)
        {
            NotesDisplayText.TextAlignment = TextAlignment.Center;
            NotesDisplayText.Text = lines.Count == 0 ? string.Empty : lines[0].Trim();
            return;
        }

        NotesDisplayText.TextAlignment = TextAlignment.Left;
        string firstLine = $"　　{lines[0].TrimStart()}";
        IEnumerable<string> remain = lines.Skip(1).Select(x => x.TrimStart());
        NotesDisplayText.Text = string.Join(Environment.NewLine, new[] { firstLine }.Concat(remain));
    }

    private void ApplyTextAppearance()
    {
        NotesDisplayText.FontFamily = ResolveFontFamily(_state.Notes.FontFamily);
        CountdownText.FontFamily = ResolveFontFamily(_state.App.CountdownFontFamily);
        NotesDisplayText.Foreground = new SolidColorBrush(ParseMediaColor(_state.App.NotesTextColor, Color.FromRgb(255, 182, 193)));
        CountdownText.Foreground = new SolidColorBrush(ParseMediaColor(_state.App.CountdownTextColor, Color.FromRgb(173, 216, 230)));
    }

    private static FontFamily ResolveFontFamily(string requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            try
            {
                return new FontFamily(requested);
            }
            catch (ArgumentException)
            {
                // Keep fallback below.
            }
        }

        FontFamily? matched = Fonts.SystemFontFamilies
            .FirstOrDefault(f => string.Equals(f.Source, requested, StringComparison.OrdinalIgnoreCase));
        return matched ?? new FontFamily("KaiTi");
    }

    private static Color ParseMediaColor(string colorText, Color fallback)
    {
        if (!TryParseHexColor(colorText, out Color parsed))
        {
            return fallback;
        }

        return parsed;
    }

    private static Drawing.Color ParseDrawingColor(string colorText, Drawing.Color fallback)
    {
        Color media = ParseMediaColor(colorText, Color.FromRgb(fallback.R, fallback.G, fallback.B));
        return Drawing.Color.FromArgb(media.R, media.G, media.B);
    }

    private static string ToHexColor(Drawing.Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static bool TryParseHexColor(string? colorText, out Color color)
    {
        color = Color.FromRgb(0, 0, 0);
        if (string.IsNullOrWhiteSpace(colorText))
        {
            return false;
        }

        string value = colorText.Trim();
        if (value.Length != 7 || value[0] != '#')
        {
            return false;
        }

        bool parsedR = byte.TryParse(value.Substring(1, 2), System.Globalization.NumberStyles.HexNumber, provider: null, out byte r);
        bool parsedG = byte.TryParse(value.Substring(3, 2), System.Globalization.NumberStyles.HexNumber, provider: null, out byte g);
        bool parsedB = byte.TryParse(value.Substring(5, 2), System.Globalization.NumberStyles.HexNumber, provider: null, out byte b);
        if (!parsedR || !parsedG || !parsedB)
        {
            return false;
        }

        color = Color.FromRgb(r, g, b);
        return true;
    }

    private static string FormatDuration(int totalSeconds)
    {
        int safe = Math.Max(0, totalSeconds);
        int h = safe / 3600;
        int m = (safe % 3600) / 60;
        int s = safe % 60;
        return $"{h:00}:{m:00}:{s:00}";
    }

    private static bool TryParseDuration(string input, out int totalSeconds)
    {
        totalSeconds = 0;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string normalized = input.Trim().Replace('：', ':');
        string[] parts = normalized.Split(':');
        if (parts.Length != 3)
        {
            return false;
        }

        bool parsedH = int.TryParse(parts[0], out int hours);
        bool parsedM = int.TryParse(parts[1], out int minutes);
        bool parsedS = int.TryParse(parts[2], out int seconds);
        if (!parsedH || !parsedM || !parsedS)
        {
            return false;
        }

        if (hours < 0 || minutes < 0 || minutes > 59 || seconds < 0 || seconds > 59)
        {
            return false;
        }

        totalSeconds = (hours * 3600) + (minutes * 60) + seconds;
        return totalSeconds > 0;
    }

    private bool TrySelectColor(string title, string currentHex, out string selectedHex)
    {
        using var dialog = new WinForms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            SolidColorOnly = false,
            Color = ParseDrawingColor(currentHex, Drawing.Color.FromArgb(76, 109, 140))
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            selectedHex = currentHex;
            return false;
        }

        selectedHex = ToHexColor(dialog.Color);
        _configService.AppendLog($"{title}: {selectedHex}");
        return true;
    }

    private bool TryPrompt(string title, string prompt, string defaultValue, out string value)
    {
        var dialog = new InputDialog(title, prompt, defaultValue)
        {
            Owner = this
        };
        bool? result = dialog.ShowDialog();
        value = dialog.Value;
        return result == true;
    }

    private string PickAudioFile()
    {
        var dialog = new MsOpenFileDialog
        {
            Title = "选择音频文件",
            Filter = "音频文件|*.wav;*.mp3;*.wma;*.aac;*.m4a|所有文件|*.*",
            CheckFileExists = true
        };

        bool? result = dialog.ShowDialog(this);
        return result == true ? dialog.FileName : string.Empty;
    }

    private void SaveState()
    {
        PersistRuntimeState();
        _configService.Save(_state);
    }

    private void PersistRuntimeState()
    {
        _state.Runtime.RemainingSeconds = _remainingSeconds;
        _state.Runtime.CurrentPhase = _currentPhase == TimerPhase.Work ? "Work" : "Rest";
        _state.Runtime.CurrentCycle = _currentCycle;
        _state.Runtime.TotalCycles = _totalCycles;
        _state.Runtime.PhaseIndex = _phaseIndexInCycle;
        _state.Runtime.SessionChainActive = _sessionChainActive;

        _state.App.TopMost = Topmost;
        _state.App.WindowWidth = (int)Width;
        _state.App.WindowHeight = (int)Height;
    }

    private static string GetCurrentExePath()
    {
        return Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
    }

    private static bool IsPathMatch(string left, string right)
    {
        string normalizedLeft = NormalizePathForCompare(left);
        string normalizedRight = NormalizePathForCompare(right);
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePathForCompare(string path)
    {
        string trimmed = path.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (ArgumentException)
        {
            return trimmed;
        }
        catch (NotSupportedException)
        {
            return trimmed;
        }
        catch (PathTooLongException)
        {
            return trimmed;
        }
    }

    private bool IsAutoStartEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        string? configuredPath = key?.GetValue(RunValueName) as string;
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return false;
        }

        return IsPathMatch(configuredPath, GetCurrentExePath());
    }

    private void SetAutoStart(bool enabled)
    {
        using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            throw new UnauthorizedAccessException("无法打开启动项注册表键。");
        }

        if (enabled)
        {
            key.SetValue(RunValueName, $"\"{GetCurrentExePath()}\"");
            return;
        }

        key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    private void DispatchToUi(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.Invoke(action);
    }

    private void SetMenuChecked(WinForms.ToolStripMenuItem menuItem, bool value)
    {
        if (menuItem.Checked == value)
        {
            return;
        }

        _suppressCheckCallbacks = true;
        try
        {
            menuItem.Checked = value;
        }
        finally
        {
            _suppressCheckCallbacks = false;
        }
    }

    private MediaPlayer EnsureLoopPlayer()
    {
        if (_loopPlayer is not null)
        {
            return _loopPlayer;
        }

        _loopPlayer = new MediaPlayer();
        _loopPlayer.MediaEnded += LoopPlayer_MediaEnded;
        _loopPlayer.MediaFailed += LoopPlayer_MediaFailed;
        return _loopPlayer;
    }

    private MediaPlayer EnsureEndPlayer()
    {
        if (_endPlayer is not null)
        {
            return _endPlayer;
        }

        _endPlayer = new MediaPlayer();
        _endPlayer.MediaEnded += EndPlayer_MediaEnded;
        _endPlayer.MediaFailed += EndPlayer_MediaFailed;
        return _endPlayer;
    }

    private void SuspendWindowForTray()
    {
        if (_suspendedForTray)
        {
            return;
        }

        _suspendedForTray = true;
        _noteRotationRunningBeforeSuspend = _noteRotationTimer.IsEnabled;
        _noteRotationTimer.Stop();

        RootBorder.Effect = null;
        PanelBackground.Effect = null;
        PanelBackground.Background = MediaBrushes.Transparent;
        PanelBackground.BorderBrush = MediaBrushes.Transparent;
        AppleEdgeOverlay.Background = null;
        AppleEdgeOverlay.Visibility = Visibility.Collapsed;
        MaterialOverlay.Background = null;
        MaterialOverlay.Visibility = Visibility.Collapsed;

        TrimMemoryUsage();
    }

    private void ResumeWindowFromTray()
    {
        if (!_suspendedForTray)
        {
            return;
        }

        _suspendedForTray = false;
        if (_noteRotationRunningBeforeSuspend)
        {
            ConfigureNoteRotationTimer();
        }
    }

    private void ReleaseLoopAudioSource()
    {
        if (_loopPlayer is not null)
        {
            _loopPlayer.MediaEnded -= LoopPlayer_MediaEnded;
            _loopPlayer.MediaFailed -= LoopPlayer_MediaFailed;
            _loopPlayer.Stop();
            _loopPlayer.Close();
            _loopPlayer = null;
        }
        _loopSourceLoaded = false;
        _activeLoopPath = string.Empty;
    }

    private void ReleaseEndAudioSource()
    {
        _isEndAudioPlaying = false;
        if (_endPlayer is null)
        {
            return;
        }

        _endPlayer.MediaEnded -= EndPlayer_MediaEnded;
        _endPlayer.MediaFailed -= EndPlayer_MediaFailed;
        _endPlayer.Stop();
        _endPlayer.Close();
        _endPlayer = null;
    }

    private void TrimMemoryUsage()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        IntPtr handle = Process.GetCurrentProcess().Handle;
        bool trimmed = EmptyWorkingSet(handle);
        if (!trimmed)
        {
            int errorCode = Marshal.GetLastWin32Error();
            _configService.AppendLog($"工作集收缩失败: {errorCode}");
        }
    }

    private void DisposeTrayIcon()
    {
        _memoryGuardTimer.Stop();
        _memoryGuardTimer.Tick -= MemoryGuardTimer_Tick;

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _leftPresetMenu?.Dispose();
        _leftPresetMenu = null;

        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}
