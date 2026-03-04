using System.Collections.ObjectModel;
using BridgeAssistant.WinUI.Models;
using BridgeAssistant.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace BridgeAssistant.WinUI.Views;

public sealed partial class PreflightPage : Page
{
  private readonly App _app;
  private readonly BridgeController _controller;
  private readonly BridgeStateStore _store;
  private readonly ObservableCollection<DiagnosticCheck> _checks = new();

  public PreflightPage()
  {
    InitializeComponent();

    _app = (App)Application.Current;
    _controller = _app.Controller;
    _store = _app.StateStore;

    _store.Changed += OnStoreChanged;
    Unloaded += OnUnloaded;

    ChecksList.ItemsSource = _checks;
    RepairCommandBox.Text = "Select a check row to show repair command.";

    RefreshFromStore();
  }

  private async void OnRunDiagnosticsClick(object sender, RoutedEventArgs e)
  {
    RunDiagnosticsButton.IsEnabled = false;

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
      RunDiagnosticsButton.IsEnabled = true;
    }
  }

  private void OnRerunDiagnosticsClick(object sender, RoutedEventArgs e)
  {
    OnRunDiagnosticsClick(sender, e);
  }

  private void OnCheckSelectionChanged(object sender, SelectionChangedEventArgs e)
  {
    if (ChecksList.SelectedItem is DiagnosticCheck check)
    {
      RepairCommandBox.Text = string.IsNullOrWhiteSpace(check.RepairAction)
        ? "No repair action reported for this check."
        : check.RepairAction;
    }
  }

  private void OnSelectFirewallClick(object sender, RoutedEventArgs e)
  {
    var target = _checks.FirstOrDefault(check =>
      check.Id.Contains("firewall", StringComparison.OrdinalIgnoreCase) &&
      check.Status is CheckStatus.Warn or CheckStatus.Fail) ??
      _checks.FirstOrDefault(check => check.Id.Contains("firewall", StringComparison.OrdinalIgnoreCase));

    if (target is null)
    {
      RepairCommandBox.Text = "Run diagnostics first, then select a firewall check.";
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

  private void OnStoreChanged(object? sender, EventArgs e)
  {
    _ = DispatcherQueue.TryEnqueue(RefreshFromStore);
  }

  private void RefreshFromStore()
  {
    _checks.Clear();
    foreach (var check in _store.LastDiagnosticsReport.Checks)
    {
      _checks.Add(check);
    }

    var count = _checks.Count;
    SummaryText.Text = count == 0
      ? "Run diagnostics to load preflight checks."
      : $"Loaded {count} checks. Select one row to inspect repair command.";

    if (ChecksList.SelectedItem is not DiagnosticCheck)
    {
      var firstIssue = _checks.FirstOrDefault(check => check.Status is CheckStatus.Warn or CheckStatus.Fail);
      if (firstIssue is not null)
      {
        ChecksList.SelectedItem = firstIssue;
        RepairCommandBox.Text = string.IsNullOrWhiteSpace(firstIssue.RepairAction)
          ? "No repair action reported for this check."
          : firstIssue.RepairAction;
      }
    }
  }

  private void OnUnloaded(object sender, RoutedEventArgs e)
  {
    _store.Changed -= OnStoreChanged;
    Unloaded -= OnUnloaded;
  }
}
