using System.Text;
using static BootstrapPages;
using static BridgeAddresses;
using static BridgePaths;

internal static class BootstrapEndpoints
{
  public static void Map(WebApplication app, BridgeOptions bridgeOptions)
  {
    if (bridgeOptions.BootstrapEnabled)
    {
      var bootstrapBasePath = bridgeOptions.BootstrapPath;
      var bootstrapBaseUrl = BuildBootstrapBaseUrl(bridgeOptions);
      var caRoute = $"{bootstrapBasePath}/ca/rootCA.pem";
      var wssHostForBootstrapClient = SelectWssHostForBootstrapClient(bridgeOptions);
      var wssBootstrapClientUrl = bridgeOptions.WssEnabled
        ? $"wss://{wssHostForBootstrapClient}:{bridgeOptions.WssPort}{bridgeOptions.StreamPath}"
        : string.Empty;
      var aoConnectUrl = bridgeOptions.WssEnabled
        ? $"https://anobservatory.com/?msfsBridgeUrl={Uri.EscapeDataString(wssBootstrapClientUrl)}"
        : string.Empty;

      app.MapGet(bootstrapBasePath, (ILoggerFactory loggerFactory) =>
      {
        try
        {
          var html = BuildBootstrapHtml(
            bootstrapBaseUrl: bootstrapBaseUrl,
            wssClientUrl: wssBootstrapClientUrl,
            aoConnectUrl: aoConnectUrl,
            wssEnabled: bridgeOptions.WssEnabled
          );
          return Results.Content(html, "text/html; charset=utf-8");
        }
        catch (Exception ex)
        {
          try
          {
            var logger = loggerFactory.CreateLogger("MsfsLocalBridge.Bootstrap");
            logger.LogError(ex, "Bootstrap HTML render failed.");
          }
          catch
          {
            // Best-effort logging only.
          }

          try
          {
            var fallback = BuildBootstrapFallbackText(
              bootstrapBaseUrl: bootstrapBaseUrl,
              wssClientUrl: wssBootstrapClientUrl,
              aoConnectUrl: aoConnectUrl,
              wssEnabled: bridgeOptions.WssEnabled
            );
            return Results.Text(fallback, "text/plain; charset=utf-8");
          }
          catch
          {
            var emergency = new StringBuilder();
            emergency.AppendLine("AO MSFS Listener Bootstrap");
            emergency.AppendLine();
            emergency.AppendLine($"Bootstrap base: {bootstrapBaseUrl}");
            emergency.AppendLine($"Manifest: {bootstrapBaseUrl}/manifest.json");
            emergency.AppendLine($"Root CA: {bootstrapBaseUrl}/ca/rootCA.pem");
            emergency.AppendLine($"Mac script: {bootstrapBaseUrl}/listener/mac.sh");
            emergency.AppendLine($"Windows script: {bootstrapBaseUrl}/listener/windows.ps1");
            return Results.Text(emergency.ToString(), "text/plain; charset=utf-8");
          }
        }
      });


      app.MapGet($"{bootstrapBasePath}/manifest.json", () => Results.Json(new
      {
        name = "msfs-local-bridge-bootstrap",
        status = "ok",
        bootstrapBaseUrl,
        wssEnabled = bridgeOptions.WssEnabled,
        wssBridgeUrl = bridgeOptions.WssEnabled ? wssBootstrapClientUrl : null,
        aoConnectUrl = bridgeOptions.WssEnabled ? aoConnectUrl : null,
        localDomain = bridgeOptions.WssPublicHost,
        hostIp = bridgeOptions.BootstrapHostIp,
        caUrl = $"{bootstrapBaseUrl}/ca/rootCA.pem",
        macScriptUrl = $"{bootstrapBaseUrl}/listener/mac.sh",
        windowsScriptUrl = $"{bootstrapBaseUrl}/listener/windows.ps1",
      }));

      app.MapGet(caRoute, async (HttpContext context) =>
      {
        var caPath = ResolveRuntimePath(bridgeOptions.BootstrapCaPath);
        if (!File.Exists(caPath))
        {
          return Results.NotFound(new
          {
            status = "missing_ca",
            path = caPath,
            message = "Root CA file not found. Run setup-wss-cert-v0.ps1 first.",
          });
        }

        context.Response.ContentType = "application/x-pem-file";
        context.Response.Headers.ContentDisposition = "inline; filename=rootCA.pem";
        await context.Response.SendFileAsync(caPath);
        return Results.Empty;
      });

      app.MapGet($"{bootstrapBasePath}/listener/mac.sh", () =>
      {
        var script = BuildMacListenerScript(
          bootstrapBaseUrl: bootstrapBaseUrl,
          wssPublicHost: bridgeOptions.WssPublicHost,
          bootstrapHostIp: bridgeOptions.BootstrapHostIp
        );
        return Results.Text(script, "text/x-shellscript; charset=utf-8");
      });

      app.MapGet($"{bootstrapBasePath}/listener/windows.ps1", () =>
      {
        var script = BuildWindowsListenerScript(
          bootstrapBaseUrl: bootstrapBaseUrl,
          wssPublicHost: bridgeOptions.WssPublicHost,
          bootstrapHostIp: bridgeOptions.BootstrapHostIp
        );
        return Results.Text(script, "text/plain; charset=utf-8");
      });
    }
  }
}
