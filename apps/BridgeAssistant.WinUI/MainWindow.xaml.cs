using BridgeAssistant.WinUI.Models;
using BridgeAssistant.WinUI.Services;
using BridgeAssistant.WinUI.Views;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BridgeAssistant.WinUI;

public sealed partial class MainWindow : Window
{
  private readonly Dictionary<string, Type> _pageMap = new()
  {
    ["dashboard"] = typeof(DashboardPage),
    ["wizard"] = typeof(WizardPage),
    ["preflight"] = typeof(PreflightPage),
    ["certs"] = typeof(CertificatesPage),
    ["runtime"] = typeof(RuntimePage),
    ["settings"] = typeof(SettingsPage),
  };

  private readonly App _app;
  private readonly BridgeStateStore _store;

  public MainWindow()
  {
    InitializeComponent();

    _app = (App)Application.Current;
    _store = _app.StateStore;

    _store.Changed += OnStoreChanged;
    Closed += OnWindowClosed;

    var defaultItem = AppNav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault();
    if (defaultItem is not null)
    {
      AppNav.SelectedItem = defaultItem;
      NavigateToTag(defaultItem.Tag as string ?? "dashboard");
    }

    RefreshStatusBar();
  }

  private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
  {
    if (args.SelectedItemContainer?.Tag is string tag)
    {
      NavigateToTag(tag);
    }
  }

  private void NavigateToTag(string tag)
  {
    if (!_pageMap.TryGetValue(tag, out var pageType))
    {
      return;
    }

    if (ContentFrame.CurrentSourcePageType != pageType)
    {
      ContentFrame.Navigate(pageType);
    }
  }

  private void OnStoreChanged(object? sender, EventArgs e)
  {
    _ = DispatcherQueue.TryEnqueue(RefreshStatusBar);
  }

  private void RefreshStatusBar()
  {
    StatusBridgeText.Text = _store.BridgeRunning ? "Bridge: Running" : "Bridge: Idle";
    StatusSimconnectText.Text = _store.BridgeRunning ? "SimConnect: Connected" : "SimConnect: Waiting";
    StatusIssuesText.Text = $"Issues: {_store.TotalIssues()}";
    StatusModeText.Text = $"WSS mode: {_store.Settings.WssMode.ToString().ToLowerInvariant()}";

    if (_store.BridgeRunning)
    {
      StatusDot.Foreground = new SolidColorBrush(Colors.LightGreen);
      return;
    }

    if (_store.TotalIssues() > 0)
    {
      StatusDot.Foreground = new SolidColorBrush(Colors.Goldenrod);
      return;
    }

    StatusDot.Foreground = new SolidColorBrush(Colors.Gray);
  }

  private void OnWindowClosed(object sender, WindowEventArgs args)
  {
    _store.Changed -= OnStoreChanged;
    Closed -= OnWindowClosed;
  }
}
