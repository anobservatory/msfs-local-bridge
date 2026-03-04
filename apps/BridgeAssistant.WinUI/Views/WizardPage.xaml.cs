using BridgeAssistant.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BridgeAssistant.WinUI.Views;

public sealed partial class WizardPage : Page
{
  private readonly App _app;
  private readonly BridgeController _controller;
  private readonly BridgeStateStore _store;

  public WizardPage()
  {
    InitializeComponent();

    _app = (App)Application.Current;
    _controller = _app.Controller;
    _store = _app.StateStore;

    _store.Changed += OnStoreChanged;
    Unloaded += OnUnloaded;

    RefreshUi();
  }

  private async void OnRunDiagnosticsClick(object sender, RoutedEventArgs e)
  {
    try
    {
      await _controller.RunDiagnosticsAsync();
    }
    catch (Exception ex)
    {
      _store.AppendLog($"Diagnostics failed: {ex.Message}");
    }
  }

  private async void OnStartBridgeClick(object sender, RoutedEventArgs e)
  {
    try
    {
      await _controller.StartBridgeAsync();
      Frame.Navigate(typeof(RuntimePage));
    }
    catch (Exception ex)
    {
      _store.AppendLog($"Start failed: {ex.Message}");
    }
  }

  private void OnOpenPreflightClick(object sender, RoutedEventArgs e)
  {
    Frame.Navigate(typeof(PreflightPage));
  }

  private void OnOpenCertificatesClick(object sender, RoutedEventArgs e)
  {
    Frame.Navigate(typeof(CertificatesPage));
  }

  private void OnOpenRuntimeClick(object sender, RoutedEventArgs e)
  {
    Frame.Navigate(typeof(RuntimePage));
  }

  private void OnStoreChanged(object? sender, EventArgs e)
  {
    _ = DispatcherQueue.TryEnqueue(RefreshUi);
  }

  private void RefreshUi()
  {
    Step1Badge.Text = _store.DiagnosticsRan ? "Complete" : "Pending";

    if (_store.IsFirewallReady())
    {
      Step2Badge.Text = "Complete";
      Step2Badge.Foreground = new SolidColorBrush(Colors.LightGreen);
    }
    else
    {
      Step2Badge.Text = "Action needed";
      Step2Badge.Foreground = new SolidColorBrush(Colors.Goldenrod);
    }

    if (!_store.IsMkcertReady())
    {
      Step3Badge.Text = "Install mkcert";
      Step3Badge.Foreground = new SolidColorBrush(Colors.Goldenrod);
    }
    else if (_store.IsWssCertificateReady())
    {
      Step3Badge.Text = "Complete";
      Step3Badge.Foreground = new SolidColorBrush(Colors.LightGreen);
    }
    else
    {
      Step3Badge.Text = "Generate cert";
      Step3Badge.Foreground = new SolidColorBrush(Colors.Goldenrod);
    }

    if (_store.BridgeRunning)
    {
      Step4Badge.Text = "Running";
      Step4Badge.Foreground = new SolidColorBrush(Colors.LightGreen);
    }
    else
    {
      Step4Badge.Text = "Pending";
      Step4Badge.Foreground = new SolidColorBrush(Colors.LightGray);
    }

    var reason = _store.GetStartBlockingReason();
    if (string.IsNullOrWhiteSpace(reason))
    {
      StartButton.IsEnabled = true;
      StartGuideText.Text = "Launches start-msfs-sync.ps1 with current settings.";
      StartGuideText.Foreground = new SolidColorBrush(Colors.LightGray);
      ToolTipService.SetToolTip(StartButton, "Start bridge");
    }
    else
    {
      StartButton.IsEnabled = false;
      StartGuideText.Text = reason;
      StartGuideText.Foreground = new SolidColorBrush(Colors.Goldenrod);
      ToolTipService.SetToolTip(StartButton, reason);
    }
  }

  private void OnUnloaded(object sender, RoutedEventArgs e)
  {
    _store.Changed -= OnStoreChanged;
    Unloaded -= OnUnloaded;
  }
}
