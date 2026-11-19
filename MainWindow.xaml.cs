using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipFlux.Models;
using ClipFlux.Native;
using ClipFlux.Services;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ClipFlux;

public partial class MainWindow : Window
{
    private static readonly Geometry MaximizeIconGeometry = Geometry.Parse("M 1,1 H 10 V 10 H 1 Z");
    private static readonly Geometry RestoreIconGeometry = Geometry.Parse("M 3,1 H 10 V 8 M 1,3 H 8 V 10 H 1 Z");
    private const string CustomCloudTag = "__CUSTOM__";

    private readonly DatabaseService _dbService;
    private readonly ClipboardService _clipService;
    private readonly HotkeyService _hotkeyService;
    private readonly AppSettings _settings;

    private readonly ObservableCollection<ClipItem> _clips = new();
    private readonly List<ClipItem> _trayRecentCache = new();
    private List<CloudDriveOption> _detectedCloudDrives = new();
    private string _selectedCloudFolder = string.Empty;

    private IntPtr _previousForegroundHwnd = IntPtr.Zero;
    private int? _currentTypeFilter = null;
    private bool _onlyStarred = false;
    private int _currentDisplayLimit = 1000;
    private int _totalCategoryClips = 0;

    private bool _isInitialized = false;
    private bool _suppressPreviewModeEvent = false;
    private bool _suppressThemeEvent = false;
    private bool _suppressLanguageEvent = false;
    private bool _suppressCloudSelectionEvent = false;
    private bool _isRealExit = false;

    private Forms.NotifyIcon? _notifyIcon;
    private ClipItem? _previouslySelectedHeavyItem;
    private Win32.WinEventDelegate? _foregroundEventDelegate;
    private IntPtr _foregroundHook = IntPtr.Zero;
    private DispatcherTimer? _backupTimer;

    private CancellationTokenSource? _searchDebounceCts;
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _trimCts;
    private CancellationTokenSource? _statusCts;
    private CancellationTokenSource? _previewFontDebounceCts;

    private uint _editingModifiers = 0x0001;
    private uint _editingKey = 0x56;
    private string _editingDisplay = "Alt + V";

