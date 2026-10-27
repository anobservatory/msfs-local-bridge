internal static class BridgeAddresses
{
  internal static string BuildBootstrapBaseUrl(BridgeOptions options)
  {
    var host = !string.IsNullOrWhiteSpace(options.BootstrapHostIp)
      ? options.BootstrapHostIp
      : options.BindHost;
    return $"http://{host}:{options.Port}{options.BootstrapPath}";
  }

  internal static string SelectWssHostForBootstrapClient(BridgeOptions options)
  {
    if (!string.IsNullOrWhiteSpace(options.BootstrapHostIp))
    {
      return options.BootstrapHostIp;
    }

    if (!string.IsNullOrWhiteSpace(options.WssPublicHost))
    {
      return options.WssPublicHost;
    }

    return options.BindHost;
  }
}
