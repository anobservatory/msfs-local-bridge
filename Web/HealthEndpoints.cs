using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using static BridgeAddresses;

internal static class HealthEndpoints
{
  public static void Map(WebApplication app, BridgeOptions bridgeOptions)
  {
    // This loopback-only handshake lets AO recognize an old LAN address for this
    // very PC, even when sharing is disabled and no simulator flight is loaded.
    app.Map("/local-connection", async (HttpContext context, BridgeOptions options) =>
    {
      if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
      {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
      }
      if (!context.WebSockets.IsWebSocketRequest)
      {
        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        return;
      }

      string[] addresses;
      try
      {
        addresses = NetworkInterface.GetAllNetworkInterfaces()
          .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
          .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
          .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
          .Select(address => address.Address.ToString()).Distinct().ToArray();
      }
      catch (NetworkInformationException)
      {
        addresses = [];
      }

      using var socket = await context.WebSockets.AcceptWebSocketAsync();
      var bytes = JsonSerializer.SerializeToUtf8Bytes(new
      {
        source = "msfs_local", version = 1, streamPath = options.StreamPath,
        port = options.Port, hostAddresses = addresses,
      });
      await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, context.RequestAborted);
      await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "discovery complete", context.RequestAborted);
    });

    app.MapGet("/", (BridgeOptions options, OwnshipSnapshotStore snapshotStore, RouteDiagnosticsState routeDiagnostics) => Results.Json(new
    {
      name = "msfs-local-bridge",
      status = "ok",
      streamPath = options.StreamPath,
      sampleIntervalMs = options.SampleIntervalMs,
      simConnectMode = options.SimConnectMode,
      simConnectWorkerPath = options.UseWorkerSimConnect ? options.SimConnectWorkerExecutablePath : null,
      wssEnabled = options.WssEnabled,
      wssHost = options.WssEnabled ? options.WssPublicHost : null,
      wssPort = options.WssEnabled ? options.WssPort : (int?)null,
      wssCertPath = options.WssEnabled ? options.TlsCertPath : null,
      wssKeyPath = options.WssEnabled ? options.TlsKeyPath : null,
      wssPfxPath = options.WssEnabled ? options.TlsPfxPath : null,
      wssCertMode = options.WssEnabled ? (options.HasPfxCertificate ? "pfx" : "pem") : null,
      bootstrapEnabled = options.BootstrapEnabled,
      bootstrapPath = options.BootstrapEnabled ? options.BootstrapPath : null,
      bootstrapUrl = options.BootstrapEnabled ? BuildBootstrapBaseUrl(options) : null,
      bootstrapCaPath = options.BootstrapEnabled ? options.BootstrapCaPath : null,
      routeDiagnostics = new
      {
        provider = routeDiagnostics.Provider,
        supported = routeDiagnostics.Supported,
        requestActive = routeDiagnostics.RequestActive,
        lastRouteUpdateAtMs = routeDiagnostics.LastRouteUpdateAtMs,
        originAirportId = routeDiagnostics.OriginAirportId,
        destinationAirportId = routeDiagnostics.DestinationAirportId,
        lastError = routeDiagnostics.LastError,
      },
      ownshipRoute = snapshotStore.TryRead(out var snapshot)
        ? new
        {
          originAirportId = snapshot.OriginAirportId ?? routeDiagnostics.OriginAirportId,
          destinationAirportId = snapshot.DestinationAirportId ?? routeDiagnostics.DestinationAirportId,
        }
        : new
        {
          originAirportId = routeDiagnostics.OriginAirportId,
          destinationAirportId = routeDiagnostics.DestinationAirportId,
        },
    }));
  }
}
