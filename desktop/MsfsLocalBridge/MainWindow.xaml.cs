using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using MsfsLocalBridge.Models;
using MsfsLocalBridge.Services;

namespace MsfsLocalBridge;

public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfoMessage = 0x0024;
    private const int WmDpiChangedMessage = 0x02E0;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint MonitorDefaultToPrimary = 0x00000001;
    private const double FixedWindowWidth = 600;
    private const double MinimumWindowWidth = 520;
    private const double MinimumWindowHeight = 420;
    private const double WindowContentChromeHeight = 42;
    private const double WorkAreaPadding = 24;
    private static readonly Thickness NormalFrameMargin = new(0);
    private static readonly Thickness MaximizedFrameMargin = new(0);
    private static readonly CornerRadius NormalFrameRadius = new(24);
    private static readonly CornerRadius MaximizedFrameRadius = new(0);

    private readonly BridgeWorkspace _workspace = new();
    private readonly PowerShellRunner _powerShellRunner = new();
    private readonly BridgeSessionService _sessionService;
    private readonly BridgeDiagnosticsService _diagnosticsService;
    private readonly PrerequisiteInstallerService _prerequisiteInstaller = new();
    private readonly AppStateBuilder _stateBuilder = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DispatcherTimer _refreshTimer;
    private readonly string _settingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MSFS Local Bridge");
    private readonly string _windowPlacementPath;
    private string _lastDiagnosticsJson = string.Empty;
    private double _lastContentHeight;
    private AppState _currentState = new();

    public MainWindow()
    {
        InitializeComponent();
        _windowPlacementPath = Path.Combine(_settingsDirectory, "window-placement.json");
        ApplyFrameState();
        _sessionService = new BridgeSessionService(_workspace, _powerShellRunner);
        _diagnosticsService = new BridgeDiagnosticsService(_workspace, _powerShellRunner);
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _refreshTimer.Tick += async (_, _) => await PublishStateAsync();
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        StateChanged += OnWindowStateChanged;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WindowProc);
        }

        ApplyInitialWindowPlacement();
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfoMessage)
        {
            ApplyWindowBounds(hwnd, lParam);
            handled = true;
        }
        else if (msg == WmDpiChangedMessage)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_lastContentHeight > 0)
                {
                    ApplyContentHeight(_lastContentHeight);
                }
                else
                {
                    ApplyWindowSizeLimits(GetCurrentMonitorWorkArea());
                    KeepWindowInWorkArea();
                }
            }, DispatcherPriority.Background);
        }

        return IntPtr.Zero;
    }

    private void ApplyWindowBounds(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var monitorInfo = new MonitorInfo();
        monitorInfo.Size = Marshal.SizeOf<MonitorInfo>();
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var workArea = monitorInfo.WorkArea;
        var monitorArea = monitorInfo.MonitorArea;
        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);

        minMaxInfo.MaxPosition.X = Math.Abs(workArea.Left - monitorArea.Left);
        minMaxInfo.MaxPosition.Y = Math.Abs(workArea.Top - monitorArea.Top);
        minMaxInfo.MaxSize.X = Math.Abs(workArea.Right - workArea.Left);
        minMaxInfo.MaxSize.Y = Math.Abs(workArea.Bottom - workArea.Top);
        minMaxInfo.MinTrackSize.X = (int)Math.Ceiling(MinWidth * dpi.DpiScaleX);
        minMaxInfo.MinTrackSize.Y = (int)Math.Ceiling(MinHeight * dpi.DpiScaleY);
        Marshal.StructureToPtr(minMaxInfo, lParam, true);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _refreshTimer.Stop();
        SaveWindowPlacement();

        try
        {
            _sessionService.StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort cleanup during window shutdown.
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_workspace.HostConsoleIndexPath))
        {
            MessageBox.Show(
                $"Host console file not found:\n{_workspace.HostConsoleIndexPath}",
                "MSFS Local Bridge",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
            return;
        }

        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSFS Local Bridge",
            "WebView2");
        Directory.CreateDirectory(userDataFolder);

        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
        await AppBrowser.EnsureCoreWebView2Async(environment);
        AppBrowser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(11, 20, 32);
        AppBrowser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        AppBrowser.CoreWebView2.Settings.AreDevToolsEnabled = true;
        AppBrowser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        AppBrowser.NavigationCompleted += async (_, _) =>
        {
            await PublishStateAsync();
            await PostWindowStateAsync();
        };
        AppBrowser.Source = new Uri(_workspace.HostConsoleIndexPath);
        _refreshTimer.Start();
    }

    private async void OnWindowStateChanged(object? sender, EventArgs e)
    {
        ApplyFrameState();
        await PostWindowStateAsync();
    }

    private void BeginWindowDrag()
    {
        try
        {
            DragMove();
            if (_lastContentHeight > 0)
            {
                ApplyContentHeight(_lastContentHeight);
            }
            else
            {
                ApplyWindowSizeLimits(GetCurrentMonitorWorkArea());
                KeepWindowInWorkArea();
            }
        }
        catch
        {
            // DragMove throws if the pointer state is not valid for a native drag.
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var envelope = JsonSerializer.Deserialize<WebMessageEnvelope>(e.WebMessageAsJson, _jsonOptions);
        if (envelope is null)
        {
            return;
        }

        if (string.Equals(envelope.Type, "ready", StringComparison.OrdinalIgnoreCase))
        {
            await PublishStateAsync();
            await PostWindowStateAsync();
            return;
        }

        if (string.Equals(envelope.Type, "resize", StringComparison.OrdinalIgnoreCase))
        {
            ApplyContentHeight(envelope.ContentHeight);
            return;
        }

        if (!string.Equals(envelope.Type, "action", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await HandleActionAsync(envelope.Action);
        }
        catch (Exception ex)
        {
            await PostNotificationAsync($"Action failed: {ex.Message}");
            await PublishStateAsync();
        }
    }

    private async Task HandleActionAsync(string? action)
    {
        switch (action)
        {
            case "minimize-window":
                WindowState = WindowState.Minimized;
                return;
            case "toggle-maximize-window":
                return;
            case "close-window":
                Close();
                return;
            case "start-bridge":
                await _sessionService.StartAsync();
                break;
            case "stop-bridge":
                await _sessionService.StopAsync();
                break;
            case "restart-bridge":
                await _sessionService.RestartAsync();
                break;
            case "copy-link":
                Clipboard.SetText(_currentState.ConnectUrl);
                await PostNotificationAsync("Copied AO connect URL.");
                break;
            case "copy-bootstrap-url":
                Clipboard.SetText(_currentState.LocalBridgeUrl);
                await PostNotificationAsync("Copied local bridge URL.");
                break;
            case "copy-diagnostics":
                Clipboard.SetText(_lastDiagnosticsJson);
                await PostNotificationAsync("Copied diagnostics JSON.");
                break;
            case "copy-log":
                Clipboard.SetText(_sessionService.RuntimeLog);
                await PostNotificationAsync("Copied runtime log.");
                break;
            case "clear-log":
                _sessionService.ClearLog();
                break;
            case "copy-mac-setup":
                Clipboard.SetText($"curl -fsSL {_currentState.BootstrapUrl}/listener/mac.sh | bash");
                await PostNotificationAsync("Copied Mac bootstrap command.");
                break;
            case "copy-windows-setup":
                Clipboard.SetText($"powershell -ExecutionPolicy Bypass -Command \"iwr '{_currentState.BootstrapUrl}/listener/windows.ps1' -UseBasicParsing | iex\"");
                await PostNotificationAsync("Copied Windows bootstrap command.");
                break;
            case "open-bootstrap-page":
                OpenExternal(_currentState.ConnectUrl);
                break;
            case "open-mobile-guide":
                OpenExternal(_currentState.BootstrapUrl);
                break;
            case "install-dotnet":
                await PostNotificationAsync(await _prerequisiteInstaller.InstallDotNetDesktopRuntimeAsync());
                break;
            case "install-vcredist":
                await PostNotificationAsync(await _prerequisiteInstaller.InstallVcRedistAsync());
                break;
            case "open-firewall-rules":
                _powerShellRunner.StartElevated(
                    _workspace.BridgeRepoRoot,
                    $"-ExecutionPolicy Bypass -Command \"& '{_workspace.RepairScriptPath}' -Action OpenFirewall39000 -Port 39000\"");
                await PostNotificationAsync("Requested elevated firewall rule update.");
                break;
        }

        await PublishStateAsync();
    }

    private void ToggleWindowState()
    {
        ApplyFrameState();
    }

    private void ApplyContentHeight(double contentHeight)
    {
        if (double.IsNaN(contentHeight) || double.IsInfinity(contentHeight) || contentHeight <= 0)
        {
            return;
        }

        _lastContentHeight = contentHeight;
        var workArea = GetCurrentMonitorWorkArea();
        ApplyWindowSizeLimits(workArea);

        var maxHeight = Math.Max(MinimumWindowHeight, workArea.Height - WorkAreaPadding);
        var targetHeight = Math.Clamp(
            Math.Ceiling(contentHeight + WindowContentChromeHeight),
            MinimumWindowHeight,
            maxHeight);

        MinHeight = MinimumWindowHeight;
        MaxHeight = maxHeight;

        if (Math.Abs(Height - targetHeight) > 1)
        {
            Height = targetHeight;
        }

        KeepWindowInWorkArea();
    }

    private void ApplyInitialWindowPlacement()
    {
        if (TryLoadWindowPlacement(out var placement))
        {
            Width = SanitizeDimension(placement.Width, FixedWindowWidth, MinimumWindowWidth, FixedWindowWidth);
            Height = SanitizeDimension(placement.Height, Height, MinimumWindowHeight, double.MaxValue);
            Left = placement.Left;
            Top = placement.Top;

            var workArea = GetCurrentMonitorWorkArea();
            ApplyWindowSizeLimits(workArea);
            KeepWindowInWorkArea();
            return;
        }

        var cursorWorkArea = GetCursorMonitorWorkArea();
        var targetWidth = ApplyWindowSizeLimits(cursorWorkArea);
        var targetHeight = Math.Clamp(Height, MinimumWindowHeight, Math.Max(MinimumWindowHeight, cursorWorkArea.Height - WorkAreaPadding));
        Height = targetHeight;
        Left = cursorWorkArea.Left + Math.Max(0, (cursorWorkArea.Width - targetWidth) / 2);
        Top = cursorWorkArea.Top + Math.Max(0, (cursorWorkArea.Height - targetHeight) / 2);
        KeepWindowInWorkArea();
    }

    private double ApplyWindowSizeLimits(Rect workArea)
    {
        var maxWidth = Math.Max(MinimumWindowWidth, workArea.Width - WorkAreaPadding);
        var targetWidth = Math.Clamp(FixedWindowWidth, MinimumWindowWidth, maxWidth);
        Width = targetWidth;
        MinWidth = targetWidth;
        MaxWidth = targetWidth;
        MinHeight = MinimumWindowHeight;
        MaxHeight = Math.Max(MinimumWindowHeight, workArea.Height - WorkAreaPadding);
        return targetWidth;
    }

    private void KeepWindowInWorkArea()
    {
        var workArea = GetCurrentMonitorWorkArea();
        if (Left < workArea.Left)
        {
            Left = workArea.Left;
        }

        if (Top < workArea.Top)
        {
            Top = workArea.Top;
        }

        if (Left + Width > workArea.Right)
        {
            Left = Math.Max(workArea.Left, workArea.Right - Width);
        }

        if (Top + Height > workArea.Bottom)
        {
            Top = Math.Max(workArea.Top, workArea.Bottom - Height);
        }
    }

    private Rect GetCursorMonitorWorkArea()
    {
        if (!GetCursorPos(out var cursorPoint))
        {
            return GetPrimaryMonitorWorkArea();
        }

        var monitor = MonitorFromPoint(cursorPoint, MonitorDefaultToPrimary);
        return monitor == IntPtr.Zero ? GetPrimaryMonitorWorkArea() : GetMonitorWorkArea(monitor);
    }

    private Rect GetCurrentMonitorWorkArea()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return GetPrimaryMonitorWorkArea();
        }

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        return monitor == IntPtr.Zero ? GetPrimaryMonitorWorkArea() : GetMonitorWorkArea(monitor);
    }

    private Rect GetPrimaryMonitorWorkArea()
    {
        var monitor = MonitorFromPoint(new PointInt(), MonitorDefaultToPrimary);
        return monitor == IntPtr.Zero ? SystemParameters.WorkArea : GetMonitorWorkArea(monitor);
    }

    private Rect GetMonitorWorkArea(IntPtr monitor)
    {
        var monitorInfo = new MonitorInfo();
        monitorInfo.Size = Marshal.SizeOf<MonitorInfo>();
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return SystemParameters.WorkArea;
        }

        var workArea = monitorInfo.WorkArea;
        var dpi = GetMonitorDpiScale(monitor);
        return new Rect(
            workArea.Left / dpi.ScaleX,
            workArea.Top / dpi.ScaleY,
            Math.Abs(workArea.Right - workArea.Left) / dpi.ScaleX,
            Math.Abs(workArea.Bottom - workArea.Top) / dpi.ScaleY);
    }

    private DpiScaleValue GetMonitorDpiScale(IntPtr monitor)
    {
        try
        {
            if (GetDpiForMonitor(monitor, MonitorDpiType.EffectiveDpi, out var dpiX, out var dpiY) == 0 && dpiX > 0 && dpiY > 0)
            {
                return new DpiScaleValue(dpiX / 96.0, dpiY / 96.0);
            }
        }
        catch
        {
            // Fall back to WPF's current DPI when per-monitor DPI cannot be queried.
        }

        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        return new DpiScaleValue(dpi.DpiScaleX, dpi.DpiScaleY);
    }

    private bool TryLoadWindowPlacement(out WindowPlacement placement)
    {
        placement = new WindowPlacement();

        try
        {
            if (!File.Exists(_windowPlacementPath))
            {
                return false;
            }

            var json = File.ReadAllText(_windowPlacementPath);
            var saved = JsonSerializer.Deserialize<WindowPlacement>(json, _jsonOptions);
            if (saved is null || !IsFinite(saved.Left) || !IsFinite(saved.Top))
            {
                return false;
            }

            placement = saved;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SaveWindowPlacement()
    {
        try
        {
            Directory.CreateDirectory(_settingsDirectory);
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (!IsFinite(bounds.Left) || !IsFinite(bounds.Top) || bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            var placement = new WindowPlacement
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height
            };
            File.WriteAllText(_windowPlacementPath, JsonSerializer.Serialize(placement, _jsonOptions));
        }
        catch
        {
            // Window placement is a convenience setting; failure should never block shutdown.
        }
    }

    private static double SanitizeDimension(double value, double fallback, double min, double max)
    {
        if (!IsFinite(value) || value <= 0)
        {
            return fallback;
        }

        return Math.Clamp(value, min, max);
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private void ApplyFrameState()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        FrameBorder.Margin = isMaximized ? MaximizedFrameMargin : NormalFrameMargin;
        FrameBorder.CornerRadius = isMaximized ? MaximizedFrameRadius : NormalFrameRadius;
        FrameBorder.BorderThickness = new Thickness(1);
        MaximizeGlyph.Text = isMaximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = isMaximized ? "Restore" : "Maximize";
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FindVisualParent<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            return;
        }

        if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
        {
            BeginWindowDrag();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static T? FindVisualParent<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
            {
                return match;
            }

            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    private async Task PublishStateAsync()
    {
        if (AppBrowser.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            var (diagnostics, diagnosticsJson) = await _diagnosticsService.GetAsync();
            var prerequisites = _prerequisiteInstaller.DetectStatus();
            _lastDiagnosticsJson = diagnosticsJson;
            _currentState = _stateBuilder.Build(diagnostics, diagnosticsJson, _sessionService, prerequisites);
        }
        catch (Exception ex)
        {
            _currentState = new AppState
            {
                BlockerText = "Diagnostics error",
                DotNetStatus = "Unknown",
                SimConnectStatus = "Unknown",
                BridgeStatus = _sessionService.IsRunning ? "Running" : "Stopped",
                BootstrapStatus = "Unavailable",
                BridgeControlText = _sessionService.IsRunning ? "Running" : "Stopped",
                PrimaryActionText = "Unavailable",
                RuntimeLog = ex.Message,
                LastIssue = ex.Message,
                CanStartBridge = false,
                CanStopBridge = _sessionService.IsRunning,
                CanRestartBridge = _sessionService.IsRunning,
                CanOpenFirewallRules = false
            };
        }

        var payload = JsonSerializer.Serialize(new { type = "state", state = _currentState }, _jsonOptions);
        AppBrowser.CoreWebView2.PostWebMessageAsJson(payload);
    }

    private async Task PostNotificationAsync(string message)
    {
        if (AppBrowser.CoreWebView2 is null)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new { type = "notification", message }, _jsonOptions);
        AppBrowser.CoreWebView2.PostWebMessageAsJson(payload);
        await Task.CompletedTask;
    }

    private async Task PostWindowStateAsync()
    {
        if (AppBrowser.CoreWebView2 is null)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            type = "window-state",
            maximized = WindowState == WindowState.Maximized
        }, _jsonOptions);
        AppBrowser.CoreWebView2.PostWebMessageAsJson(payload);
        await Task.CompletedTask;
    }

    private static void OpenExternal(string target)
    {
        if (string.IsNullOrWhiteSpace(target) || target == "Not available")
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true
        });
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(PointInt pt, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointInt lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    private enum MonitorDpiType
    {
        EffectiveDpi = 0
    }

    private readonly record struct DpiScaleValue(double ScaleX, double ScaleY);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointInt
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public PointInt Reserved;
        public PointInt MaxSize;
        public PointInt MaxPosition;
        public PointInt MinTrackSize;
        public PointInt MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public MonitorRectangle MonitorArea;
        public MonitorRectangle WorkArea;
        public uint Flags;
    }
}

internal sealed class WebMessageEnvelope
{
    public string Type { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public double ContentHeight { get; set; }
}

internal sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}
