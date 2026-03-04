using BridgeAssistant.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Threading;

namespace BridgeAssistant.WinUI.Views;

public sealed partial class RuntimePage : Page
{
  private readonly App _app;
  private readonly BridgeController _controller;
  private readonly BridgeStateStore _store;
  private readonly DispatcherTimer _uptimeTimer;

  public RuntimePage()
  {
    InitializeComponent();

    _app = (App)Application.Current;
    _controller = _app.Controller;
    _store = _app.StateStore;

    _store.Changed += OnStoreChanged;
    Unloaded += OnUnloaded;

    _uptimeTimer = new DispatcherTimer
    {
      Interval = TimeSpan.FromSeconds(1),
    };
    _uptimeTimer.Tick += OnUptimeTick;
    _uptimeTimer.Start();

    RefreshUi();
  }

  private async void OnStartClick(object sender, RoutedEventArgs e)
  {
    SetButtonsEnabled(false);

    try
    {
      var result = await _controller.StartBridgeAsync();
      if (!result.Success)
      {
        HintText.Text = $"Start blocked: {result.StandardError}";
      }
    }
    catch (Exception ex)
    {
      HintText.Text = $"Start failed: {ex.Message}";
    }
    finally
    {
      RefreshUi();
    }
  }

  private async void OnStopClick(object sender, RoutedEventArgs e)
  {
    SetButtonsEnabled(false);

    try
    {
      await _controller.StopBridgeAsync();
    }
    catch (Exception ex)
    {
      HintText.Text = $"Stop failed: {ex.Message}";
    }
    finally
    {
      RefreshUi();
    }
  }

  private async void OnRestartClick(object sender, RoutedEventArgs e)
  {
    SetButtonsEnabled(false);

    try
    {
      await _controller.StopBridgeAsync();
      await _controller.StartBridgeAsync();
    }
    catch (Exception ex)
    {
      HintText.Text = $"Restart failed: {ex.Message}";
    }
    finally
    {
      RefreshUi();
    }
  }

  private async void OnRerunDiagnosticsClick(object sender, RoutedEventArgs e)
  {
    try
    {
      await _controller.RunDiagnosticsAsync();
      RefreshUi();
    }
    catch (Exception ex)
    {
      HintText.Text = $"Diagnostics failed: {ex.Message}";
    }
  }

  private void OnClearLogClick(object sender, RoutedEventArgs e)
  {
    _store.ClearActivityLog();
    HintText.Text = "Runtime log cleared.";
  }

  private void OnStoreChanged(object? sender, EventArgs e)
  {
    _ = DispatcherQueue.TryEnqueue(RefreshUi);
  }

  private void OnUptimeTick(object? sender, object e)
  {
    if (!_store.BridgeRunning || _store.BridgeStartedAt is null)
    {
      UptimeText.Text = "-";
      return;
    }

    var elapsed = DateTimeOffset.Now - _store.BridgeStartedAt.Value;
    UptimeText.Text = FormatUptime(elapsed);
  }

  private void RefreshUi()
  {
    RuntimeStateText.Text = _store.BridgeRunning ? "Running" : "Idle";
    RuntimeStateText.Foreground = new SolidColorBrush(_store.BridgeRunning ? Colors.LightGreen : Colors.LightGray);

    WsEndpointText.Text = _store.BuildWsEndpoint();
    WssEndpointText.Text = _store.BuildWssEndpoint();
    WssModeText.Text = _store.GetWssModeLabel();
    ConnectionText.Text = _store.RuntimeConnections.ToString();

    if (_store.BridgeRunning)
    {
      HintText.Text = "Bridge running. Logs stream below.";
    }
    else
    {
      HintText.Text = "Bridge not running. Press Start to launch.";
    }

    var reason = _store.GetStartBlockingReason();
    if (string.IsNullOrWhiteSpace(reason))
    {
      StartButton.IsEnabled = !_store.BridgeRunning;
      StartReasonText.Text = "Start is available with current settings.";
      StartReasonText.Foreground = new SolidColorBrush(Colors.LightGray);
    }
    else
    {
      StartButton.IsEnabled = false;
      StartReasonText.Text = reason;
      StartReasonText.Foreground = new SolidColorBrush(Colors.Goldenrod);
    }

    StopButton.IsEnabled = _store.BridgeRunning;
    RestartButton.IsEnabled = _store.BridgeRunning;

    RuntimeLogBox.Text = string.Join(Environment.NewLine, _store.ActivityLog.TakeLast(160));
  }

  private static string FormatUptime(TimeSpan elapsed)
  {
    if (elapsed.TotalHours >= 1)
    {
      return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
    }

    if (elapsed.TotalMinutes >= 1)
    {
      return $"{elapsed.Minutes}m {elapsed.Seconds}s";
    }

    return $"{elapsed.Seconds}s";
  }

  private void SetButtonsEnabled(bool enabled)
  {
    StartButton.IsEnabled = enabled;
    StopButton.IsEnabled = enabled;
    RestartButton.IsEnabled = enabled;
  }

  private void OnUnloaded(object sender, RoutedEventArgs e)
  {
    _uptimeTimer.Stop();
    _uptimeTimer.Tick -= OnUptimeTick;
    _store.Changed -= OnStoreChanged;
    Unloaded -= OnUnloaded;
  }
}
