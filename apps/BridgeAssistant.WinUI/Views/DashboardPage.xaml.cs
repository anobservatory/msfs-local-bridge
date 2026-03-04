using System.Collections.ObjectModel;
using BridgeAssistant.WinUI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BridgeAssistant.WinUI.Views;

public sealed partial class DashboardPage : Page
{
  private readonly App _app;
  private readonly ObservableCollection<string> _issues = new();

  public DashboardPage()
  {
    InitializeComponent();

    _app = (App)Application.Current;
    IssuesList.ItemsSource = _issues;
  }

  private async void OnRunDiagnosticsClick(object sender, RoutedEventArgs e)
  {
    SetBusy(true, "Running diagnostics-v0.ps1 ...");

    try
    {
      var report = await _app.BridgeApi.RunDiagnosticsAsync(_app.Settings);
      _issues.Clear();

      var warn = 0;
      var fail = 0;
      foreach (var check in report.Checks)
      {
        if (check.Status == CheckStatus.Warn || check.Status == CheckStatus.Fail)
        {
          _issues.Add($"[{check.StatusText}] {check.Label}");
        }

        if (check.Status == CheckStatus.Warn)
        {
          warn++;
        }

        if (check.Status == CheckStatus.Fail)
        {
          fail++;
        }
      }

      if (_issues.Count == 0)
      {
        _issues.Add("No warnings/failures in diagnostics report.");
      }

      SummaryText.Text = $"Diagnostics complete. Warn={warn}, Fail={fail}, ExitCode={report.ExitCode}";
    }
    catch (Exception ex)
    {
      _issues.Clear();
      _issues.Add(ex.Message);
      SummaryText.Text = "Diagnostics failed. Check script path/permissions.";
    }
    finally
    {
      SetBusy(false, SummaryText.Text);
    }
  }

  private async void OnStartBridgeClick(object sender, RoutedEventArgs e)
  {
    SetBusy(true, "Starting start-msfs-sync.ps1 ...");

    try
    {
      var result = await _app.BridgeApi.StartBridgeAsync(_app.Settings);
      SummaryText.Text = result.Success ? "Bridge start requested." : $"Start blocked: {result.StandardError}";
    }
    catch (Exception ex)
    {
      SummaryText.Text = $"Start failed: {ex.Message}";
    }
    finally
    {
      SetBusy(false, SummaryText.Text);
    }
  }

  private void OnOpenCertificatesClick(object sender, RoutedEventArgs e)
  {
    Frame.Navigate(typeof(CertificatesPage));
  }

  private void SetBusy(bool busy, string message)
  {
    BusyRing.IsActive = busy;
    RunDiagnosticsButton.IsEnabled = !busy;
    StartBridgeButton.IsEnabled = !busy;

    if (!string.IsNullOrWhiteSpace(message))
    {
      SummaryText.Text = message;
    }
  }
}
