using System.Globalization;

namespace BridgeAssistant.WinUI.Models;

public enum WssMode
{
  Auto,
  Disabled,
  Required,
}

public sealed class BridgeSettings
{
  public string BindHost { get; set; } = "0.0.0.0";

  public int WsPort { get; set; } = 39000;

  public int WssPort { get; set; } = 39002;

  public string Domain { get; set; } = "ao.home.arpa";

  public int SampleIntervalMs { get; set; } = 200;

  public int PollIntervalMs { get; set; } = 25;

  public int ReconnectDelayMs { get; set; } = 2000;

  public int ReconnectMaxDelayMs { get; set; } = 10000;

  public WssMode WssMode { get; set; } = WssMode.Auto;

  public string CertDirectory { get; set; } = "certs";

  public IReadOnlyList<string> BuildStartScriptArguments()
  {
    var args = new List<string>
    {
      "-BindHost", BindHost,
      "-Port", WsPort.ToString(CultureInfo.InvariantCulture),
      "-WssPort", WssPort.ToString(CultureInfo.InvariantCulture),
      "-LocalDomain", Domain,
      "-SampleIntervalMs", SampleIntervalMs.ToString(CultureInfo.InvariantCulture),
      "-PollIntervalMs", PollIntervalMs.ToString(CultureInfo.InvariantCulture),
      "-ReconnectDelayMs", ReconnectDelayMs.ToString(CultureInfo.InvariantCulture),
      "-ReconnectMaxDelayMs", ReconnectMaxDelayMs.ToString(CultureInfo.InvariantCulture),
    };

    if (WssMode == WssMode.Disabled)
    {
      args.Add("-DisableWss");
    }
    else if (WssMode == WssMode.Required)
    {
      args.Add("-RequireWss");
    }

    return args;
  }

  public static BridgeSettings CreateDefault()
  {
    return new BridgeSettings();
  }
}
