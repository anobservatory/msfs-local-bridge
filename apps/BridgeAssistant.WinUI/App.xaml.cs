using BridgeAssistant.WinUI.Models;
using BridgeAssistant.WinUI.Services;
using Microsoft.UI.Xaml;

namespace BridgeAssistant.WinUI;

public partial class App : Application
{
  private Window? _window;

  public BridgeSettings Settings { get; } = BridgeSettings.CreateDefault();

  public IBridgeHostApi BridgeApi { get; } = new LocalBridgeHostApi();

  public App()
  {
    InitializeComponent();
  }

  protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
  {
    _window = new MainWindow();
    _window.Closed += (_, _) => BridgeApi.Dispose();
    _window.Activate();
  }
}
