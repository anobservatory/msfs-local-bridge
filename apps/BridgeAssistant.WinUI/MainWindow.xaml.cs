using BridgeAssistant.WinUI.Models;
using BridgeAssistant.WinUI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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

  public MainWindow()
  {
    InitializeComponent();

    var app = (App)Application.Current;
    SetStatusMode(app.Settings.WssMode);

    var defaultItem = AppNav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault();
    if (defaultItem is not null)
    {
      AppNav.SelectedItem = defaultItem;
      NavigateToTag(defaultItem.Tag as string ?? "dashboard");
    }
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

    if (tag == "runtime")
    {
      StatusBridgeText.Text = "Bridge: runtime view";
    }
  }

  private void SetStatusMode(WssMode mode)
  {
    StatusModeText.Text = $"WSS mode: {mode.ToString().ToLowerInvariant()}";
  }
}
