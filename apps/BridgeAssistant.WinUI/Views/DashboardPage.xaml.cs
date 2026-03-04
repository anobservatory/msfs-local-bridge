using System.Collections.ObjectModel;
using BridgeAssistant.WinUI.Models;
using BridgeAssistant.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace BridgeAssistant.WinUI.Views;

public sealed partial class DashboardPage : Page
{
  private readonly App _app;
  private readonly BridgeController _controller;
  private readonly BridgeStateStore _store;
  private readonly ObservableCollection<string> _issues = new();
  private readonly ObservableCollection<string> _recentLogs = new();

  public DashboardPage()
  {
    InitializeComponent();

    _app = (App)Application.Current;
    _controller = _app.Controller;
    _store = _app.StateStore;

    IssuesList.ItemsSource = _issues;
    RecentLogList.ItemsSource = _recentLogs;

    _store.Changed += OnStoreChanged;
    Unloaded += OnUnloaded;

    RefreshUi();
  }

  private async void OnRunDiagnosticsClick(object sender, RoutedEventArgs e)
  {
    StartBridgeButton.IsEnabled = false;

    try
    {
      await _controller.RunDiagnosticsAsync();
    }
    catch (Exception ex)
    {
      _store.AppendLog($"Diagnostics failed: {ex.Message}");
    }
    finally
    {
      RefreshUi();
    }
  }

  private async void OnStartBridgeClick(object sender, RoutedEventArgs e)
  {
    StartBridgeButton.IsEnabled = false;

    try
    {
      var result = await _controller.StartBridgeAsync();
      if (!result.Success)
      {
        _store.AppendLog($"Start blocked: {result.StandardError}");
      }
    }
    catch (Exception ex)
    {
      _store.AppendLog($"Start failed: {ex.Message}");
    }
    finally
    {
      RefreshUi();
      Frame.Navigate(typeof(RuntimePage));
    }
  }

  private void OnOpenCertificatesClick(object sender, RoutedEventArgs e)
  {
    Frame.Navigate(typeof(CertificatesPage));
  }

  private void OnOpenPreflightForFirewallClick(object sender, RoutedEventArgs e)
  {
    _store.AppendLog("Open Preflight and select a firewall check, then run command in Administrator PowerShell.");
    Frame.Navigate(typeof(PreflightPage));
  }

  private void OnCopyWsClick(object sender, RoutedEventArgs e)
  {
    CopyText(_store.BuildWsEndpoint());
  }

  private void OnCopyWssClick(object sender, RoutedEventArgs e)
  {
    CopyText(_store.BuildWssEndpoint());
  }

  private void OnCopyBootstrapClick(object sender, RoutedEventArgs e)
  {
    CopyText(_store.BuildBootstrapEndpoint());
  }

  private void CopyText(string text)
  {
    var package = new DataPackage();
    package.SetText(text);
    Clipboard.SetContent(package);
  }

  private void OnStoreChanged(object? sender, EventArgs e)
  {
    _ = DispatcherQueue.TryEnqueue(RefreshUi);
  }

  private void RefreshUi()
  {
    PassCountText.Text = _store.Count(CheckStatus.Pass).ToString();
    WarnCountText.Text = _store.Count(CheckStatus.Warn).ToString();
    FailCountText.Text = _store.Count(CheckStatus.Fail).ToString();
    BridgeStatusText.Text = _store.BridgeRunning ? "Running" : "Idle";

    BridgeStatusText.Foreground = _store.BridgeRunning
      ? new SolidColorBrush(Colors.LightGreen)
      : new SolidColorBrush(Colors.LightGray);

    WsEndpointBox.Text = _store.BuildWsEndpoint();
    WssEndpointBox.Text = _store.BuildWssEndpoint();
    BootstrapEndpointBox.Text = _store.BuildBootstrapEndpoint();

    AdminInfoBar.IsOpen = true;
    CertInfoBar.IsOpen = !_store.IsWssCertificateReady();

    var reason = _store.GetStartBlockingReason();
    if (string.IsNullOrWhiteSpace(reason))
    {
      StartBridgeButton.IsEnabled = true;
      StartNoteText.Text = "If WSS mode is Required, Start stays blocked until certificate and key checks pass.";
      StartNoteText.Foreground = new SolidColorBrush(Colors.LightGray);
      ToolTipService.SetToolTip(StartBridgeButton, "Start bridge");
    }
    else
    {
      StartBridgeButton.IsEnabled = false;
      StartNoteText.Text = reason;
      StartNoteText.Foreground = new SolidColorBrush(Colors.Goldenrod);
      ToolTipService.SetToolTip(StartBridgeButton, reason);
    }

    _issues.Clear();
    foreach (var issue in _store.LastDiagnosticsReport.Checks.Where(check =>
               check.Status is CheckStatus.Warn or CheckStatus.Fail))
    {
      _issues.Add($"[{issue.StatusText}] {issue.Label}");
    }

    if (_issues.Count == 0)
    {
      _issues.Add(_store.DiagnosticsRan ? "No warnings/failures." : "Run diagnostics to load checks.");
    }

    _recentLogs.Clear();
    foreach (var line in _store.ActivityLog.TakeLast(40))
    {
      _recentLogs.Add(line);
    }
  }

  private void OnUnloaded(object sender, RoutedEventArgs e)
  {
    _store.Changed -= OnStoreChanged;
    Unloaded -= OnUnloaded;
  }
}
