using BridgeAssistant.WinUI.Models;
using BridgeAssistant.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BridgeAssistant.WinUI.Views;

public sealed partial class SettingsPage : Page
{
  private readonly App _app;
  private readonly BridgeStateStore _store;

  public SettingsPage()
  {
    InitializeComponent();

    _app = (App)Application.Current;
    _store = _app.StateStore;

    _store.Changed += OnStoreChanged;
    Unloaded += OnUnloaded;

    LoadFromSettings();
  }

  private void OnSaveClick(object sender, RoutedEventArgs e)
  {
    var settings = _store.Settings;

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

    if (int.TryParse(SampleMsBox.Text, out var sampleMs) && sampleMs > 0)
    {
      settings.SampleIntervalMs = sampleMs;
    }

    if (int.TryParse(PollMsBox.Text, out var pollMs) && pollMs > 0)
    {
      settings.PollIntervalMs = pollMs;
    }

    if (int.TryParse(ReconnectMsBox.Text, out var reconnectMs) && reconnectMs > 0)
    {
      settings.ReconnectDelayMs = reconnectMs;
    }

    if (int.TryParse(ReconnectMaxMsBox.Text, out var reconnectMaxMs) && reconnectMaxMs > 0)
    {
      settings.ReconnectMaxDelayMs = reconnectMaxMs;
    }

    _store.AppendLog("Settings saved. Run diagnostics to refresh status under new settings.");
    ApplyResultText.Text = "Settings applied to runtime model. Re-run diagnostics recommended.";
  }

  private void OnResetDefaultsClick(object sender, RoutedEventArgs e)
  {
    var defaults = BridgeSettings.CreateDefault();
    var settings = _store.Settings;

    settings.BindHost = defaults.BindHost;
    settings.WsPort = defaults.WsPort;
    settings.WssPort = defaults.WssPort;
    settings.Domain = defaults.Domain;
    settings.SampleIntervalMs = defaults.SampleIntervalMs;
    settings.PollIntervalMs = defaults.PollIntervalMs;
    settings.ReconnectDelayMs = defaults.ReconnectDelayMs;
    settings.ReconnectMaxDelayMs = defaults.ReconnectMaxDelayMs;
    settings.WssMode = defaults.WssMode;
    settings.CertDirectory = defaults.CertDirectory;

    LoadFromSettings();
    _store.AppendLog("Settings reset to defaults.");
    ApplyResultText.Text = "Settings reset to defaults.";
  }

  private void OnAdvancedToggleChanged(object sender, RoutedEventArgs e)
  {
    AdvancedPanel.Visibility = AdvancedToggle.IsChecked == true
      ? Visibility.Visible
      : Visibility.Collapsed;
  }

  private void LoadFromSettings()
  {
    var settings = _store.Settings;
    BindHostBox.Text = settings.BindHost;
    WsPortBox.Text = settings.WsPort.ToString();
    WssPortBox.Text = settings.WssPort.ToString();
    DomainBox.Text = settings.Domain;
    SampleMsBox.Text = settings.SampleIntervalMs.ToString();
    PollMsBox.Text = settings.PollIntervalMs.ToString();
    ReconnectMsBox.Text = settings.ReconnectDelayMs.ToString();
    ReconnectMaxMsBox.Text = settings.ReconnectMaxDelayMs.ToString();

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

  private void OnStoreChanged(object? sender, EventArgs e)
  {
    _ = DispatcherQueue.TryEnqueue(() =>
    {
      if (this.FocusState != Microsoft.UI.Xaml.FocusState.Keyboard)
      {
        LoadFromSettings();
      }
    });
  }

  private void OnUnloaded(object sender, RoutedEventArgs e)
  {
    _store.Changed -= OnStoreChanged;
    Unloaded -= OnUnloaded;
  }
}
