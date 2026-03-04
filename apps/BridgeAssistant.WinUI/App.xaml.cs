using BridgeAssistant.WinUI.Models;
using BridgeAssistant.WinUI.Services;
using Microsoft.UI.Xaml;

namespace BridgeAssistant.WinUI;

public partial class App : Application
{
  private Window? _window;

  public BridgeSettings Settings { get; } = BridgeSettings.CreateDefault();

  public IBridgeHostApi BridgeApi { get; }

  public BridgeStateStore StateStore { get; }

  public BridgeController Controller { get; }

  public App()
  {
    InitializeComponent();

    BridgeApi = new LocalBridgeHostApi();
    StateStore = new BridgeStateStore(Settings);
    Controller = new BridgeController(BridgeApi, StateStore);
  }

  protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
  {
    _window = new MainWindow();
    _window.Closed += (_, _) => Controller.Dispose();
    _window.Activate();
  }
}
