using BridgeAssistant.WinUI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BridgeAssistant.WinUI.Views;

public sealed partial class SettingsPage : Page
{
  private readonly App _app;

  public SettingsPage()
  {
    InitializeComponent();

    _app = (App)Application.Current;
    LoadFromSettings();
  }

  private void OnApplyClick(object sender, RoutedEventArgs e)
  {
    var settings = _app.Settings;

    settings.BindHost = string.IsNullOrWhiteSpace(BindHostBox.Text) ? settings.BindHost : BindHostBox.Text.Trim();
    settings.Domain = string.IsNullOrWhiteSpace(DomainBox.Text) ? settings.Domain : DomainBox.Text.Trim();
    settings.WssMode = ParseWssMode(WssModeBox.SelectedItem as string);

    if (int.TryParse(WsPortBox.Text, out var wsPort) && wsPort > 0)
    {
      settings.WsPort = wsPort;
    }

    if (int.TryParse(WssPortBox.Text, out var wssPort) && wssPort > 0)
    {
      settings.WssPort = wssPort;
    }

    ApplyResultText.Text = "Settings applied to in-memory runtime model.";
  }

  private void LoadFromSettings()
  {
    var settings = _app.Settings;
    BindHostBox.Text = settings.BindHost;
    WsPortBox.Text = settings.WsPort.ToString();
    WssPortBox.Text = settings.WssPort.ToString();
    DomainBox.Text = settings.Domain;
    WssModeBox.SelectedItem = settings.WssMode.ToString();
  }

  private static WssMode ParseWssMode(string? value)
  {
    return value?.ToLowerInvariant() switch
    {
      "disabled" => WssMode.Disabled,
      "required" => WssMode.Required,
      _ => WssMode.Auto,
    };
  }
}