    public MainWindow()
    {
        _dbService = new DatabaseService();
        _clipService = new ClipboardService();
        _hotkeyService = new HotkeyService();
        _settings = AppSettings.Load();

        LocalizationService.ApplyLanguage(_settings.LanguageMode);
        InitializeComponent();
        ApplyCurrentTheme(_settings.ThemeMode);
        SystemEvents.UserPreferenceChanged += OnSystemUserPreferenceChanged;

        ClipList.ItemsSource = _clips;
        _isInitialized = true;

        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Maximized)
            {
                MainOuterBorder.CornerRadius = new CornerRadius(0);
                PathMaximize.Data = RestoreIconGeometry;
            }
            else
            {
                MainOuterBorder.CornerRadius = new CornerRadius(12);
                PathMaximize.Data = MaximizeIconGeometry;
            }
        };

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_SETTINGCHANGE)
        {
            if (ThemeService.NormalizeMode(_settings.ThemeMode) == ThemeService.ModeSystem)
            {
                Dispatcher.BeginInvoke(() => ApplyCurrentTheme(ThemeService.ModeSystem));
            }
            return IntPtr.Zero;
        }

        if (msg == Win32.WM_GETMINMAXINFO)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            Win32.WmGetMinMaxInfo(hwnd, lParam, (int)(880 * dpi.DpiScaleX), (int)(560 * dpi.DpiScaleY));
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == Win32.WM_NCHITTEST)
        {
            if (WindowState == WindowState.Maximized) return IntPtr.Zero;

            short screenX = unchecked((short)(lParam.ToInt64() & 0xFFFF));
            short screenY = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
            Point pt = PointFromScreen(new Point(screenX, screenY));

            const int resizeBorder = 8;
            bool left = pt.X < resizeBorder;
            bool right = pt.X > ActualWidth - resizeBorder;
            bool top = pt.Y < resizeBorder;
            bool bottom = pt.Y > ActualHeight - resizeBorder;

            if (left && top) { handled = true; return (IntPtr)Win32.HTTOPLEFT; }
            if (right && top) { handled = true; return (IntPtr)Win32.HTTOPRIGHT; }
            if (left && bottom) { handled = true; return (IntPtr)Win32.HTBOTTOMLEFT; }
            if (right && bottom) { handled = true; return (IntPtr)Win32.HTBOTTOMRIGHT; }
            if (left) { handled = true; return (IntPtr)Win32.HTLEFT; }
            if (right) { handled = true; return (IntPtr)Win32.HTRIGHT; }
            if (top) { handled = true; return (IntPtr)Win32.HTTOP; }
            if (bottom) { handled = true; return (IntPtr)Win32.HTBOTTOM; }
        }

        return IntPtr.Zero;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _dbService.InitializeAsync();

            _clipService.StartListening(this);
            _clipService.ClipCaptured += OnClipCaptured;

            _hotkeyService.Initialize(this);
            RegisterConfiguredHotkeys();

            _foregroundEventDelegate = OnForegroundWindowChanged;
            _foregroundHook = Win32.SetWinEventHook(
                Win32.EVENT_SYSTEM_FOREGROUND,
                Win32.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _foregroundEventDelegate,
                0,
                0,
                Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);

            SetupSystemTray();
            ApplyLoadedSettings();

            bool isSilent = Environment.GetCommandLineArgs().Any(a =>
                a.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("/silent", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("-silent", StringComparison.OrdinalIgnoreCase));

            if (isSilent)
            {
                HideToBackground();
            }
            else
            {
                await RefreshListAsync();
                TxtSearch.Focus();
            }

            await RefreshTrayCacheAsync();

            // Populate missing SQL url columns for recent HTML history items without blocking startup
            _ = Task.Run(() => _dbService.BackfillRecentHtmlUrlsAsync(500));

            StartBackupScheduler();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Initialization error: {ex.Message}", "ClipFlux", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartBackupScheduler()
    {
        _backupTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _backupTimer.Tick += async (_, _) => await ExecuteScheduledBackupIfDueAsync();
        _backupTimer.Start();

        _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ =>
        {
            if (!_isRealExit)
            {
                Dispatcher.BeginInvoke(async () => await ExecuteScheduledBackupIfDueAsync());
            }
        });
    }

    private async Task ExecuteScheduledBackupIfDueAsync()
    {
        if (_isRealExit || !BackupService.IsScheduledBackupDue(_settings)) return;
        await RunBackupFlowAsync(showFeedback: false);
    }

    private void RegisterConfiguredHotkeys()
    {
        _hotkeyService.UnregisterAll();

        if (!_settings.EnableGlobalHotkey)
        {
            UpdateTrayTooltip();
            return;
        }

        uint mod = _settings.HotkeyModifiers;
        uint key = _settings.HotkeyKey;
        if (key == 0)
        {
            mod = Win32.MOD_ALT;
            key = 0x56;
        }

        int id = _hotkeyService.Register(mod, key, OnGlobalHotkeyTriggered);
        if (id == -1 && TxtHotkeyStatusHint != null)
        {
            TxtHotkeyStatusHint.Text = LocalizationService.Format("Str_Set_HotkeyRegFailed", _settings.HotkeyDisplay);
            TxtHotkeyStatusHint.Foreground = (Brush)FindResource("BrushDanger");
        }

        UpdateTrayTooltip();
    }

    private void UpdateTrayTooltip()
    {
        string hotkeyDesc = !_settings.EnableGlobalHotkey
            ? LocalizationService.Get("Str_Hotkey_Disabled", "Disabled")
            : _settings.HotkeyDisplay;

        if (_notifyIcon != null)
        {
            _notifyIcon.Text = LocalizationService.Format("Str_TrayText", hotkeyDesc);
        }
        if (TxtHotkeyLegend != null)
        {
            TxtHotkeyLegend.Text = !_settings.EnableGlobalHotkey
                ? LocalizationService.Get("Str_Hotkey_LegendOff", "Global Hotkey: Disabled")
                : LocalizationService.Format("Str_Hotkey_LegendOn", hotkeyDesc);
        }
    }

    private void SetupSystemTray()
    {
        try
        {
            _notifyIcon = new Forms.NotifyIcon
            {
                Text = "ClipFlux",
                Visible = true
            };
            UpdateTrayTooltip();

            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(iconPath))
                {
                    _notifyIcon.Icon = new System.Drawing.Icon(iconPath);
                }
                else
                {
                    var exePath = Environment.ProcessPath;
                    _notifyIcon.Icon = !string.IsNullOrEmpty(exePath)
                        ? System.Drawing.Icon.ExtractAssociatedIcon(exePath) ?? System.Drawing.SystemIcons.Application
                        : System.Drawing.SystemIcons.Application;
                }
            }
            catch
            {
                _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }

            _notifyIcon.DoubleClick += (_, _) => ShowAndActivate();
            _notifyIcon.Click += (_, e) =>
            {
                if (e is Forms.MouseEventArgs me && me.Button == Forms.MouseButtons.Left)
                {
                    ShowAndActivate();
                }
            };

            var contextMenu = new Forms.ContextMenuStrip();
            contextMenu.Opening += (_, _) => PopulateTrayContextMenu(contextMenu);
            _notifyIcon.ContextMenuStrip = contextMenu;
        }
        catch { }
    }

    private async Task RefreshTrayCacheAsync()
    {
        try
        {
            int count = Math.Clamp(_settings.TrayRecentCount, 1, 20);
            var recent = await _dbService.GetRecentClipsForTrayAsync(count * 3);
            Dispatcher.Invoke(() =>
            {
                _trayRecentCache.Clear();
                var seenKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in recent)
                {
                    string key = $"{item.Type}:{item.Title}";
                    if (seenKeys.Add(key))
                    {
                        _trayRecentCache.Add(item);
                        if (_trayRecentCache.Count >= 20) break;
                    }
                }
            });
        }
        catch { }
    }

    private void UpdateTrayCacheWithItem(ClipItem item)
    {
        string key = $"{item.Type}:{item.Title}";
        for (int i = _trayRecentCache.Count - 1; i >= 0; i--)
        {
            if ($"{_trayRecentCache[i].Type}:{_trayRecentCache[i].Title}" == key)
            {
                _trayRecentCache.RemoveAt(i);
            }
        }

        _trayRecentCache.Insert(0, new ClipItem
        {
            Id = item.Id,
            Created = item.Created,
            Title = item.Title,
            Type = item.Type,
            Description = item.Description,
            DataSize = item.DataSize,
            AppName = item.AppName,
            AppTitle = item.AppTitle,
            IsStarred = item.IsStarred,
            Tags = item.Tags,
            Url = item.DisplayUrl
        });

        while (_trayRecentCache.Count > 20)
        {
            _trayRecentCache.RemoveAt(_trayRecentCache.Count - 1);
        }
    }

    private void PopulateTrayContextMenu(Forms.ContextMenuStrip menu)
    {
        menu.Items.Clear();

        int count = Math.Clamp(_settings.TrayRecentCount, 1, 20);
        IEnumerable<ClipItem> source = _trayRecentCache.Count > 0 ? _trayRecentCache : _clips;

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var recentItems = new List<ClipItem>();
        foreach (var clip in source)
        {
            string key = $"{clip.Type}:{clip.Title}";
            if (seenKeys.Add(key))
            {
                recentItems.Add(clip);
                if (recentItems.Count >= count) break;
            }
        }

        if (recentItems.Count > 0)
        {
            var header = new Forms.ToolStripMenuItem(LocalizationService.Format("Str_Tray_RecentHeader", recentItems.Count))
            {
                Enabled = false,
                Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Bold)
            };
            menu.Items.Add(header);

            string recentTip = LocalizationService.Get("Str_Tray_RecentTip", "Click to copy plain text");
            for (int i = 0; i < recentItems.Count; i++)
            {
                var item = recentItems[i];
                string typeIcon = item.Type switch
                {
                    ClipType.Image => "🖼️",
                    ClipType.Files => "📁",
                    ClipType.Html => "🌐",
                    ClipType.RichText => "📝",
                    _ => "📄"
                };

                string title = item.Title.Replace("\r", " ").Replace("\n", " ").Trim();
                if (title.Length > 28) title = title[..28] + "...";
                if (string.IsNullOrWhiteSpace(title)) title = $"[{item.TypeDisplayName}]";

                var menuItem = new Forms.ToolStripMenuItem($"{i + 1}. {typeIcon} {title}")
                {
                    ToolTipText = recentTip
                };
                menuItem.Click += async (_, _) =>
                {
                    if (item.Data == null || item.Data.Length == 0)
                    {
                        await _dbService.EnsureDataLoadedAsync(item);
                    }
                    _clipService.CopyToClipboard(item, asPlainText: true);
                };
                menu.Items.Add(menuItem);
            }

            menu.Items.Add(new Forms.ToolStripSeparator());
        }

        string showText = !_settings.EnableGlobalHotkey
            ? LocalizationService.Get("Str_Tray_ShowNoHotkey", "Show Window (Click Tray)")
            : LocalizationService.Format("Str_Tray_ShowHotkey", _settings.HotkeyDisplay);

        menu.Items.Add(showText, null, (_, _) => ShowAndActivate());
        menu.Items.Add(LocalizationService.Get("Str_Tray_Settings", "Settings..."), null, (_, _) => { ShowAndActivate(); BtnSettings_Click(this, new RoutedEventArgs()); });
        menu.Items.Add(LocalizationService.Get("Str_Tray_BackupNow", "Backup History Now"), null, async (_, _) => await RunBackupFlowAsync(showFeedback: true));
        menu.Items.Add(LocalizationService.Get("Str_Tray_Import", "Import History Data..."), null, (_, _) => BtnImport_Click(this, new RoutedEventArgs()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(LocalizationService.Get("Str_Tray_Exit", "Exit ClipFlux"), null, (_, _) => ExitApplication());
    }

    private void ApplyLoadedSettings()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_settings.FontFamilyName))
            {
                RootWindow.FontFamily = new FontFamily(_settings.FontFamilyName);
                foreach (ComboBoxItem item in CmbFontFamily.Items)
                {
                    if (item.Tag?.ToString() == _settings.FontFamilyName)
                    {
                        item.IsSelected = true;
                        break;
                    }
                }
            }

            SliderNavFontSize.Value = _settings.NavFontSize;
            SliderListFontSize.Value = _settings.ListFontSize;
            SliderPreviewFontSize.Value = _settings.PreviewFontSize;
            SliderGlobalScale.Value = _settings.GlobalFontScale;

            NavFilterScroll.FontSize = _settings.NavFontSize;
            TxtSearch.FontSize = _settings.NavFontSize + 0.5;
            ClipList.FontSize = _settings.ListFontSize;
            TxtPreviewContent.FontSize = _settings.PreviewFontSize;
            RichPreviewViewer.FontSize = _settings.PreviewFontSize;

            _suppressLanguageEvent = true;
            try
            {
                string langMode = LocalizationService.NormalizeMode(_settings.LanguageMode);
                LangSystem.IsChecked = langMode == LocalizationService.ModeSystem;
                LangZhCn.IsChecked = langMode == LocalizationService.ModeZhCn;
                LangEnUs.IsChecked = langMode == LocalizationService.ModeEnUs;
            }
            finally
            {
                _suppressLanguageEvent = false;
            }

            _suppressThemeEvent = true;
            try
            {
                string themeMode = ThemeService.NormalizeMode(_settings.ThemeMode);
                ThemeSystem.IsChecked = themeMode == ThemeService.ModeSystem;
                ThemeLight.IsChecked = themeMode == ThemeService.ModeLight;
                ThemeDark.IsChecked = themeMode == ThemeService.ModeDark;
            }
            finally
            {
                _suppressThemeEvent = false;
            }
            ApplyCurrentTheme(_settings.ThemeMode);

            GlobalScaleTransform.ScaleX = _settings.GlobalFontScale;
            GlobalScaleTransform.ScaleY = _settings.GlobalFontScale;

            SliderTrayRecentCount.Value = _settings.TrayRecentCount;
            int maxCount = _settings.MaxDisplayCount > 0 ? _settings.MaxDisplayCount : 1000;
            SliderMaxDisplayCount.Value = maxCount;
            _currentDisplayLimit = maxCount;

            ChkEnableHotkey.IsChecked = _settings.EnableGlobalHotkey;
            PanelHotkeyChoice.IsEnabled = _settings.EnableGlobalHotkey;
            PanelHotkeyChoice.Opacity = _settings.EnableGlobalHotkey ? 1.0 : 0.45;

            _editingModifiers = _settings.HotkeyModifiers;
            _editingKey = _settings.HotkeyKey;
            _editingDisplay = _settings.HotkeyDisplay;
            TxtCustomHotkey.Text = _settings.HotkeyDisplay;
            ChkAutoStart.IsChecked = _settings.AutoStart;

            // Backup settings hydration
            TxtLocalBackupDir.Text = string.IsNullOrWhiteSpace(_settings.LocalBackupDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ClipFluxBackups")
                : _settings.LocalBackupDir;

            ChkEnableScheduledBackup.IsChecked = _settings.EnableScheduledBackup;
            TogglePanelState(PanelScheduleOptions, _settings.EnableScheduledBackup);
            SelectComboItemByTag(CmbBackupInterval, _settings.BackupIntervalHours.ToString(), fallbackIndex: 2);
            SelectComboItemByTag(CmbBackupRetention, _settings.MaxBackupRetention.ToString(), fallbackIndex: 2);

            _selectedCloudFolder = _settings.CloudDriveFolder ?? string.Empty;
            PopulateCloudDriveCombo();
            ChkEnableCloudBackup.IsChecked = _settings.EnableCloudDriveBackup;
            TogglePanelState(PanelCloudOptions, _settings.EnableCloudDriveBackup);

            ChkEnableWebDavBackup.IsChecked = _settings.EnableWebDavBackup;
            TogglePanelState(PanelWebDavOptions, _settings.EnableWebDavBackup);
            TxtWebDavUrl.Text = _settings.WebDavUrl ?? string.Empty;
            TxtWebDavUser.Text = _settings.WebDavUsername ?? string.Empty;
            PwdWebDav.Password = Win32.UnprotectString(_settings.WebDavPasswordProtected);

            RefreshLocalizedDynamicLabels();
        }
        catch { }
    }

    private static void SelectComboItemByTag(ComboBox combo, string tagValue, int fallbackIndex = 0)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tagValue, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > fallbackIndex)
        {
            combo.SelectedIndex = fallbackIndex;
        }
    }

    private static void TogglePanelState(FrameworkElement panel, bool enabled)
    {
        panel.IsEnabled = enabled;
        panel.Opacity = enabled ? 1.0 : 0.45;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isRealExit)
        {
            e.Cancel = true;
            HideToBackground();
        }
    }

    private void OnSystemUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General &&
            ThemeService.NormalizeMode(_settings.ThemeMode) == ThemeService.ModeSystem)
        {
            Dispatcher.BeginInvoke(() => ApplyCurrentTheme(ThemeService.ModeSystem));
        }
    }

    private void ApplyCurrentTheme(string? themeMode)
    {
        ThemeService.ApplyTheme(themeMode);

        if (WindowDropShadow != null)
        {
            WindowDropShadow.Opacity = ThemeService.IsDarkTheme ? 0.45 : 0.20;
        }

        foreach (var item in _clips)
        {
            item.NotifyThemeChanged();
        }

        if (_isInitialized && ClipList?.SelectedItem is ClipItem current && RichPreviewViewer?.Visibility == Visibility.Visible)
        {
            UpdatePreviewPane(current);
        }
    }

    private void ApplyCurrentLanguage(string? languageMode)
    {
        LocalizationService.ApplyLanguage(languageMode);

        foreach (var item in _clips)
        {
            item.NotifyLocaleChanged();
        }

        RefreshLocalizedDynamicLabels();
        _ = UpdateStatsAsync();
        if (_isInitialized && ClipList?.SelectedItem is ClipItem current)
        {
            UpdatePreviewPane(current);
        }
    }

    private void RefreshLocalizedDynamicLabels()
    {
        if (!_isInitialized) return;

        TxtNavFontSizeVal.Text = $"{SliderNavFontSize.Value:F0} px";
        TxtListFontSizeVal.Text = $"{SliderListFontSize.Value:F0} px";
        TxtPreviewFontSizeVal.Text = $"{SliderPreviewFontSize.Value:F1} px";
        TxtGlobalScaleVal.Text = $"{SliderGlobalScale.Value:P0}";

        TxtTrayRecentCountVal.Text = LocalizationService.Format("Str_Unit_Items", (int)SliderTrayRecentCount.Value);
        TxtMaxDisplayCountVal.Text = LocalizationService.Format("Str_Unit_Items", (int)Math.Round(SliderMaxDisplayCount.Value));

        foreach (ComboBoxItem item in CmbBackupRetention.Items)
        {
            if (item.Tag is string tag)
            {
                item.Content = LocalizationService.Format("Str_Bak_RetFormat", tag);
            }
        }

        UpdateBackupStatusLabel();
        UpdateCloudBackupTargetLabel();
        UpdateTrayTooltip();
        UpdateSelectionUi();

        if (TxtStatus != null)
        {
            TxtStatus.Text = LocalizationService.Get("Str_Status_Ready", "Ready");
        }
    }

    private void UpdateBackupStatusLabel()
    {
        if (TxtBackupStatus == null) return;
        TxtBackupStatus.Text = string.IsNullOrWhiteSpace(_settings.LastBackupTime)
            ? LocalizationService.Get("Str_Bak_Never", "Last backup: Never")
            : LocalizationService.Format("Str_Bak_LastFormat", _settings.LastBackupTime);
    }

    private void ExitApplication()
    {
        _isRealExit = true;
        _backupTimer?.Stop();
        SystemEvents.UserPreferenceChanged -= OnSystemUserPreferenceChanged;
        _trimCts?.Cancel();
        _searchDebounceCts?.Cancel();
        _refreshCts?.Cancel();
        _statusCts?.Cancel();
        _previewFontDebounceCts?.Cancel();

        try
        {
            if (_foregroundHook != IntPtr.Zero)
            {
                Win32.UnhookWinEvent(_foregroundHook);
                _foregroundHook = IntPtr.Zero;
            }
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            _clipService.StopListening();
            _hotkeyService.UnregisterAll();
        }
        catch { }
        Application.Current.Shutdown();
    }

    private void OnForegroundWindowChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero) return;
        var myHwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == myHwnd || Win32.IsShellOrTrayWindow(hwnd) || !Win32.IsWindowVisible(hwnd)) return;
        _previousForegroundHwnd = hwnd;
    }

    private void CaptureTargetForegroundWindow()
    {
        var myHwnd = new WindowInteropHelper(this).Handle;
        IntPtr fg = Win32.GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != myHwnd && Win32.IsWindowVisible(fg) && !Win32.IsShellOrTrayWindow(fg))
        {
            _previousForegroundHwnd = fg;
        }
        else if (_previousForegroundHwnd == IntPtr.Zero ||
                 _previousForegroundHwnd == myHwnd ||
                 !Win32.IsWindow(_previousForegroundHwnd) ||
                 !Win32.IsWindowVisible(_previousForegroundHwnd) ||
                 Win32.IsShellOrTrayWindow(_previousForegroundHwnd))
        {
            IntPtr fallback = Win32.FindFallbackForegroundWindow(myHwnd);
            if (fallback != IntPtr.Zero)
            {
                _previousForegroundHwnd = fallback;
            }
        }
    }

    private IntPtr ResolveTargetPasteWindow(IntPtr myHwnd)
    {
        if (_previousForegroundHwnd != IntPtr.Zero &&
            _previousForegroundHwnd != myHwnd &&
            Win32.IsWindow(_previousForegroundHwnd) &&
            Win32.IsWindowVisible(_previousForegroundHwnd) &&
            !Win32.IsShellOrTrayWindow(_previousForegroundHwnd))
        {
            return _previousForegroundHwnd;
        }
        return Win32.FindFallbackForegroundWindow(myHwnd);
    }

    private void OnGlobalHotkeyTriggered()
    {
        if (IsVisible && IsActive)
        {
            HideToBackground();
        }
        else
        {
            CaptureTargetForegroundWindow();
            ShowAndActivate();
        }
    }

    public async void ShowAndActivate()
    {
        _trimCts?.Cancel();
        CaptureTargetForegroundWindow();

        Topmost = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Focus();

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            Topmost = false;
        }), DispatcherPriority.ApplicationIdle);

        if (_clips.Count == 0)
        {
            await RefreshListAsync();
        }
        else if (ClipList.SelectedItem is ClipItem selected)
        {
            if (selected.Data == null || selected.Data.Length == 0)
            {
                await _dbService.EnsureDataLoadedAsync(selected);
            }
            UpdatePreviewPane(selected);
        }

        TxtSearch.Focus();
        TxtSearch.SelectAll();
    }

    public void HideToBackground(ClipItem? preserveItem = null)
    {
        ClearConfirmOverlay.Visibility = Visibility.Collapsed;
        SettingsOverlay.Visibility = Visibility.Collapsed;
        MultiPasteOverlay.Visibility = Visibility.Collapsed;
        RichPreviewViewer.Document = null;
        TxtPreviewContent.Text = string.Empty;
        Hide();

        _previouslySelectedHeavyItem = null;
        foreach (var item in _clips)
        {
            if (preserveItem != null && ReferenceEquals(item, preserveItem)) continue;
            item.ClearDataAndCache();
        }

        _trimCts?.Cancel();
        var cts = new CancellationTokenSource();
        _trimCts = cts;
        _ = ScheduleDelayedTrimAsync(cts.Token);
    }

    private async Task ScheduleDelayedTrimAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(800, token);
            if (token.IsCancellationRequested || IsVisible) return;
            Win32.TrimWorkingSet();
        }
        catch (OperationCanceledException) { }
    }

    private async void OnClipCaptured(ClipItem item)
    {
        await _dbService.SaveClipAsync(item);

        Dispatcher.Invoke(() =>
        {
            UpdateTrayCacheWithItem(item);

            bool hasSearch = !string.IsNullOrWhiteSpace(TxtSearch.Text);
            if (MatchesFilter(item) && !hasSearch)
            {
                var existing = _clips.FirstOrDefault(c =>
                    c.Type == item.Type &&
                    c.Title == item.Title &&
                    (c.DataSize == item.DataSize || c.Description == item.Description));

                if (existing != null)
                {
                    _clips.Remove(existing);
                }

                if (!IsVisible)
                {
                    item.ClearDataAndCache();
                }

                _clips.Insert(0, item);
                EmptyPlaceholder.Visibility = Visibility.Collapsed;
                if (ClipList.SelectedIndex == -1)
                {
                    ClipList.SelectedIndex = 0;
                }
            }
            else if (!IsVisible)
            {
                item.ClearDataAndCache();
            }

            UpdateSelectionUi();
            _ = UpdateStatsAsync();
        });
    }

    private bool MatchesFilter(ClipItem item)
    {
        if (_onlyStarred && !item.IsStarred) return false;
        if (_currentTypeFilter.HasValue && (int)item.Type != _currentTypeFilter.Value) return false;
        return true;
    }

    private async Task RefreshListAsync()
    {
        if (!_isInitialized) return;

        _refreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        var token = cts.Token;

        try
        {
            string search = TxtSearch.Text.Trim();
            bool hasSearch = !string.IsNullOrWhiteSpace(search);

            var items = await _dbService.GetClipsAsync(search, _currentTypeFilter, _onlyStarred, _currentDisplayLimit, 0, token);
            token.ThrowIfCancellationRequested();

            int total = hasSearch
                ? items.Count
                : await _dbService.GetClipsCountAsync(null, _currentTypeFilter, _onlyStarred);
            token.ThrowIfCancellationRequested();

            _totalCategoryClips = total;
            _previouslySelectedHeavyItem = null;

            _clips.Clear();
            foreach (var item in items)
            {
                _clips.Add(item);
            }

            EmptyPlaceholder.Visibility = _clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (_clips.Count > 0)
            {
                ClipList.SelectedIndex = 0;
            }
            else
            {
                UpdatePreviewPane(null);
            }

            UpdateSelectionUi();
            await UpdateStatsAsync();
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async Task UpdateStatsAsync()
    {
        if (!_isInitialized) return;
        var (total, starred, dbSize) = await _dbService.GetStatsAsync();
        TxtStats.Text = LocalizationService.Format("Str_StatsFormat", total, starred);
        if (TxtDbInfo != null)
        {
            double mb = dbSize / (1024.0 * 1024.0);
            TxtDbInfo.Text = LocalizationService.Format("Str_Data_Storage", total, mb);
        }
    }

    private void ShowStatusMessage(string message)
    {
        _statusCts?.Cancel();
        var cts = new CancellationTokenSource();
        _statusCts = cts;

        TxtStatus.Text = message;
        TxtStatus.Foreground = (Brush)FindResource("BrushTextSecondary");

        _ = ResetStatusAfterDelayAsync(cts.Token);
    }

    private async Task ResetStatusAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(2400, token);
            if (!token.IsCancellationRequested)
            {
                TxtStatus.Text = LocalizationService.Get("Str_Status_Ready", "Ready");
                TxtStatus.Foreground = (Brush)FindResource("BrushTextMuted");
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && e.LeftButton == MouseButtonState.Pressed)
        {
            BtnMaximize_Click(sender, e);
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private async void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitialized) return;
        BtnClearSearch.Visibility = string.IsNullOrEmpty(TxtSearch.Text) ? Visibility.Collapsed : Visibility.Visible;

        _searchDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;

        try
        {
            await Task.Delay(160, cts.Token);
            if (!cts.Token.IsCancellationRequested)
            {
                await RefreshListAsync();
            }
        }
        catch (OperationCanceledException) { }
    }

    private async void BtnClearSearch_Click(object sender, RoutedEventArgs e)
    {
        _searchDebounceCts?.Cancel();
        TxtSearch.Text = string.Empty;
        TxtSearch.Focus();
        await RefreshListAsync();
    }

    private async void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        if (sender is not RadioButton rb) return;

        if (rb == FilterAll)
        {
            _currentTypeFilter = null;
            _onlyStarred = false;
        }
        else if (rb == FilterText)
        {
            _currentTypeFilter = (int)ClipType.Text;
            _onlyStarred = false;
        }
        else if (rb == FilterRichText)
        {
            _currentTypeFilter = (int)ClipType.RichText;
            _onlyStarred = false;
        }
        else if (rb == FilterHtml)
        {
            _currentTypeFilter = (int)ClipType.Html;
            _onlyStarred = false;
        }
        else if (rb == FilterImage)
        {
            _currentTypeFilter = (int)ClipType.Image;
            _onlyStarred = false;
        }
        else if (rb == FilterFiles)
        {
            _currentTypeFilter = (int)ClipType.Files;
            _onlyStarred = false;
        }
        else if (rb == FilterStarred)
        {
            _currentTypeFilter = null;
            _onlyStarred = true;
        }

        _currentDisplayLimit = _settings.MaxDisplayCount > 0 ? _settings.MaxDisplayCount : 1000;
        await RefreshListAsync();
    }

    private async void ClipList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionUi();

        if (ClipList.SelectedItem is not ClipItem item)
        {
            UpdatePreviewPane(null);
            return;
        }

        if (_previouslySelectedHeavyItem != null && !ReferenceEquals(_previouslySelectedHeavyItem, item))
        {
            if (_previouslySelectedHeavyItem.Type == ClipType.Image || _previouslySelectedHeavyItem.DataSize > 16384)
            {
                _previouslySelectedHeavyItem.ClearDataAndCache();
            }
            _previouslySelectedHeavyItem = null;
        }

        if (item.Data == null || item.Data.Length == 0)
        {
            await _dbService.EnsureDataLoadedAsync(item);
            if (!ReferenceEquals(ClipList.SelectedItem, item)) return;
        }

        if (item.Type == ClipType.Image || item.DataSize > 16384)
        {
            _previouslySelectedHeavyItem = item;
        }

        if (item.Type == ClipType.Html || item.Type == ClipType.RichText)
        {
            _suppressPreviewModeEvent = true;
            ModeFormatted.IsChecked = true;
            _suppressPreviewModeEvent = false;
        }

        UpdatePreviewPane(item);
    }

    private void PreviewMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized || _suppressPreviewModeEvent) return;
        UpdatePreviewPane(ClipList.SelectedItem as ClipItem);
    }

    private void UpdatePreviewPane(ClipItem? item)
    {
        if (item == null)
        {
            PreviewModeBar.Visibility = Visibility.Collapsed;
            RichPreviewViewer.Document = null;
            RichPreviewViewer.Visibility = Visibility.Collapsed;
            TextPreviewScroll.Visibility = Visibility.Collapsed;
            ImagePreviewPanel.Visibility = Visibility.Collapsed;
            BtnPastePlain.Visibility = Visibility.Visible;
            BtnToggleStar.Content = LocalizationService.Get("Str_BtnStar", "Star (Ctrl+S)");
            return;
        }

        BtnToggleStar.Content = item.IsStarred
            ? LocalizationService.Get("Str_BtnStarred", "Starred (Ctrl+S)")
            : LocalizationService.Get("Str_BtnStar", "Star (Ctrl+S)");
        BtnPastePlain.Visibility = item.Type == ClipType.Image ? Visibility.Collapsed : Visibility.Visible;

        if (item.Type == ClipType.Image)
        {
            PreviewModeBar.Visibility = Visibility.Collapsed;
            RichPreviewViewer.Document = null;
            RichPreviewViewer.Visibility = Visibility.Collapsed;
            TextPreviewScroll.Visibility = Visibility.Collapsed;
            ImagePreviewPanel.Visibility = Visibility.Visible;
            return;
        }

        if (item.Type == ClipType.Html || item.Type == ClipType.RichText)
        {
            PreviewModeBar.Visibility = Visibility.Visible;
            ModeFormatted.Content = item.Type == ClipType.Html
                ? LocalizationService.Get("Str_Mode_WebView", "Rendered")
                : LocalizationService.Get("Str_Mode_RichText", "Formatted");
            ImagePreviewPanel.Visibility = Visibility.Collapsed;

            if (ModeFormatted.IsChecked == true)
            {
                TextPreviewScroll.Visibility = Visibility.Collapsed;
                RichPreviewViewer.Visibility = Visibility.Visible;

                double fontSize = SliderPreviewFontSize != null && SliderPreviewFontSize.Value > 0
                    ? SliderPreviewFontSize.Value
                    : (_settings.PreviewFontSize > 0 ? _settings.PreviewFontSize : 18.0);
                string fontFamily = !string.IsNullOrWhiteSpace(_settings.FontFamilyName) ? _settings.FontFamilyName : "Microsoft YaHei UI";
                bool isDark = ThemeService.IsDarkTheme;

                RichPreviewViewer.Document = item.Type == ClipType.Html
                    ? RichContentService.BuildHtmlPreviewDocument(
                        item.GetHtmlContent() ?? item.TextContent,
                        item.DisplayUrl,
                        fontSize,
                        fontFamily,
                        OpenSafeExternalUrl,
                        isDark)
                    : RichContentService.BuildRtfPreviewDocument(
                        item.GetRtfContent() ?? item.TextContent,
                        fontSize,
                        fontFamily,
                        isDark);
            }
            else if (ModePlainText.IsChecked == true)
            {
                RichPreviewViewer.Document = null;
                RichPreviewViewer.Visibility = Visibility.Collapsed;
                TxtPreviewContent.FontFamily = RootWindow.FontFamily;
                TxtPreviewContent.Text = item.PlainTextContent;
                TextPreviewScroll.Visibility = Visibility.Visible;
            }
            else
            {
                RichPreviewViewer.Document = null;
                RichPreviewViewer.Visibility = Visibility.Collapsed;
                TxtPreviewContent.FontFamily = (FontFamily)FindResource("CodeFont");
                TxtPreviewContent.Text = item.Type == ClipType.Html
                    ? (item.GetHtmlContent() ?? item.TextContent)
                    : (item.GetRtfContent() ?? item.TextContent);
                TextPreviewScroll.Visibility = Visibility.Visible;
            }
            return;
        }

        PreviewModeBar.Visibility = Visibility.Collapsed;
        RichPreviewViewer.Document = null;
        RichPreviewViewer.Visibility = Visibility.Collapsed;
        ImagePreviewPanel.Visibility = Visibility.Collapsed;
        TxtPreviewContent.FontFamily = (FontFamily)FindResource("CodeFont");
        TxtPreviewContent.Text = item.Type == ClipType.Files && !string.IsNullOrWhiteSpace(item.Description)
            ? item.Description
            : item.PlainTextContent;
        TextPreviewScroll.Visibility = Visibility.Visible;
    }

    private void OpenSafeExternalUrl(string? url)
    {
        if (!ClipItem.IsWebUrl(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url!.Trim()) { UseShellExecute = true });
        }
        catch { }
    }

    private void BtnOpenPreviewUrl_Click(object sender, RoutedEventArgs e)
    {
        if (ClipList.SelectedItem is ClipItem item)
        {
            OpenSafeExternalUrl(item.DisplayUrl);
        }
    }

    private void BtnCopyPreviewUrl_Click(object sender, RoutedEventArgs e)
    {
        if (ClipList.SelectedItem is ClipItem item && !string.IsNullOrWhiteSpace(item.DisplayUrl))
        {
            _clipService.CopyPlainText(item.DisplayUrl);
            ShowStatusMessage(LocalizationService.Get("Str_Toast_CopiedUrl", "URL copied"));
        }
    }

    private void ClipList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep)
        {
            DependencyObject? current = dep;
            while (current != null && current != ClipList)
            {
                if (current is Button) return;
                current = VisualTreeHelper.GetParent(current);
            }
        }
        BtnPaste_Click(sender, e);
    }

    private async void BtnPaste_Click(object sender, RoutedEventArgs e) => await ExecuteSinglePasteAsync(asPlainText: false);
    private async void BtnPastePlain_Click(object sender, RoutedEventArgs e) => await ExecuteSinglePasteAsync(asPlainText: true);

    private async Task ExecuteSinglePasteAsync(bool asPlainText)
    {
        var item = ClipList.SelectedItem as ClipItem;
        if (item == null && _clips.Count > 0)
        {
            item = _clips[0];
            ClipList.SelectedItem = item;
        }

        if (item == null) return;

        if (item.Data == null || item.Data.Length == 0)
        {
            await _dbService.EnsureDataLoadedAsync(item);
        }

        await PasteItemToActiveWindowAsync(item, asPlainText, preserveInMemory: true);
    }

    private async Task PasteItemToActiveWindowAsync(ClipItem item, bool asPlainText, bool preserveInMemory)
    {
        if (!await _clipService.WriteClipToClipboardWithRetryAsync(item, asPlainText))
        {
            ShowStatusMessage(LocalizationService.Get("Str_Toast_ClipboardFail", "Failed to write clipboard"));
            return;
        }

        var myHwnd = new WindowInteropHelper(this).Handle;
        IntPtr targetHwnd = ResolveTargetPasteWindow(myHwnd);

        // Activate target window before hiding while ClipFlux still owns foreground lock
        if (targetHwnd != IntPtr.Zero)
        {
            Win32.ForceForegroundWindow(targetHwnd);
        }

        HideToBackground(preserveItem: preserveInMemory ? item : null);
        await _clipService.CompletePasteToWindowAsync(targetHwnd);
    }

    private async void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (ClipList.SelectedItem is ClipItem item)
        {
            if (item.Data == null || item.Data.Length == 0)
            {
                await _dbService.EnsureDataLoadedAsync(item);
            }
            _clipService.CopyToClipboard(item, asPlainText: false);
            ShowStatusMessage(LocalizationService.Get("Str_Toast_Copied", "Copied to clipboard"));
        }
    }

    private async void BtnToggleStar_Click(object sender, RoutedEventArgs e)
    {
        if (ClipList.SelectedItem is ClipItem item)
        {
            item.IsStarred = !item.IsStarred;
            await _dbService.ToggleStarAsync(item.Id, item.IsStarred);
            BtnToggleStar.Content = item.IsStarred
                ? LocalizationService.Get("Str_BtnStarred", "Starred (Ctrl+S)")
                : LocalizationService.Get("Str_BtnStar", "Star (Ctrl+S)");
            ShowStatusMessage(item.IsStarred
                ? LocalizationService.Get("Str_Toast_Starred", "Added to starred")
                : LocalizationService.Get("Str_Toast_Unstarred", "Removed from starred"));
            await UpdateStatsAsync();
        }
    }

    private async void BtnCardStar_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button btn && btn.Tag is ClipItem item)
        {
            item.IsStarred = !item.IsStarred;
            await _dbService.ToggleStarAsync(item.Id, item.IsStarred);
            if (ReferenceEquals(item, ClipList.SelectedItem))
            {
                BtnToggleStar.Content = item.IsStarred
                    ? LocalizationService.Get("Str_BtnStarred", "Starred (Ctrl+S)")
                    : LocalizationService.Get("Str_BtnStar", "Star (Ctrl+S)");
            }
            await UpdateStatsAsync();
        }
    }

    private void UpdateSelectionUi()
    {
        if (TxtSelectionStatus == null) return;
        int count = ClipList.SelectedItems.Count;
        int displayed = _clips.Count;
        int total = Math.Max(_totalCategoryClips, displayed);
        bool hasSearch = !string.IsNullOrWhiteSpace(TxtSearch.Text);

        if (count > 1)
        {
            TxtSelectionStatus.Text = LocalizationService.Format("Str_SelSelected", count, displayed);
        }
        else if (!hasSearch && displayed < total)
        {
            TxtSelectionStatus.Text = LocalizationService.Format("Str_SelPartial", displayed, total);
        }
        else
        {
            TxtSelectionStatus.Text = LocalizationService.Format("Str_SelTotal", displayed);
        }

        if (BtnLoadAll != null)
        {
            if (!hasSearch && displayed < total)
            {
                BtnLoadAll.Visibility = Visibility.Visible;
                BtnLoadAll.Content = LocalizationService.Format("Str_LoadAllCount", total);
            }
            else
            {
                BtnLoadAll.Visibility = Visibility.Collapsed;
            }
        }

        if (BtnBatchDelete != null)
        {
            BtnBatchDelete.IsEnabled = count > 0;
            BtnBatchDelete.Opacity = count > 0 ? 1.0 : 0.4;
        }

        if (BtnBatchStar != null)
        {
            BtnBatchStar.IsEnabled = count > 0;
            BtnBatchStar.Opacity = count > 0 ? 1.0 : 0.4;
        }

        if (BtnClearCurrentCategory != null)
        {
            BtnClearCurrentCategory.Visibility = _onlyStarred ? Visibility.Collapsed : Visibility.Visible;
        }

        if (BtnDelete != null)
        {
            BtnDelete.Content = count > 1
                ? LocalizationService.Format("Str_BatchDeleteCount", count)
                : LocalizationService.Get("Str_BtnDelete", "Delete");
        }
    }

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        ClipList.SelectAll();
        UpdateSelectionUi();
    }

    private void BtnInvertSelect_Click(object sender, RoutedEventArgs e)
    {
        if (_clips.Count == 0) return;
        var selectedSet = new HashSet<ClipItem>(ClipList.SelectedItems.Cast<ClipItem>());
        ClipList.SelectedItems.Clear();
        foreach (var item in _clips)
        {
            if (!selectedSet.Contains(item))
            {
                ClipList.SelectedItems.Add(item);
            }
        }
        UpdateSelectionUi();
    }

    private async void BtnBatchStar_Click(object sender, RoutedEventArgs e)
    {
        var items = ClipList.SelectedItems.Cast<ClipItem>().ToList();
        if (items.Count == 0) return;

        bool targetStar = !items.All(x => x.IsStarred);
        foreach (var item in items)
        {
            item.IsStarred = targetStar;
            await _dbService.ToggleStarAsync(item.Id, targetStar);
        }

        if (ClipList.SelectedItem is ClipItem sel)
        {
            BtnToggleStar.Content = sel.IsStarred
                ? LocalizationService.Get("Str_BtnStarred", "Starred (Ctrl+S)")
                : LocalizationService.Get("Str_BtnStar", "Star (Ctrl+S)");
        }
        ShowStatusMessage(targetStar
            ? LocalizationService.Format("Str_Toast_BatchStarred", items.Count)
            : LocalizationService.Format("Str_Toast_BatchUnstarred", items.Count));
        await UpdateStatsAsync();
    }

    private void BtnBatchDelete_Click(object sender, RoutedEventArgs e) => BtnDelete_Click(sender, e);

    private async void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        var items = ClipList.SelectedItems.Cast<ClipItem>().ToList();
        if (items.Count == 0 && ClipList.SelectedItem is ClipItem single)
        {
            items.Add(single);
        }

        if (items.Count == 0) return;

        if (items.Count > 1)
        {
            var confirm = MessageBox.Show(
                LocalizationService.Format("Str_Dlg_BatchDelMsg", items.Count),
                LocalizationService.Get("Str_Dlg_BatchDelTitle", "Confirm Delete"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
        }

        int lastIdx = ClipList.SelectedIndex;
        var ids = items.Select(x => x.Id).ToList();
        await _dbService.DeleteClipsAsync(ids);

        var idSet = new HashSet<long>(ids);
        _trayRecentCache.RemoveAll(c => idSet.Contains(c.Id));

        foreach (var item in items)
        {
            item.ClearDataAndCache();
            _clips.Remove(item);
        }

        _totalCategoryClips = Math.Max(0, _totalCategoryClips - items.Count);

        if (_clips.Count > 0)
        {
            ClipList.SelectedIndex = Math.Clamp(lastIdx, 0, _clips.Count - 1);
        }
        else
        {
            UpdatePreviewPane(null);
        }

        EmptyPlaceholder.Visibility = _clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowStatusMessage(LocalizationService.Format("Str_Toast_Deleted", items.Count));
        await UpdateStatsAsync();
        UpdateSelectionUi();
        await RefreshTrayCacheAsync();
    }

    private async void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        int unstarredCount = await _dbService.GetUnstarredCountAsync();
        if (unstarredCount <= 0)
        {
            MessageBox.Show(
                LocalizationService.Get("Str_Dlg_NoUnstarred", "There are no unstarred items to clear."),
                LocalizationService.Get("Str_Dlg_Info", "Notice"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Step 1 confirmation prompt
        var step1 = MessageBox.Show(
            LocalizationService.Format("Str_Dlg_ClearStep1Msg", unstarredCount),
            LocalizationService.Get("Str_Dlg_ClearStep1Title", "Clear History (Step 1 of 2)"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (step1 != MessageBoxResult.Yes) return;

        // Step 2 high-risk confirmation overlay requiring explicit acknowledgment checkbox
        TxtClearConfirmDetail.Text = LocalizationService.Format("Str_ClearModal_Detail", unstarredCount);
        ChkClearConfirmAck.IsChecked = false;
        BtnConfirmFinalClear.IsEnabled = false;
        ClearConfirmOverlay.Visibility = Visibility.Visible;
    }

    private void ChkClearConfirmAck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        BtnConfirmFinalClear.IsEnabled = ChkClearConfirmAck.IsChecked == true;
    }

    private void BtnCancelFinalClear_Click(object sender, RoutedEventArgs e)
    {
        ClearConfirmOverlay.Visibility = Visibility.Collapsed;
    }

    private async void BtnConfirmFinalClear_Click(object sender, RoutedEventArgs e)
    {
        if (ChkClearConfirmAck.IsChecked != true) return;

        ClearConfirmOverlay.Visibility = Visibility.Collapsed;
        await _dbService.ClearHistoryAsync(keepStarred: true);
        await RefreshListAsync();
        await RefreshTrayCacheAsync();
        ShowStatusMessage(LocalizationService.Get("Str_Toast_ClearedHistory", "Unstarred history cleared"));
    }

    private async void BtnLoadAll_Click(object sender, RoutedEventArgs e)
    {
        _currentDisplayLimit = 50000;
        await RefreshListAsync();
    }

    private async void BtnClearCurrentCategory_Click(object sender, RoutedEventArgs e)
    {
        if (_onlyStarred) return;

        string catName = _currentTypeFilter switch
        {
            (int)ClipType.Text => LocalizationService.Get("Str_Filter_Text", "Text"),
            (int)ClipType.RichText => LocalizationService.Get("Str_Filter_RichText", "Rich Text"),
            (int)ClipType.Html => LocalizationService.Get("Str_Filter_Html", "HTML"),
            (int)ClipType.Image => LocalizationService.Get("Str_Filter_Image", "Images"),
            (int)ClipType.Files => LocalizationService.Get("Str_Filter_Files", "Files"),
            _ => LocalizationService.Get("Str_Filter_All", "All")
        };

        var result = MessageBox.Show(
            LocalizationService.Format("Str_Dlg_ClearCatMsg", catName),
            LocalizationService.Format("Str_Dlg_ClearCatTitle", catName),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            int removed = await _dbService.ClearCategoryHistoryAsync(_currentTypeFilter, keepStarred: true);
            await RefreshListAsync();
            await RefreshTrayCacheAsync();
            ShowStatusMessage(LocalizationService.Format("Str_Toast_ClearedCategory", removed));
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => HideToBackground();

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => HideToBackground();

    private async void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = LocalizationService.Get("Str_Import_DialogTitle", "Select History Database File"),
            Filter = LocalizationService.Get("Str_Import_Filter", "SQLite Database (*.db;*.sqlite)|*.db;*.sqlite|All Files (*.*)|*.*"),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        };

        if (ofd.ShowDialog() != true) return;

        string selectedPath = ofd.FileName;
        if (string.Equals(Path.GetFullPath(selectedPath), Path.GetFullPath(_dbService.DatabasePath), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                LocalizationService.Get("Str_Import_SameDb", "Cannot import the active history database."),
                LocalizationService.Get("Str_Dlg_Info", "Notice"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var fileInfo = new FileInfo(selectedPath);
        string sizeText = fileInfo.Length >= 1024 * 1024
            ? $"{fileInfo.Length / (1024.0 * 1024.0):F1} MB"
            : $"{Math.Max(1, fileInfo.Length / 1024.0):F0} KB";

        var prompt = MessageBox.Show(
            LocalizationService.Format("Str_Import_Confirm", selectedPath, sizeText),
            LocalizationService.Get("Str_Data_ImportTitle", "Import History Database"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (prompt == MessageBoxResult.Yes)
        {
            BtnImport.IsEnabled = false;
            BtnImport.Content = LocalizationService.Get("Str_Import_InProgress", "Importing...");

            try
            {
                int imported = await _dbService.ImportExternalDatabaseAsync(selectedPath, (cur, _) =>
                {
                    Dispatcher.Invoke(() => BtnImport.Content = LocalizationService.Format("Str_Import_Progress", cur));
                });

                MessageBox.Show(
                    LocalizationService.Format("Str_Import_Done", imported),
                    LocalizationService.Get("Str_Import_DoneTitle", "Import Complete"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                await RefreshListAsync();
                await RefreshTrayCacheAsync();
                _ = Task.Run(() => _dbService.BackfillRecentHtmlUrlsAsync(500));
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    LocalizationService.Format("Str_Import_Fail", ex.Message),
                    LocalizationService.Get("Str_Dlg_Error", "Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                BtnImport.IsEnabled = true;
                BtnImport.Content = LocalizationService.Get("Str_Data_ImportBtn", "Import Data");
            }
        }
    }

    #region Backup UI Handlers

    private void PopulateCloudDriveCombo()
    {
        _suppressCloudSelectionEvent = true;
        try
        {
            CmbCloudDrives.Items.Clear();
            _detectedCloudDrives = BackupService.DetectInstalledCloudDrives();

            foreach (var drive in _detectedCloudDrives)
            {
                CmbCloudDrives.Items.Add(new ComboBoxItem
                {
                    Content = drive.DisplayName,
                    Tag = drive.RootPath
                });
            }

            if (!string.IsNullOrWhiteSpace(_selectedCloudFolder) &&
                !_detectedCloudDrives.Any(d => string.Equals(d.RootPath, _selectedCloudFolder, StringComparison.OrdinalIgnoreCase)))
            {
                CmbCloudDrives.Items.Add(new ComboBoxItem
                {
                    Content = $"{LocalizationService.Get("Str_Bak_CloudCustom", "Custom Sync Folder")} ({_selectedCloudFolder})",
                    Tag = _selectedCloudFolder
                });
            }

            CmbCloudDrives.Items.Add(new ComboBoxItem
            {
                Content = LocalizationService.Get("Str_Bak_CloudCustom", "Custom Sync Folder") + "...",
                Tag = CustomCloudTag
            });

            if (!string.IsNullOrWhiteSpace(_selectedCloudFolder))
            {
                SelectComboItemByTag(CmbCloudDrives, _selectedCloudFolder, 0);
            }
            else if (_detectedCloudDrives.Count > 0)
            {
                CmbCloudDrives.SelectedIndex = 0;
                _selectedCloudFolder = _detectedCloudDrives[0].RootPath;
            }
            else if (CmbCloudDrives.Items.Count > 0)
            {
                CmbCloudDrives.SelectedIndex = 0;
            }

            UpdateCloudBackupTargetLabel();
        }
        finally
        {
            _suppressCloudSelectionEvent = false;
        }
    }

    private void UpdateCloudBackupTargetLabel()
    {
        if (TxtCloudBackupTarget == null) return;

        if (!string.IsNullOrWhiteSpace(_selectedCloudFolder) && Directory.Exists(_selectedCloudFolder))
        {
            string targetSubfolder = Path.Combine(_selectedCloudFolder, "ClipFluxBackups");
            TxtCloudBackupTarget.Text = LocalizationService.Format("Str_Bak_CloudTarget", targetSubfolder);
        }
        else
        {
            TxtCloudBackupTarget.Text = LocalizationService.Get("Str_Bak_CloudNone", "No cloud sync folder selected");
        }
    }

    private void BtnBrowseLocalBackup_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = LocalizationService.Get("Str_Bak_FolderDialog", "Select Local Backup Folder"),
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(TxtLocalBackupDir.Text)
                ? TxtLocalBackupDir.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            TxtLocalBackupDir.Text = dialog.SelectedPath;
        }
    }

    private void CmbCloudDrives_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || _suppressCloudSelectionEvent) return;
        if (CmbCloudDrives.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            if (tag == CustomCloudTag)
            {
                BtnBrowseCloudFolder_Click(sender, e);
            }
            else
            {
                _selectedCloudFolder = tag;
                UpdateCloudBackupTargetLabel();
            }
        }
    }

    private void BtnBrowseCloudFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = LocalizationService.Get("Str_Bak_CloudFolderDialog", "Select Cloud Sync Folder"),
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(_selectedCloudFolder)
                ? _selectedCloudFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            _selectedCloudFolder = dialog.SelectedPath;
            PopulateCloudDriveCombo();
        }
    }

    private void ChkEnableScheduledBackup_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        TogglePanelState(PanelScheduleOptions, ChkEnableScheduledBackup.IsChecked == true);
    }

    private void ChkEnableCloudBackup_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        TogglePanelState(PanelCloudOptions, ChkEnableCloudBackup.IsChecked == true);
    }

    private void ChkEnableWebDavBackup_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        TogglePanelState(PanelWebDavOptions, ChkEnableWebDavBackup.IsChecked == true);
    }

    private void BtnWebDavPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            TxtWebDavUrl.Text = tag switch
            {
                "Nutstore" => "https://dav.jianguoyun.com/dav/ClipFlux/",
                "Nextcloud" => "https://your-server/remote.php/dav/files/username/ClipFlux/",
                "Alist" => "http://127.0.0.1:5244/dav/ClipFlux/",
                _ => TxtWebDavUrl.Text
            };
        }
    }

    private async void BtnTestWebDav_Click(object sender, RoutedEventArgs e)
    {
        BtnTestWebDav.IsEnabled = false;
        TxtWebDavTestStatus.Foreground = (Brush)FindResource("BrushTextSecondary");
        TxtWebDavTestStatus.Text = LocalizationService.Get("Str_Bak_Testing", "Testing...");

        try
        {
            var (ok, message) = await BackupService.TestWebDavConnectionAsync(
                TxtWebDavUrl.Text,
                TxtWebDavUser.Text,
                PwdWebDav.Password);

            TxtWebDavTestStatus.Text = message;
            TxtWebDavTestStatus.Foreground = (Brush)FindResource(ok ? "BrushAccent" : "BrushDanger");
        }
        finally
        {
            BtnTestWebDav.IsEnabled = true;
        }
    }

    private async void BtnBackupNow_Click(object sender, RoutedEventArgs e)
    {
        SyncBackupUiToSettings();
        _settings.Save();
        await RunBackupFlowAsync(showFeedback: true);
    }

    private async void BtnBackupSaveAs_Click(object sender, RoutedEventArgs e)
    {
        var sfd = new SaveFileDialog
        {
            Title = LocalizationService.Get("Str_Bak_SaveAsTitle", "Save Clipboard History Backup"),
            Filter = LocalizationService.Get("Str_Import_Filter", "SQLite Database (*.db;*.sqlite)|*.db;*.sqlite|All Files (*.*)|*.*"),
            FileName = $"ClipFlux_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.db"
        };

        if (sfd.ShowDialog() == true)
        {
            BtnBackupSaveAs.IsEnabled = false;
            try
            {
                var result = await BackupService.RunBackupAsync(_dbService, _settings, includeLocal: false, customSaveAsPath: sfd.FileName);
                if (result.Success)
                {
                    _settings.LastBackupTime = result.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
                    _settings.Save();
                    UpdateBackupStatusLabel();
                    string msg = LocalizationService.Format("Str_Bak_Success", sfd.SafeFileName);
                    ShowStatusMessage(msg);
                    MessageBox.Show(msg, LocalizationService.Get("Str_Dlg_Info", "Notice"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, LocalizationService.Get("Str_Dlg_Error", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnBackupSaveAs.IsEnabled = true;
            }
        }
    }

    private async Task RunBackupFlowAsync(bool showFeedback)
    {
        if (BtnBackupNow != null)
        {
            BtnBackupNow.IsEnabled = false;
            BtnBackupNow.Content = LocalizationService.Get("Str_Bak_Running", "Backing up...");
        }

        try
        {
            var result = await BackupService.RunBackupAsync(_dbService, _settings, includeLocal: true);
            if (result.CompletedTargets.Count > 0)
            {
                _settings.LastBackupTime = result.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
                _settings.Save();
                UpdateBackupStatusLabel();
            }

            if (result.Errors.Count > 0)
            {
                string errText = LocalizationService.Format("Str_Bak_PartialFail", string.Join("\r\n", result.Errors));
                if (showFeedback)
                {
                    MessageBox.Show(errText, LocalizationService.Get("Str_Dlg_Error", "Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else if (showFeedback)
            {
                string okText = LocalizationService.Format("Str_Bak_Success", string.Join(" + ", result.CompletedTargets));
                ShowStatusMessage(okText);
                if (SettingsOverlay.Visibility == Visibility.Visible)
                {
                    MessageBox.Show(okText, LocalizationService.Get("Str_Dlg_Info", "Notice"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
        catch (Exception ex)
        {
            if (showFeedback)
            {
                MessageBox.Show(ex.Message, LocalizationService.Get("Str_Dlg_Error", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            if (BtnBackupNow != null)
            {
                BtnBackupNow.IsEnabled = true;
                BtnBackupNow.Content = LocalizationService.Get("Str_Bak_Now", "Backup Now");
            }
        }
    }

    private void SyncBackupUiToSettings()
    {
        if (!string.IsNullOrWhiteSpace(TxtLocalBackupDir.Text))
        {
            _settings.LocalBackupDir = TxtLocalBackupDir.Text.Trim();
        }
        _settings.EnableScheduledBackup = ChkEnableScheduledBackup.IsChecked == true;
        if (CmbBackupInterval.SelectedItem is ComboBoxItem intItem && int.TryParse(intItem.Tag?.ToString(), out int hours))
        {
            _settings.BackupIntervalHours = hours;
        }
        if (CmbBackupRetention.SelectedItem is ComboBoxItem retItem && int.TryParse(retItem.Tag?.ToString(), out int ret))
        {
            _settings.MaxBackupRetention = ret;
        }

        _settings.EnableCloudDriveBackup = ChkEnableCloudBackup.IsChecked == true;
        _settings.CloudDriveFolder = _selectedCloudFolder;

        _settings.EnableWebDavBackup = ChkEnableWebDavBackup.IsChecked == true;
        _settings.WebDavUrl = TxtWebDavUrl.Text.Trim();
        _settings.WebDavUsername = TxtWebDavUser.Text.Trim();
        _settings.WebDavPasswordProtected = string.IsNullOrEmpty(PwdWebDav.Password)
            ? string.Empty
            : Win32.ProtectString(PwdWebDav.Password);
    }

    #endregion

    private void ClipCard_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ClipItem item)
        {
            if (!ClipList.SelectedItems.Contains(item))
            {
                ClipList.SelectedItem = item;
            }
        }
    }

    private void MenuPaste_Click(object sender, RoutedEventArgs e) => BtnPaste_Click(sender, e);
    private void MenuPastePlain_Click(object sender, RoutedEventArgs e) => BtnPastePlain_Click(sender, e);
    private void MenuCopy_Click(object sender, RoutedEventArgs e) => BtnCopy_Click(sender, e);
    private void MenuToggleStar_Click(object sender, RoutedEventArgs e) => BtnToggleStar_Click(sender, e);

    private async void MenuCopyPlain_Click(object sender, RoutedEventArgs e)
    {
        if (ClipList.SelectedItem is ClipItem item)
        {
            if (item.Data == null || item.Data.Length == 0)
            {
                await _dbService.EnsureDataLoadedAsync(item);
            }
            _clipService.CopyToClipboard(item, asPlainText: true);
            ShowStatusMessage(LocalizationService.Get("Str_Toast_CopiedPlain", "Copied plain text"));
        }
    }

    private async void MenuOpenInNotepad_Click(object sender, RoutedEventArgs e)
    {
        if (ClipList.SelectedItem is not ClipItem item) return;
        try
        {
            if (item.Data == null || item.Data.Length == 0)
            {
                await _dbService.EnsureDataLoadedAsync(item);
            }

            string tempFile = Path.Combine(Path.GetTempPath(), $"clip_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(tempFile, item.PlainTextContent, Encoding.UTF8);
            Process.Start(new ProcessStartInfo("notepad.exe", tempFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, LocalizationService.Get("Str_Dlg_Error", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void MenuOpenUrlOrSearch_Click(object sender, RoutedEventArgs e)
    {
        if (ClipList.SelectedItem is not ClipItem item) return;
        try
        {
            if (item.Data == null || item.Data.Length == 0)
            {
                await _dbService.EnsureDataLoadedAsync(item);
            }

            string url = item.DisplayUrl;
            if (ClipItem.IsWebUrl(url))
            {
                OpenSafeExternalUrl(url);
                return;
            }

            string text = item.PlainTextContent.Trim();
            if (ClipItem.IsWebUrl(text))
            {
                OpenSafeExternalUrl(text);
            }
            else if (!string.IsNullOrWhiteSpace(text))
            {
                string searchUrl = "https://www.bing.com/search?q=" + Uri.EscapeDataString(text.Length > 200 ? text[..200] : text);
                Process.Start(new ProcessStartInfo(searchUrl) { UseShellExecute = true });
            }
        }
        catch { }
    }

    private async void MenuSaveToFile_Click(object sender, RoutedEventArgs e)
    {
        if (ClipList.SelectedItem is not ClipItem item) return;

        if (item.Data == null || item.Data.Length == 0)
        {
            await _dbService.EnsureDataLoadedAsync(item);
        }

        var sfd = new SaveFileDialog();

        if (item.Type == ClipType.Image)
        {
            sfd.Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg|BMP (*.bmp)|*.bmp";
            sfd.FileName = $"Clip_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            if (sfd.ShowDialog() == true)
            {
                try
                {
                    var fullBmp = item.GetFullResolutionBitmap() ?? item.BitmapImage;
                    if (fullBmp != null)
                    {
                        using var fs = new FileStream(sfd.FileName, FileMode.Create);
                        BitmapEncoder encoder = sfd.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                            ? new JpegBitmapEncoder()
                            : sfd.FileName.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)
                                ? new BmpBitmapEncoder()
                                : new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(fullBmp));
                        encoder.Save(fs);
                        ShowStatusMessage(LocalizationService.Get("Str_Toast_ImageSaved", "Image saved"));
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, LocalizationService.Get("Str_Dlg_Error", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            return;
        }

        sfd.Filter = item.Type switch
        {
            ClipType.Html => "HTML (*.html)|*.html|Text (*.txt)|*.txt|All (*.*)|*.*",
            ClipType.RichText => "Rich Text (*.rtf)|*.rtf|Text (*.txt)|*.txt|All (*.*)|*.*",
            _ => "Text (*.txt)|*.txt|All (*.*)|*.*"
        };
        string defaultExt = item.Type switch
        {
            ClipType.Html => ".html",
            ClipType.RichText => ".rtf",
            _ => ".txt"
        };
        sfd.FileName = $"Clip_{DateTime.Now:yyyyMMdd_HHmmss}{defaultExt}";

        if (sfd.ShowDialog() == true)
        {
            try
            {
                string content = Path.GetExtension(sfd.FileName).ToLowerInvariant() switch
                {
                    ".html" or ".htm" => item.GetHtmlContent() ?? item.TextContent,
                    ".rtf" => item.GetRtfContent() ?? item.TextContent,
                    _ => item.PlainTextContent
                };
                File.WriteAllText(sfd.FileName, content, Encoding.UTF8);
                ShowStatusMessage(LocalizationService.Get("Str_Toast_FileSaved", "File saved"));
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, LocalizationService.Get("Str_Dlg_Error", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void MenuMultiplePaste_Click(object sender, RoutedEventArgs e)
    {
        MultiPasteOverlay.Visibility = Visibility.Visible;
        await UpdateMultiPastePreviewAsync();
    }

    private async void MultiPasteOption_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized) return;
        await UpdateMultiPastePreviewAsync();
    }

    private async Task UpdateMultiPastePreviewAsync()
    {
        if (TxtMultiPastePreview == null) return;

        string sep = "\r\n";
        if (CmbSeparator.SelectedItem is ComboBoxItem sepItem && sepItem.Tag != null)
        {
            sep = sepItem.Tag.ToString()!;
        }

        bool isDesc = true;
        if (CmbOrder.SelectedItem is ComboBoxItem ordItem && ordItem.Tag?.ToString() == "asc")
        {
            isDesc = false;
        }

        List<ClipItem> sourceList = ClipList.SelectedItems.Count > 1
            ? ClipList.SelectedItems.Cast<ClipItem>().ToList()
            : _clips.Take(10).ToList();

        if (!isDesc)
        {
            sourceList.Reverse();
        }

        foreach (var item in sourceList)
        {
            if (item.Data == null || item.Data.Length == 0)
            {
                await _dbService.EnsureDataLoadedAsync(item);
            }
        }

        var sb = new StringBuilder();
        for (int i = 0; i < sourceList.Count; i++)
        {
            if (i > 0) sb.Append(sep);
            sb.Append(sourceList[i].PlainTextContent);
        }

        TxtMultiPastePreview.Text = sb.ToString();
    }

    private async void BtnExecuteMultiPaste_Click(object sender, RoutedEventArgs e)
    {
        string text = TxtMultiPastePreview.Text;
        if (string.IsNullOrEmpty(text)) return;

        MultiPasteOverlay.Visibility = Visibility.Collapsed;
        var tempItem = new ClipItem
        {
            Title = text.Length > 80 ? text[..80] : text,
            Data = Encoding.UTF8.GetBytes(text),
            Type = ClipType.Text
        };
        await PasteItemToActiveWindowAsync(tempItem, asPlainText: true, preserveInMemory: false);
    }

    private void BtnCopyMultiPaste_Click(object sender, RoutedEventArgs e)
    {
        string text = TxtMultiPastePreview.Text;
        if (!string.IsNullOrEmpty(text))
        {
            _clipService.CopyPlainText(text);
            MultiPasteOverlay.Visibility = Visibility.Collapsed;
            ShowStatusMessage(LocalizationService.Get("Str_Toast_MultiCopied", "Merged text copied"));
        }
    }

    private void BtnCloseMultiPaste_Click(object sender, RoutedEventArgs e)
    {
        MultiPasteOverlay.Visibility = Visibility.Collapsed;
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        _ = UpdateStatsAsync();
        PopulateCloudDriveCombo();
        UpdateBackupStatusLabel();
        TxtWebDavTestStatus.Text = string.Empty;
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void BtnCloseSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void SettingsTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized || PanelSettingsGeneral == null) return;
        PanelSettingsGeneral.Visibility = TabSettingsGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelSettingsAppearance.Visibility = TabSettingsAppearance.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelSettingsBackup.Visibility = TabSettingsBackup.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LangOption_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized || _suppressLanguageEvent) return;
        if (sender is RadioButton rb && rb.Tag is string mode)
        {
            ApplyCurrentLanguage(mode);
        }
    }

    private void CmbFontFamily_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized) return;
        if (CmbFontFamily.SelectedItem is ComboBoxItem item && item.Tag != null)
        {
            string fontName = item.Tag.ToString()!;
            RootWindow.FontFamily = new FontFamily(fontName);
        }
    }

    private void SliderNavFontSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized) return;
        TxtNavFontSizeVal.Text = $"{e.NewValue:F1} px";
        if (NavFilterScroll != null) NavFilterScroll.FontSize = e.NewValue;
        if (TxtSearch != null) TxtSearch.FontSize = e.NewValue + 0.5;
    }

    private void SliderListFontSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized) return;
        TxtListFontSizeVal.Text = $"{e.NewValue:F1} px";
        if (ClipList != null) ClipList.FontSize = e.NewValue;
    }

    private void ThemeOption_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized || _suppressThemeEvent) return;
        if (sender is RadioButton rb && rb.Tag is string mode)
        {
            ApplyCurrentTheme(mode);
        }
    }

    private void SliderPreviewFontSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized) return;
        TxtPreviewFontSizeVal.Text = $"{e.NewValue:F1} px";
        if (TxtPreviewContent != null) TxtPreviewContent.FontSize = e.NewValue;
        if (RichPreviewViewer != null)
        {
            RichPreviewViewer.FontSize = e.NewValue;
            if (RichPreviewViewer.Document != null)
            {
                RichPreviewViewer.Document.FontSize = e.NewValue;
            }
            if (RichPreviewViewer.Visibility == Visibility.Visible && ClipList?.SelectedItem is ClipItem current)
            {
                ScheduleRichPreviewRefresh(current);
            }
        }
    }

    private async void ScheduleRichPreviewRefresh(ClipItem target)
    {
        _previewFontDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewFontDebounceCts = cts;
        try
        {
            await Task.Delay(90, cts.Token);
            if (!cts.Token.IsCancellationRequested &&
                ReferenceEquals(ClipList?.SelectedItem, target) &&
                RichPreviewViewer.Visibility == Visibility.Visible)
            {
                UpdatePreviewPane(target);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void SliderGlobalScale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized) return;
        TxtGlobalScaleVal.Text = $"{e.NewValue:P0}";
        if (GlobalScaleTransform != null)
        {
            GlobalScaleTransform.ScaleX = e.NewValue;
            GlobalScaleTransform.ScaleY = e.NewValue;
        }
    }

    private void SliderTrayRecentCount_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized || TxtTrayRecentCountVal == null) return;
        TxtTrayRecentCountVal.Text = LocalizationService.Format("Str_Unit_Items", (int)e.NewValue);
    }

    private void SliderMaxDisplayCount_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized || TxtMaxDisplayCountVal == null) return;
        int val = (int)Math.Round(e.NewValue);
        TxtMaxDisplayCountVal.Text = LocalizationService.Format("Str_Unit_Items", val);
    }

    private void ChkEnableHotkey_CheckChanged(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        bool enabled = ChkEnableHotkey.IsChecked == true;
        PanelHotkeyChoice.IsEnabled = enabled;
        PanelHotkeyChoice.Opacity = enabled ? 1.0 : 0.45;
    }

    private void TxtCustomHotkey_GotFocus(object sender, RoutedEventArgs e)
    {
        TxtCustomHotkey.BorderBrush = (Brush)FindResource("BrushAccent");
        TxtHotkeyStatusHint.Text = LocalizationService.Get("Str_Set_HotkeyRecording", "Press hotkey...");
        TxtHotkeyStatusHint.Foreground = (Brush)FindResource("BrushAccent");
    }

    private void TxtCustomHotkey_LostFocus(object sender, RoutedEventArgs e)
    {
        TxtCustomHotkey.BorderBrush = (Brush)FindResource("BrushBorderSubtle");
        TxtHotkeyStatusHint.Text = LocalizationService.Get("Str_Set_HotkeyHint", "Click box and press hotkey");
        TxtHotkeyStatusHint.Foreground = (Brush)FindResource("BrushTextMuted");
    }

    private void TxtCustomHotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LWin || key == Key.RWin)
        {
            var mods = Keyboard.Modifiers;
            var modParts = new List<string>();
            if ((mods & ModifierKeys.Control) != 0) modParts.Add("Ctrl");
            if ((mods & ModifierKeys.Alt) != 0) modParts.Add("Alt");
            if ((mods & ModifierKeys.Shift) != 0) modParts.Add("Shift");
            if ((mods & ModifierKeys.Windows) != 0) modParts.Add("Win");
            if (modParts.Count > 0)
            {
                TxtCustomHotkey.Text = string.Join(" + ", modParts) + " + ...";
            }
            return;
        }

        if (key == Key.Escape)
        {
            TxtCustomHotkey.Text = _settings.HotkeyDisplay;
            _editingModifiers = _settings.HotkeyModifiers;
            _editingKey = _settings.HotkeyKey;
            _editingDisplay = _settings.HotkeyDisplay;
            Keyboard.ClearFocus();
            return;
        }

        uint modifiers = 0;
        var currentMods = Keyboard.Modifiers;
        if ((currentMods & ModifierKeys.Control) != 0) modifiers |= Win32.MOD_CONTROL;
        if ((currentMods & ModifierKeys.Alt) != 0) modifiers |= Win32.MOD_ALT;
        if ((currentMods & ModifierKeys.Shift) != 0) modifiers |= Win32.MOD_SHIFT;
        if ((currentMods & ModifierKeys.Windows) != 0) modifiers |= Win32.MOD_WIN;

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0) return;

        string keyName = FormatKeyName(key);
        var parts = new List<string>();
        if ((modifiers & Win32.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & Win32.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & Win32.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & Win32.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(keyName);

        string display = string.Join(" + ", parts);
        _editingModifiers = modifiers;
        _editingKey = (uint)vk;
        _editingDisplay = display;

        TxtCustomHotkey.Text = display;
        TxtHotkeyStatusHint.Text = LocalizationService.Format("Str_Set_HotkeyRecorded", display);
        TxtHotkeyStatusHint.Foreground = (Brush)FindResource("BrushTextSecondary");
    }

    private static string FormatKeyName(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return key.ToString();
        if (key >= Key.D0 && key <= Key.D9) return key.ToString()[1..];
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return "Num " + key.ToString()[6..];
        if (key >= Key.F1 && key <= Key.F24) return key.ToString();

        return key switch
        {
            Key.Space => "Space",
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Tab => "Tab",
            Key.OemTilde => "` (~)",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            _ => key.ToString()
        };
    }

    private void BtnPresetHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            switch (tag)
            {
                case "Alt+V": SetHotkeyConfig(Win32.MOD_ALT, 0x56, "Alt + V"); break;
                case "Ctrl+D": SetHotkeyConfig(Win32.MOD_CONTROL, 0x44, "Ctrl + D"); break;
                case "Alt+Space": SetHotkeyConfig(Win32.MOD_ALT, 0x20, "Alt + Space"); break;
                case "Ctrl+Shift+V": SetHotkeyConfig(Win32.MOD_CONTROL | Win32.MOD_SHIFT, 0x56, "Ctrl + Shift + V"); break;
                case "F9": SetHotkeyConfig(0, 0x78, "F9"); break;
            }
        }
    }

    private void SetHotkeyConfig(uint modifiers, uint vk, string display)
    {
        _editingModifiers = modifiers;
        _editingKey = vk;
        _editingDisplay = display;
        TxtCustomHotkey.Text = display;
        TxtHotkeyStatusHint.Text = LocalizationService.Format("Str_Set_HotkeyPresetChosen", display);
        TxtHotkeyStatusHint.Foreground = (Brush)FindResource("BrushTextSecondary");
    }

    private void BtnClearHotkey_Click(object sender, RoutedEventArgs e) => SetHotkeyConfig(Win32.MOD_ALT, 0x56, "Alt + V");

    private void BtnResetFont_Click(object sender, RoutedEventArgs e)
    {
        SliderNavFontSize.Value = 14.5;
        SliderListFontSize.Value = 15.5;
        SliderPreviewFontSize.Value = 18.0;
        SliderGlobalScale.Value = 1.0;
        SliderTrayRecentCount.Value = 5;
        SliderMaxDisplayCount.Value = 1000;
        ChkEnableHotkey.IsChecked = false;
        PanelHotkeyChoice.IsEnabled = false;
        PanelHotkeyChoice.Opacity = 0.45;
        SetHotkeyConfig(Win32.MOD_ALT, 0x56, "Alt + V");
        RootWindow.FontFamily = (FontFamily)FindResource("ModernFont");
        ThemeSystem.IsChecked = true;
        LangSystem.IsChecked = true;

        if (CmbFontFamily.Items.Count > 0)
        {
            CmbFontFamily.SelectedIndex = 0;
        }
    }

    private async void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings.AutoStart = ChkAutoStart.IsChecked == true;
            _settings.EnableGlobalHotkey = ChkEnableHotkey.IsChecked == true;
            if (_editingKey != 0)
            {
                _settings.HotkeyModifiers = _editingModifiers;
                _settings.HotkeyKey = _editingKey;
                _settings.HotkeyDisplay = _editingDisplay;
            }

            _settings.LanguageMode = LangZhCn.IsChecked == true
                ? LocalizationService.ModeZhCn
                : LangEnUs.IsChecked == true
                    ? LocalizationService.ModeEnUs
                    : LocalizationService.ModeSystem;

            _settings.ThemeMode = ThemeLight.IsChecked == true
                ? ThemeService.ModeLight
                : ThemeDark.IsChecked == true
                    ? ThemeService.ModeDark
                    : ThemeService.ModeSystem;

            if (CmbFontFamily.SelectedItem is ComboBoxItem fontItem && fontItem.Tag != null)
            {
                _settings.FontFamilyName = fontItem.Tag.ToString()!;
            }
            _settings.NavFontSize = SliderNavFontSize.Value;
            _settings.ListFontSize = SliderListFontSize.Value;
            _settings.PreviewFontSize = SliderPreviewFontSize.Value;
            _settings.GlobalFontScale = SliderGlobalScale.Value;
            _settings.TrayRecentCount = (int)SliderTrayRecentCount.Value;
            _settings.MaxDisplayCount = (int)Math.Round(SliderMaxDisplayCount.Value);
            _currentDisplayLimit = _settings.MaxDisplayCount;

            SyncBackupUiToSettings();
            _settings.Save();

            ApplyCurrentLanguage(_settings.LanguageMode);
            ApplyCurrentTheme(_settings.ThemeMode);
            RegisterConfiguredHotkeys();

            SettingsOverlay.Visibility = Visibility.Collapsed;
            ShowStatusMessage(LocalizationService.Get("Str_Toast_SettingsSaved", "Settings saved"));

            if (ClipList.SelectedItem is ClipItem current && (current.Type == ClipType.Html || current.Type == ClipType.RichText))
            {
                UpdatePreviewPane(current);
            }
            await RefreshTrayCacheAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, LocalizationService.Get("Str_Dlg_Error", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (ClearConfirmOverlay.Visibility == Visibility.Visible)
            {
                ClearConfirmOverlay.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }
            if (SettingsOverlay.Visibility == Visibility.Visible)
            {
                SettingsOverlay.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }
            if (MultiPasteOverlay.Visibility == Visibility.Visible)
            {
                MultiPasteOverlay.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }

            HideToBackground();
            e.Handled = true;
            return;
        }

        if (ClearConfirmOverlay.Visibility == Visibility.Visible ||
            SettingsOverlay.Visibility == Visibility.Visible ||
            MultiPasteOverlay.Visibility == Visibility.Visible)
        {
            return;
        }

        bool isEnter = e.Key == Key.Return || (e.Key == Key.ImeProcessed && e.ImeProcessedKey == Key.Return);
        if (isEnter && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            e.Handled = true;
            BtnPastePlain_Click(sender, e);
            return;
        }

        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            bool hasFocusedSelection =
                (TxtSearch.IsFocused && TxtSearch.SelectionLength > 0) ||
                (TxtPreviewContent.IsKeyboardFocused && TxtPreviewContent.SelectionLength > 0) ||
                (RichPreviewViewer.IsKeyboardFocusWithin && RichPreviewViewer.Selection != null && !RichPreviewViewer.Selection.IsEmpty);
            if (!hasFocusedSelection)
            {
                BtnCopy_Click(sender, e);
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            BtnToggleStar_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (!TxtSearch.IsFocused)
            {
                ClipList.SelectAll();
                UpdateSelectionUi();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Delete)
        {
            if (TxtSearch.IsFocused && TxtSearch.SelectionLength > 0) return;
            BtnDelete_Click(sender, e);
            e.Handled = true;
        }
    }
}
