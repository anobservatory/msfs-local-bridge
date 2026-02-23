using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.FlightSimulator.SimConnect;

var bridgeOptions = BridgeOptions.FromEnvironment();

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
  options.TimestampFormat = "HH:mm:ss ";
  options.SingleLine = true;
});

builder.Services.AddSingleton(bridgeOptions);
builder.Services.AddSingleton<OwnshipSnapshotStore>();
builder.Services.AddHostedService<SimConnectOwnshipService>();

builder.WebHost.UseUrls($"http://{bridgeOptions.BindHost}:{bridgeOptions.Port}");

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions
{
  KeepAliveInterval = TimeSpan.FromSeconds(15),
});

app.MapGet("/", (BridgeOptions options) => Results.Json(new
{
  name = "msfs-local-bridge",
  status = "ok",
  streamPath = options.StreamPath,
  sampleIntervalMs = options.SampleIntervalMs,
}));

app.Map(bridgeOptions.StreamPath, async (
  HttpContext context,
  OwnshipSnapshotStore snapshotStore,
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

  try
  {
    while (!context.RequestAborted.IsCancellationRequested && socket.State == WebSocketState.Open)
    {
      if (snapshotStore.TryRead(out var snapshot))
      {
        var payload = new
        {
          source = "msfs_local",
          version = 1,
          ts = snapshot.TimestampMs,
          ownship = new
          {
            id = snapshot.Id,
            callsign = snapshot.Callsign,
            aircraftTitle = snapshot.AircraftTitle,
            lat = snapshot.Lat,
            lon = snapshot.Lon,
            altBaroFt = snapshot.AltBaroFt,
            altGeomFt = snapshot.AltGeomFt,
            gsKt = snapshot.GsKt,
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

    logger.LogInformation("WebSocket closed for {RemoteIp}", context.Connection.RemoteIpAddress);
  }
});

app.Lifetime.ApplicationStarted.Register(() =>
{
  app.Logger.LogInformation(
    "MSFS bridge listening at ws://{BindHost}:{Port}{Path}",
    bridgeOptions.BindHost,
    bridgeOptions.Port,
    bridgeOptions.StreamPath
  );
  app.Logger.LogInformation("Start MSFS flight and keep this bridge window open.");
});

try
{
  app.Run();
}
catch (FileNotFoundException ex) when (IsSimConnectLoadFailure(ex))
{
  Console.Error.WriteLine("SimConnect managed assembly load failed.");
  Console.Error.WriteLine("Check these items:");
  Console.Error.WriteLine("1) lib/Microsoft.FlightSimulator.SimConnect.dll exists");
  Console.Error.WriteLine("2) lib/SimConnect.dll exists");
  Console.Error.WriteLine("3) Visual C++ 2015-2022 Redistributable (x64) is installed");
  Console.Error.WriteLine("4) Use 64-bit .NET runtime");
  throw;
}

static bool IsSimConnectLoadFailure(FileNotFoundException ex)
{
  var fileName = ex.FileName ?? string.Empty;
  return fileName.Contains("Microsoft.FlightSimulator.SimConnect.dll", StringComparison.OrdinalIgnoreCase);
}

internal sealed class BridgeOptions
{
  public string BindHost { get; }
  public int Port { get; }
  public string StreamPath { get; }
  public int SampleIntervalMs { get; }
  public int SimConnectPollMs { get; }
  public int ReconnectDelayMs { get; }
  public int ReconnectMaxDelayMs { get; }

  private BridgeOptions(
    string bindHost,
    int port,
    string streamPath,
    int sampleIntervalMs,
    int simConnectPollMs,
    int reconnectDelayMs,
    int reconnectMaxDelayMs
  )
  {
    BindHost = bindHost;
    Port = port;
    StreamPath = streamPath;
    SampleIntervalMs = sampleIntervalMs;
    SimConnectPollMs = simConnectPollMs;
    ReconnectDelayMs = reconnectDelayMs;
    ReconnectMaxDelayMs = reconnectMaxDelayMs;
  }

  public static BridgeOptions FromEnvironment()
  {
    var bindHost = ReadString("MSFS_BRIDGE_BIND", "0.0.0.0");
    var port = ReadInt("MSFS_BRIDGE_PORT", fallback: 39000, min: 1025, max: 65535);
    var streamPath = NormalizePath(ReadString("MSFS_BRIDGE_PATH", "/stream"));
    var sampleIntervalMs = ReadInt("MSFS_BRIDGE_SAMPLE_MS", fallback: 200, min: 80, max: 2000);
    var simConnectPollMs = ReadInt("MSFS_BRIDGE_POLL_MS", fallback: 25, min: 5, max: 1000);
    var reconnectDelayMs = ReadInt("MSFS_BRIDGE_RECONNECT_MS", fallback: 2000, min: 500, max: 30000);
    var reconnectMaxDelayMsRaw = ReadInt("MSFS_BRIDGE_RECONNECT_MAX_MS", fallback: 10000, min: 1000, max: 120000);
    var reconnectMaxDelayMs = Math.Max(reconnectDelayMs, reconnectMaxDelayMsRaw);

    return new BridgeOptions(
      bindHost: bindHost,
      port: port,
      streamPath: streamPath,
      sampleIntervalMs: sampleIntervalMs,
      simConnectPollMs: simConnectPollMs,
      reconnectDelayMs: reconnectDelayMs,
      reconnectMaxDelayMs: reconnectMaxDelayMs
    );
  }

  private static string ReadString(string name, string fallback)
  {
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value))
    {
      return fallback;
    }
    return value.Trim();
  }

  private static int ReadInt(string name, int fallback, int min, int max)
  {
    var raw = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(raw))
    {
      return fallback;
    }

    if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
    {
      return fallback;
    }

    return Math.Clamp(parsed, min, max);
  }

  private static string NormalizePath(string path)
  {
    var trimmed = path.Trim();
    if (trimmed.Length == 0)
    {
      return "/stream";
    }

    if (!trimmed.StartsWith('/'))
    {
      trimmed = "/" + trimmed;
    }

    return trimmed;
  }
}

internal readonly record struct OwnshipSnapshot(
  string Id,
  string? Callsign,
  string? AircraftTitle,
  double Lat,
  double Lon,
  double AltBaroFt,
  double AltGeomFt,
  double GsKt,
  double TrackDegTrue,
  double VsFpm,
  bool OnGround,
  long TimestampMs
);

internal sealed class OwnshipSnapshotStore
{
  private readonly object _gate = new();
  private OwnshipSnapshot? _latest;

  public void Write(OwnshipSnapshot snapshot)
  {
    lock (_gate)
    {
      _latest = snapshot;
    }
  }

  public bool TryRead(out OwnshipSnapshot snapshot)
  {
    lock (_gate)
    {
      if (_latest is null)
      {
        snapshot = default;
        return false;
      }

      snapshot = _latest.Value;
      return true;
    }
  }
}

internal sealed class SimConnectOwnshipService : BackgroundService
{
  private readonly object _gate = new();
  private readonly ILogger<SimConnectOwnshipService> _logger;
  private readonly OwnshipSnapshotStore _snapshotStore;
  private readonly BridgeOptions _options;

  private SimConnect? _simConnect;
  private AutoResetEvent? _messageSignal;
  private bool _requestedOwnshipStream;
  private DateTimeOffset _lastConnectWarningAt = DateTimeOffset.MinValue;

  public SimConnectOwnshipService(
    ILogger<SimConnectOwnshipService> logger,
    OwnshipSnapshotStore snapshotStore,
    BridgeOptions options
  )
  {
    _logger = logger;
    _snapshotStore = snapshotStore;
    _options = options;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    // Ensure host startup can complete even when SimConnect connects immediately.
    await Task.Yield();

    var reconnectDelayMs = _options.ReconnectDelayMs;

    while (!stoppingToken.IsCancellationRequested)
    {
      if (!EnsureConnected())
      {
        _logger.LogDebug(
          "SimConnect reconnect retry in {DelayMs}ms (max {MaxDelayMs}ms).",
          reconnectDelayMs,
          _options.ReconnectMaxDelayMs
        );
        await Task.Delay(reconnectDelayMs, stoppingToken);
        reconnectDelayMs = Math.Min(_options.ReconnectMaxDelayMs, reconnectDelayMs * 2);
        continue;
      }

      reconnectDelayMs = _options.ReconnectDelayMs;

      try
      {
        _messageSignal?.WaitOne(_options.SimConnectPollMs);
        _simConnect?.ReceiveMessage();
        // Keep this loop cooperative so startup/shutdown remain responsive.
        await Task.Yield();
      }
      catch (COMException ex)
      {
        _logger.LogWarning(ex, "SimConnect receive failed. Reconnecting...");
        Disconnect();
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Unexpected SimConnect error. Reconnecting...");
        Disconnect();
      }
    }

    Disconnect();
  }

  public override void Dispose()
  {
    Disconnect();
    base.Dispose();
  }

  private bool EnsureConnected()
  {
    lock (_gate)
    {
      if (_simConnect is not null)
      {
        return true;
      }

      try
      {
        _messageSignal = new AutoResetEvent(false);
        _simConnect = new SimConnect("AO MSFS Local Bridge", IntPtr.Zero, 0, _messageSignal, 0);
        _simConnect.OnRecvOpen += OnRecvOpen;
        _simConnect.OnRecvQuit += OnRecvQuit;
        _simConnect.OnRecvException += OnRecvException;
        _simConnect.OnRecvSimobjectData += OnRecvSimobjectData;

        RegisterOwnshipDefinition(_simConnect);
        _requestedOwnshipStream = false;
        _logger.LogInformation("Connected to SimConnect.");
        return true;
      }
      catch (Exception ex)
      {
        if (DateTimeOffset.UtcNow - _lastConnectWarningAt > TimeSpan.FromSeconds(10))
        {
          _logger.LogWarning(ex, "Waiting for MSFS + SimConnect...");
          _lastConnectWarningAt = DateTimeOffset.UtcNow;
        }

        DisconnectLocked();
        return false;
      }
    }
  }

  private void RegisterOwnshipDefinition(SimConnect simConnect)
  {
    simConnect.AddToDataDefinition(DefinitionId.Ownship, "PLANE LATITUDE", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
    simConnect.AddToDataDefinition(DefinitionId.Ownship, "PLANE LONGITUDE", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
    simConnect.AddToDataDefinition(DefinitionId.Ownship, "INDICATED ALTITUDE", "feet", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
    simConnect.AddToDataDefinition(DefinitionId.Ownship, "PLANE ALTITUDE", "feet", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
    simConnect.AddToDataDefinition(DefinitionId.Ownship, "GROUND VELOCITY", "knots", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
    simConnect.AddToDataDefinition(DefinitionId.Ownship, "PLANE HEADING DEGREES TRUE", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
    simConnect.AddToDataDefinition(DefinitionId.Ownship, "VERTICAL SPEED", "feet per minute", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
    simConnect.AddToDataDefinition(DefinitionId.Ownship, "SIM ON GROUND", "bool", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
    simConnect.AddToDataDefinition(DefinitionId.Ownship, "TITLE", null, SIMCONNECT_DATATYPE.STRING256, 0f, SimConnect.SIMCONNECT_UNUSED);
    simConnect.AddToDataDefinition(DefinitionId.Ownship, "ATC ID", null, SIMCONNECT_DATATYPE.STRING64, 0f, SimConnect.SIMCONNECT_UNUSED);
    simConnect.RegisterDataDefineStruct<OwnshipData>(DefinitionId.Ownship);
  }

  private void RequestOwnshipStream()
  {
    lock (_gate)
    {
      if (_simConnect is null || _requestedOwnshipStream)
      {
        return;
      }

      _simConnect.RequestDataOnSimObject(
        RequestId.Ownship,
        DefinitionId.Ownship,
        SimConnect.SIMCONNECT_OBJECT_ID_USER,
        SIMCONNECT_PERIOD.SIM_FRAME,
        SIMCONNECT_DATA_REQUEST_FLAG.CHANGED,
        0,
        0,
        0
      );

      _requestedOwnshipStream = true;
      _logger.LogInformation("Ownship data stream requested.");
    }
  }

  private void OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
  {
    _logger.LogInformation("SimConnect session opened: {Version}", data.szApplicationName);
    RequestOwnshipStream();
  }

  private void OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
  {
    _logger.LogWarning("MSFS session closed. Waiting for simulator restart...");
    Disconnect();
  }

  private void OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
  {
    _logger.LogWarning("SimConnect exception: {ExceptionCode}", data.dwException);
  }

  private void OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
  {
    if (data.dwRequestID != (uint)RequestId.Ownship || data.dwData.Length == 0)
    {
      return;
    }

    var ownship = (OwnshipData)data.dwData[0];
    if (!IsValidCoordinates(ownship.LatitudeDeg, ownship.LongitudeDeg))
    {
      return;
    }

    var snapshot = new OwnshipSnapshot(
      Id: "msfs_ownship",
      Callsign: NormalizeText(ownship.AtcId),
      AircraftTitle: NormalizeText(ownship.Title),
      Lat: ownship.LatitudeDeg,
      Lon: ownship.LongitudeDeg,
      AltBaroFt: ownship.IndicatedAltitudeFt,
      AltGeomFt: ownship.PlaneAltitudeFt,
      GsKt: Math.Max(0, ownship.GroundVelocityKt),
      TrackDegTrue: NormalizeHeading(ownship.TrueHeadingDeg),
      VsFpm: ownship.VerticalSpeedFpm,
      OnGround: ownship.SimOnGround != 0,
      TimestampMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    );

    _snapshotStore.Write(snapshot);
  }

  private void Disconnect()
  {
    lock (_gate)
    {
      DisconnectLocked();
    }
  }

  private void DisconnectLocked()
  {
    _requestedOwnshipStream = false;

    if (_simConnect is not null)
    {
      _simConnect.OnRecvOpen -= OnRecvOpen;
      _simConnect.OnRecvQuit -= OnRecvQuit;
      _simConnect.OnRecvException -= OnRecvException;
      _simConnect.OnRecvSimobjectData -= OnRecvSimobjectData;
      _simConnect.Dispose();
      _simConnect = null;
    }

    if (_messageSignal is not null)
    {
      _messageSignal.Dispose();
      _messageSignal = null;
    }
  }

  private static bool IsValidCoordinates(double lat, double lon)
  {
    return
      double.IsFinite(lat)
      && double.IsFinite(lon)
      && lat is >= -90 and <= 90
      && lon is >= -180 and <= 180;
  }

  private static string? NormalizeText(string? value)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return null;
    }
    return value.Trim();
  }

  private static double NormalizeHeading(double headingDeg)
  {
    if (!double.IsFinite(headingDeg))
    {
      return 0;
    }

    var normalized = headingDeg % 360.0;
    if (normalized < 0)
    {
      normalized += 360.0;
    }
    return normalized;
  }

  private enum DefinitionId : uint
  {
    Ownship = 1,
  }

  private enum RequestId : uint
  {
    Ownship = 1,
  }

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
  private struct OwnshipData
  {
    public double LatitudeDeg;
    public double LongitudeDeg;
    public double IndicatedAltitudeFt;
    public double PlaneAltitudeFt;
    public double GroundVelocityKt;
    public double TrueHeadingDeg;
    public double VerticalSpeedFpm;
    public int SimOnGround;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string Title;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string AtcId;
  }
}
