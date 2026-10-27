using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using MsfsLocalBridge.Models;
using MsfsLocalBridge.Services;

namespace MsfsLocalBridge;

public partial class MainWindow : Window
{
    private readonly BridgeWorkspace _workspace = new();
    private readonly PowerShellRunner _powerShellRunner = new();
    private readonly BridgeSessionService _sessionService;
    private readonly ConnectionSettings _connectionSettings;
    private bool _actionInProgress;
    private bool _isClosing;
    private Task? _statePublishTask;
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
    private AppState _currentState = new();

    public MainWindow()
    {
        InitializeComponent();
        _windowPlacementPath = Path.Combine(_settingsDirectory, "window-placement.json");
        ApplyFrameState();
        _connectionSettings = new ConnectionSettings(Path.Combine(_settingsDirectory, "connection-settings.json"));
        _sessionService = new BridgeSessionService(_workspace, _powerShellRunner, _connectionSettings);
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
        Activated += async (_, _) =>
        {
            if (!_actionInProgress) await RefreshStateAsync();
        };
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _isClosing = true;
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

    private Task PublishStateAsync()
    {
        if (_isClosing) return Task.CompletedTask;
        // Timer, navigation, and actions share one diagnostics process.
        if (_statePublishTask is { IsCompleted: false }) return _statePublishTask;
        return _statePublishTask = PublishStateCoreAsync();
    }

    private async Task RefreshStateAsync()
    {
        if (_isClosing || AppBrowser.CoreWebView2 is null) return;
        _diagnosticsService.Invalidate();
        if (_statePublishTask is { IsCompleted: false } pending) await pending;
        await PublishStateAsync();
    }

    private async Task PublishStateCoreAsync()
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
            _currentState = _stateBuilder.Build(diagnostics, diagnosticsJson, _sessionService, prerequisites, _connectionSettings.AllowLanAccess);
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

        if (_isClosing) return;
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

    private Task PostActionStateAsync(bool busy)
    {
        if (_isClosing) return Task.CompletedTask;
        AppBrowser.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "action-state", busy }, _jsonOptions));
        return Task.CompletedTask;
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


}
