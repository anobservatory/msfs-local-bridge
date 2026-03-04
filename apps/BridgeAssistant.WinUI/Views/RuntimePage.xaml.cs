using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BridgeAssistant.WinUI.Views;

public sealed partial class RuntimePage : Page
{
  private readonly App _app;

  public RuntimePage()
  {
    InitializeComponent();
    _app = (App)Application.Current;
    _app.BridgeApi.LogReceived += OnBridgeLogReceived;
  }

  private async void OnStartClick(object sender, RoutedEventArgs e)
  {
    var result = await _app.BridgeApi.StartBridgeAsync(_app.Settings);
    RuntimeStateText.Text = result.Success ? "Bridge state: running" : $"Bridge state: blocked ({result.StandardError})";
  }

  private async void OnStopClick(object sender, RoutedEventArgs e)
  {
    var result = await _app.BridgeApi.StopBridgeAsync();
    RuntimeStateText.Text = result.Success ? "Bridge state: idle" : $"Bridge stop failed ({result.StandardError})";
  }

  private async void OnRestartClick(object sender, RoutedEventArgs e)
  {
    await _app.BridgeApi.StopBridgeAsync();
    var result = await _app.BridgeApi.StartBridgeAsync(_app.Settings);
    RuntimeStateText.Text = result.Success ? "Bridge state: running" : $"Bridge restart failed ({result.StandardError})";
  }

  private void OnBridgeLogReceived(object? sender, string line)
  {
    _ = DispatcherQueue.TryEnqueue(() =>
    {
      RuntimeLogBox.Text += line + Environment.NewLine;
    });
  }
}
