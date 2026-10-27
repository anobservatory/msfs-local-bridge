using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

internal static class TelemetryEndpoints
{
  public static void Map(WebApplication app, BridgeOptions bridgeOptions)
  {
    app.Map(bridgeOptions.StreamPath, async (
      HttpContext context,
      OwnshipSnapshotStore snapshotStore,
      RouteDiagnosticsState routeDiagnostics,
      BridgeOptions options,
      ILoggerFactory loggerFactory
    ) =>
    {
      if (!context.WebSockets.IsWebSocketRequest)
      {
        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        await context.Response.WriteAsync("Upgrade Required");
        return;
      }

      using var socket = await context.WebSockets.AcceptWebSocketAsync();
      var logger = loggerFactory.CreateLogger("MsfsLocalBridge.Socket");
      logger.LogInformation("WebSocket connected from {RemoteIp}", context.Connection.RemoteIpAddress);
      var cadenceWindowStartedAt = DateTimeOffset.UtcNow;
      var cadenceTelemetrySent = 0;
      DateTimeOffset? waitingTelemetryLoggedAt = null;

      try
      {
        while (!context.RequestAborted.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
          var emittedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
          if (snapshotStore.TryRead(out var snapshot))
          {
            waitingTelemetryLoggedAt = null;
            var originAirportId = snapshot.OriginAirportId ?? routeDiagnostics.OriginAirportId;
            var destinationAirportId = snapshot.DestinationAirportId ?? routeDiagnostics.DestinationAirportId;
            var payload = new
            {
              source = "msfs_local",
              version = 1,
              ts = emittedAtMs,
              sessionMeta = new
              {
                hasPairedDevice = true,
                hasAnySession = true,
                deviceId = options.DeviceId,
                deviceName = options.DeviceName,
                companionVersion = options.CompanionVersion,
                simPlatform = options.SimPlatform,
                simVersion = snapshot.SimVersionLabel,
                lastHeartbeatAtMs = emittedAtMs,
                lastTelemetryAtMs = emittedAtMs,
                lastSimSampleAtMs = snapshot.TimestampMs,
              },
              ownship = new
              {
                id = snapshot.Id,
                callsign = snapshot.Callsign,
                tailNumber = snapshot.TailNumber,
                aircraftTitle = snapshot.AircraftTitle,
                typeCode = snapshot.TypeCode,
                originAirportId,
                destinationAirportId,
                squawk = snapshot.Squawk,
                lat = snapshot.Lat,
                lon = snapshot.Lon,
                altBaroFt = snapshot.AltBaroFt,
                altGeomFt = snapshot.AltGeomFt,
                gsKt = snapshot.GsKt,
                headingDegTrue = snapshot.HeadingDegTrue,
                trackDegTrue = snapshot.TrackDegTrue,
                vsFpm = snapshot.VsFpm,
                onGround = snapshot.OnGround,
              },
            };

            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(
              new ArraySegment<byte>(bytes),
              WebSocketMessageType.Text,
              true,
              context.RequestAborted
            );

            cadenceTelemetrySent += 1;
            var now = DateTimeOffset.UtcNow;
            if ((now - cadenceWindowStartedAt) >= TimeSpan.FromSeconds(15))
            {
              var sampleAgeMs = Math.Max(0, emittedAtMs - snapshot.TimestampMs);
              logger.LogInformation(
                "Bridge cadence to {RemoteIp}: telemetrySent={TelemetrySent}, sampleAgeMs={SampleAgeMs}",
                context.Connection.RemoteIpAddress,
                cadenceTelemetrySent,
                sampleAgeMs
              );
              cadenceWindowStartedAt = now;
              cadenceTelemetrySent = 0;
            }
          }
          else
          {
            var now = DateTimeOffset.UtcNow;
            var shouldLogWaiting = waitingTelemetryLoggedAt is null
              || (now - waitingTelemetryLoggedAt.Value) >= TimeSpan.FromSeconds(15);
            if (shouldLogWaiting)
            {
              logger.LogInformation(
                "WebSocket connected from {RemoteIp} but waiting for first ownship telemetry.",
                context.Connection.RemoteIpAddress
              );
              waitingTelemetryLoggedAt = now;
            }
          }

          await Task.Delay(options.SampleIntervalMs, context.RequestAborted);
        }
      }
      catch (OperationCanceledException)
      {
        // Normal shutdown path.
      }
      catch (WebSocketException ex)
      {
        logger.LogInformation(ex, "WebSocket disconnected unexpectedly.");
      }
      finally
      {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
          try
          {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
          }
          catch
          {
            // Ignore close errors.
          }
        }

        logger.LogInformation(
          "WebSocket closed for {RemoteIp} (closeStatus={CloseStatus}, description={CloseDescription})",
          context.Connection.RemoteIpAddress,
          socket.CloseStatus?.ToString() ?? "None",
          socket.CloseStatusDescription ?? string.Empty
        );
      }
    });
  }
}
