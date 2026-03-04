using BridgeAssistant.WinUI.Models;
using BridgeAssistant.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace BridgeAssistant.WinUI.Views;

public sealed partial class CertificatesPage : Page
{
  private readonly App _app;
  private readonly BridgeController _controller;
  private readonly BridgeStateStore _store;

  public CertificatesPage()
  {
    InitializeComponent();

    _app = (App)Application.Current;
    _controller = _app.Controller;
    _store = _app.StateStore;

    _store.Changed += OnStoreChanged;
    Unloaded += OnUnloaded;

    RefreshFromStore();
  }

  private async void OnGenerateClick(object sender, RoutedEventArgs e)
  {
    SetBusy(true, "Running setup-wss-cert-v0.ps1 ...");

    try
    {
      var result = await _controller.RunSetupCertificateAsync();
      ResultText.Text = result.Success
        ? "Certificate setup completed. Diagnostics refreshed."
        : $"Certificate setup failed: {result.StandardError}";
    }
    catch (Exception ex)
    {
      ResultText.Text = ex.Message;
    }
    finally
    {
      SetBusy(false);
      RefreshFromStore();
    }
  }

  private async void OnVerifyClick(object sender, RoutedEventArgs e)
  {
    SetBusy(true, "Verifying trust via diagnostics ...");

    try
    {
      var result = await _controller.VerifyTrustAsync();
      ResultText.Text = result.Success ? "Trust verified." : $"Trust not verified: {result.StandardError}";
    }
    catch (Exception ex)
    {
      ResultText.Text = ex.Message;
    }
    finally
    {
      SetBusy(false);
      RefreshFromStore();
    }
  }

  private async void OnRefreshDiagnosticsClick(object sender, RoutedEventArgs e)
  {
    SetBusy(true, "Running diagnostics ...");

    try
    {
      await _controller.RunDiagnosticsAsync();
      ResultText.Text = "Diagnostics refreshed for certificate checks.";
    }
    catch (Exception ex)
    {
      ResultText.Text = ex.Message;
    }
    finally
    {
      SetBusy(false);
      RefreshFromStore();
    }
  }

  private void OnApplyDomainClick(object sender, RoutedEventArgs e)
  {
    var newDomain = DomainBox.Text?.Trim() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(newDomain))
    {
      return;
    }

    _store.Settings.Domain = newDomain;
    _store.AppendLog($"Domain updated to {newDomain}. Run certificate setup again if needed.");
    RefreshFromStore();
  }

  private void OnCopyCommandClick(object sender, RoutedEventArgs e)
  {
    var package = new DataPackage();
    package.SetText(CertCommandBox.Text ?? string.Empty);
    Clipboard.SetContent(package);
    ResultText.Text = "Certificate command copied.";
  }

  private void OnShowMkcertCheckClick(object sender, RoutedEventArgs e)
  {
    _store.AppendLog("Open Preflight and select network.mkcert check for installation guidance.");
    Frame.Navigate(typeof(PreflightPage));
  }

  private void RefreshFromStore()
  {
    DomainBox.Text = _store.Settings.Domain;

    var mkcertReady = _store.IsMkcertReady();
    MkcertStatusText.Text = mkcertReady ? "Ready" : (_store.DiagnosticsRan ? "Missing/Not verified" : "Check needed");
    MkcertStatusText.Foreground = new SolidColorBrush(mkcertReady ? Colors.LightGreen : Colors.Goldenrod);

    CertCommandBox.Text = BuildCertCommand();
    CertPathText.Text = $"certs/{_store.Settings.Domain}.pem";
    KeyPathText.Text = $"certs/{_store.Settings.Domain}-key.pem";

    SetCheckStatus(_store.FindCheck("network.wss_cert"), CertStatusText, "Installed", "Missing", "Warning");
    SetCheckStatus(_store.FindCheck("network.wss_key"), KeyStatusText, "Installed", "Missing", "Warning");
    SetCheckStatus(_store.FindCheck("network.root_ca"), RootStatusText, "Trusted", "Failed", "Not verified");
  }

  private static void SetCheckStatus(DiagnosticCheck? check, TextBlock target, string passText, string failText, string warnText)
  {
    var status = check?.Status ?? CheckStatus.Warn;

    if (status == CheckStatus.Pass)
    {
      target.Text = passText;
      target.Foreground = new SolidColorBrush(Colors.LightGreen);
      return;
    }

    if (status == CheckStatus.Fail)
    {
      target.Text = failText;
      target.Foreground = new SolidColorBrush(Colors.IndianRed);
      return;
    }

    target.Text = warnText;
    target.Foreground = new SolidColorBrush(Colors.Goldenrod);
  }

  private string BuildCertCommand()
  {
    var domain = _store.Settings.Domain;
    var certDir = _store.Settings.CertDirectory;
    return $".\\setup-wss-cert-v0.ps1 -LocalDomain {domain} -CertDir \"{certDir}\"";
  }

  private void SetBusy(bool busy, string? statusMessage = null)
  {
    GenerateButton.IsEnabled = !busy;
    VerifyButton.IsEnabled = !busy;
    RefreshButton.IsEnabled = !busy;

    if (!string.IsNullOrWhiteSpace(statusMessage))
    {
      ResultText.Text = statusMessage;
    }
  }

  private void OnStoreChanged(object? sender, EventArgs e)
  {
    _ = DispatcherQueue.TryEnqueue(RefreshFromStore);
  }

  private void OnUnloaded(object sender, RoutedEventArgs e)
  {
    _store.Changed -= OnStoreChanged;
    Unloaded -= OnUnloaded;
  }
}
