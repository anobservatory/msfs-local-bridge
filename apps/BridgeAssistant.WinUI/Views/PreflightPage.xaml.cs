using System.Collections.ObjectModel;
using BridgeAssistant.WinUI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace BridgeAssistant.WinUI.Views;

public sealed partial class PreflightPage : Page
{
  private readonly App _app;
  private readonly ObservableCollection<DiagnosticCheck> _checks = new();

  public PreflightPage()
  {
    InitializeComponent();

    _app = (App)Application.Current;
    ChecksList.ItemsSource = _checks;
    RepairCommandBox.Text = "Select a check to see recommended repair action.";
  }

  private async void OnRunDiagnosticsClick(object sender, RoutedEventArgs e)
  {
    RunDiagnosticsButton.IsEnabled = false;

    try
    {
      var report = await _app.BridgeApi.RunDiagnosticsAsync(_app.Settings);
      _checks.Clear();
      foreach (var check in report.Checks)
      {
        _checks.Add(check);
      }

      if (_checks.Count == 0)
      {
        RepairCommandBox.Text = "Diagnostics returned no checks. Verify script execution and JSON output.";
      }
    }
    catch (Exception ex)
    {
      _checks.Clear();
      RepairCommandBox.Text = ex.Message;
    }
    finally
    {
      RunDiagnosticsButton.IsEnabled = true;
    }
  }

  private void OnCheckSelectionChanged(object sender, SelectionChangedEventArgs e)
  {
    if (ChecksList.SelectedItem is DiagnosticCheck check)
    {
      RepairCommandBox.Text = string.IsNullOrWhiteSpace(check.RepairAction)
        ? "No repair command reported for this check."
        : check.RepairAction;
    }
  }

  private void OnSelectFirewallClick(object sender, RoutedEventArgs e)
  {
    var target = _checks.FirstOrDefault(check => check.Id.Contains("firewall", StringComparison.OrdinalIgnoreCase));
    if (target is null)
    {
      RepairCommandBox.Text = "Run diagnostics first, then choose firewall check.";
      return;
    }

    ChecksList.SelectedItem = target;
    ChecksList.ScrollIntoView(target);
  }

  private void OnCopyCommandClick(object sender, RoutedEventArgs e)
  {
    var package = new DataPackage();
    package.SetText(RepairCommandBox.Text ?? string.Empty);
    Clipboard.SetContent(package);
  }
}
