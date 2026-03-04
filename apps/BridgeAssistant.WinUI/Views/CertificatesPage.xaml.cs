using BridgeAssistant.WinUI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BridgeAssistant.WinUI.Views;

public sealed partial class CertificatesPage : Page
{
  private readonly App _app;

  public CertificatesPage()
  {
    InitializeComponent();
    _app = (App)Application.Current;
  }

  private async void OnGenerateClick(object sender, RoutedEventArgs e)
  {
    SetBusy(true, "Running setup-wss-cert-v0.ps1 ...");

    try
    {
      var result = await _app.BridgeApi.RunSetupCertificateAsync(_app.Settings);
      ResultText.Text = result.Success ? "Certificate setup completed." : $"Certificate setup failed: {result.StandardError}";
    }
    catch (Exception ex)
    {
      ResultText.Text = ex.Message;
    }
    finally
    {
      SetBusy(false, ResultText.Text);
    }
  }

  private async void OnVerifyClick(object sender, RoutedEventArgs e)
  {
    SetBusy(true, "Verifying trust via diagnostics ...");

    try
    {
      var result = await _app.BridgeApi.VerifyTrustAsync(_app.Settings);
      ResultText.Text = result.Success ? "Trust verified." : $"Trust not verified: {result.StandardError}";
    }
    catch (Exception ex)
    {
      ResultText.Text = ex.Message;
    }
    finally
    {
      SetBusy(false, ResultText.Text);
    }
  }

  private async void OnRefreshDiagnosticsClick(object sender, RoutedEventArgs e)
  {
    SetBusy(true, "Running diagnostics for mkcert/cert checks ...");

    try
    {
      var report = await _app.BridgeApi.RunDiagnosticsAsync(_app.Settings);
      var mkcert = report.Checks.FirstOrDefault(check => check.Id == "network.mkcert");
      MkcertStatusText.Text = mkcert is null
        ? "mkcert check missing in diagnostics output."
        : $"mkcert: {mkcert.StatusText}";
      ResultText.Text = "Diagnostics refreshed for certificate checks.";
    }
    catch (Exception ex)
    {
      ResultText.Text = ex.Message;
    }
    finally
    {
      SetBusy(false, ResultText.Text);
    }
  }

  private void SetBusy(bool busy, string message)
  {
    GenerateButton.IsEnabled = !busy;
    VerifyButton.IsEnabled = !busy;
    RefreshButton.IsEnabled = !busy;

    if (!string.IsNullOrWhiteSpace(message))
    {
      ResultText.Text = message;
    }
  }
}
