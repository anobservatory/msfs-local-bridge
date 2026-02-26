using System.Globalization;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.FlightSimulator.SimConnect;

var bridgeOptions = BridgeOptions.FromEnvironment();
X509Certificate2? wssCertificate = null;

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

if (bridgeOptions.WssEnabled)
{
  wssCertificate = LoadWssCertificate(bridgeOptions);
  builder.WebHost.ConfigureKestrel(serverOptions =>
  {
    serverOptions.ConfigureHttpsDefaults(httpsOptions =>
    {
      httpsOptions.ServerCertificate = wssCertificate;
    });
  });

  builder.WebHost.UseUrls(
    $"http://{bridgeOptions.BindHost}:{bridgeOptions.Port}",
    $"https://{bridgeOptions.WssBindHost}:{bridgeOptions.WssPort}"
  );
}
else
{
  builder.WebHost.UseUrls($"http://{bridgeOptions.BindHost}:{bridgeOptions.Port}");
}

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
  wssEnabled = options.WssEnabled,
  wssHost = options.WssEnabled ? options.WssPublicHost : null,
  wssPort = options.WssEnabled ? options.WssPort : (int?)null,
  wssCertPath = options.WssEnabled ? options.TlsCertPath : null,
  wssKeyPath = options.WssEnabled ? options.TlsKeyPath : null,
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
            squawk = snapshot.Squawk,
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

app.Lifetime.ApplicationStarted.Register(() =>
{
  app.Logger.LogInformation(
    "MSFS bridge listening at ws://{BindHost}:{Port}{Path}",
    bridgeOptions.BindHost,
    bridgeOptions.Port,
    bridgeOptions.StreamPath
  );
  if (bridgeOptions.WssEnabled)
  {
    app.Logger.LogInformation(
      "MSFS bridge secure stream at wss://{PublicHost}:{WssPort}{Path}",
      bridgeOptions.WssPublicHost,
      bridgeOptions.WssPort,
      bridgeOptions.StreamPath
    );
  }
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

static X509Certificate2 LoadWssCertificate(BridgeOptions options)
{
  var certPath = ResolveRuntimePath(options.TlsCertPath);
  var keyPath = ResolveRuntimePath(options.TlsKeyPath);

  if (!File.Exists(certPath))
  {
    throw new FileNotFoundException(
      $"WSS certificate file not found. Set MSFS_BRIDGE_TLS_CERT_PATH or create file at '{certPath}'.",
      certPath
    );
  }

  if (!File.Exists(keyPath))
  {
    throw new FileNotFoundException(
      $"WSS private key file not found. Set MSFS_BRIDGE_TLS_KEY_PATH or create file at '{keyPath}'.",
      keyPath
    );
  }

  try
  {
    // Kestrel expects a certificate with private key. PEM loading is normalized via PKCS#12 re-wrap.
    var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);
    return new X509Certificate2(pem.Export(X509ContentType.Pkcs12));
  }
  catch (Exception ex)
  {
    throw new InvalidOperationException(
      $"Failed to load WSS certificate from cert='{certPath}', key='{keyPath}'.",
      ex
    );
  }
}

static string ResolveRuntimePath(string path)
{
  if (Path.IsPathRooted(path))
  {
    return path;
  }
  return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
}

internal sealed class BridgeOptions
{
  public string BindHost { get; }
  public int Port { get; }
  public string StreamPath { get; }
  public bool WssEnabled { get; }
  public string WssBindHost { get; }
  public int WssPort { get; }
  public string WssPublicHost { get; }
  public string TlsCertPath { get; }
  public string TlsKeyPath { get; }
  public int SampleIntervalMs { get; }
  public int SimConnectPollMs { get; }
  public int ReconnectDelayMs { get; }
  public int ReconnectMaxDelayMs { get; }
  public string DeviceName { get; }
  public string DeviceId { get; }
  public string CompanionVersion { get; }
  public string SimPlatform { get; }
  public string SimVersionFallback { get; }

  private BridgeOptions(
    string bindHost,
    int port,
    string streamPath,
    bool wssEnabled,
    string wssBindHost,
    int wssPort,
    string wssPublicHost,
    string tlsCertPath,
    string tlsKeyPath,
    int sampleIntervalMs,
    int simConnectPollMs,
    int reconnectDelayMs,
    int reconnectMaxDelayMs,
    string deviceName,
    string deviceId,
    string companionVersion,
    string simPlatform,
    string simVersionFallback
  )
  {
    BindHost = bindHost;
    Port = port;
    StreamPath = streamPath;
    WssEnabled = wssEnabled;
    WssBindHost = wssBindHost;
    WssPort = wssPort;
    WssPublicHost = wssPublicHost;
    TlsCertPath = tlsCertPath;
    TlsKeyPath = tlsKeyPath;
    SampleIntervalMs = sampleIntervalMs;
    SimConnectPollMs = simConnectPollMs;
    ReconnectDelayMs = reconnectDelayMs;
    ReconnectMaxDelayMs = reconnectMaxDelayMs;
    DeviceName = deviceName;
    DeviceId = deviceId;
    CompanionVersion = companionVersion;
    SimPlatform = simPlatform;
    SimVersionFallback = simVersionFallback;
  }

  public static BridgeOptions FromEnvironment()
  {
    var bindHost = ReadString("MSFS_BRIDGE_BIND", "0.0.0.0");
    var port = ReadInt("MSFS_BRIDGE_PORT", fallback: 39000, min: 1025, max: 65535);
    var streamPath = NormalizePath(ReadString("MSFS_BRIDGE_PATH", "/stream"));
    var wssEnabled = ReadBool("MSFS_BRIDGE_WSS_ENABLED", false);
    var wssBindHost = ReadString("MSFS_BRIDGE_WSS_BIND", bindHost);
    var wssPort = ReadInt("MSFS_BRIDGE_WSS_PORT", fallback: 39002, min: 1025, max: 65535);
    var wssPublicHost = ReadString("MSFS_BRIDGE_PUBLIC_WSS_HOST", "ao.home.arpa");
    var tlsCertPath = ReadString("MSFS_BRIDGE_TLS_CERT_PATH", Path.Combine("certs", "ao.home.arpa.pem"));
    var tlsKeyPath = ReadString("MSFS_BRIDGE_TLS_KEY_PATH", Path.Combine("certs", "ao.home.arpa-key.pem"));
    var sampleIntervalMs = ReadInt("MSFS_BRIDGE_SAMPLE_MS", fallback: 200, min: 80, max: 2000);
    var simConnectPollMs = ReadInt("MSFS_BRIDGE_POLL_MS", fallback: 25, min: 5, max: 1000);
    var reconnectDelayMs = ReadInt("MSFS_BRIDGE_RECONNECT_MS", fallback: 2000, min: 500, max: 30000);
    var reconnectMaxDelayMsRaw = ReadInt("MSFS_BRIDGE_RECONNECT_MAX_MS", fallback: 10000, min: 1000, max: 120000);
    var reconnectMaxDelayMs = Math.Max(reconnectDelayMs, reconnectMaxDelayMsRaw);
    var deviceName = ReadString("MSFS_BRIDGE_DEVICE_NAME", Environment.MachineName);
    var companionVersion = ReadString("MSFS_BRIDGE_COMPANION_VERSION", ResolveCompanionVersion());
    var simPlatform = ReadString("MSFS_BRIDGE_SIM_PLATFORM", "msfs").ToLowerInvariant();
    var simVersionFallback = ReadString("MSFS_BRIDGE_SIM_VERSION_FALLBACK", "Local Bridge");
    var deviceId = NormalizeDeviceId(deviceName);

    return new BridgeOptions(
      bindHost: bindHost,
      port: port,
      streamPath: streamPath,
      wssEnabled: wssEnabled,
      wssBindHost: wssBindHost,
      wssPort: wssPort,
      wssPublicHost: wssPublicHost,
      tlsCertPath: tlsCertPath,
      tlsKeyPath: tlsKeyPath,
      sampleIntervalMs: sampleIntervalMs,
      simConnectPollMs: simConnectPollMs,
      reconnectDelayMs: reconnectDelayMs,
      reconnectMaxDelayMs: reconnectMaxDelayMs,
      deviceName: deviceName,
      deviceId: deviceId,
      companionVersion: companionVersion,
      simPlatform: simPlatform,
      simVersionFallback: simVersionFallback
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

  private static bool ReadBool(string name, bool fallback)
  {
    var raw = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(raw))
    {
      return fallback;
    }

    var normalized = raw.Trim().ToLowerInvariant();
    return normalized switch
    {
      "1" => true,
      "true" => true,
      "yes" => true,
      "y" => true,
      "on" => true,
      "0" => false,
      "false" => false,
      "no" => false,
      "n" => false,
      "off" => false,
      _ => fallback,
    };
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

  private static string NormalizeDeviceId(string deviceName)
  {
    var normalized = deviceName
      .Trim()
      .ToLowerInvariant()
      .Replace(' ', '-');
    if (normalized.Length == 0)
    {
      normalized = "windows-flight-device";
    }
    return $"local:{normalized}";
  }

  private static string ResolveCompanionVersion()
  {
    var entryAssembly = Assembly.GetEntryAssembly();
    var informational = entryAssembly?
      .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
      .InformationalVersion;
    if (!string.IsNullOrWhiteSpace(informational))
    {
      var trimmed = informational.Trim();
      var plusIndex = trimmed.IndexOf('+');
      return plusIndex >= 0 ? trimmed[..plusIndex] : trimmed;
    }

    var version = entryAssembly?.GetName().Version;
    if (version is not null)
    {
      var major = Math.Max(0, version.Major);
      var minor = Math.Max(0, version.Minor);
      var build = Math.Max(0, version.Build);
      return $"{major}.{minor}.{build}";
    }

    return "0.1.x";
  }
}

internal readonly record struct OwnshipSnapshot(
  string Id,
  string? Callsign,
  string? TailNumber,
  string? AircraftTitle,
  string? Squawk,
  string SimVersionLabel,
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

  public void Clear()
  {
    lock (_gate)
    {
      _latest = null;
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
  private bool _simFlightActive;
  private DateTimeOffset _lastConnectWarningAt = DateTimeOffset.MinValue;
  private volatile string? _simApplicationName;

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
        _simConnect.OnRecvEvent += OnRecvEvent;
        _simConnect.OnRecvException += OnRecvException;
        _simConnect.OnRecvSimobjectData += OnRecvSimobjectData;

        RegisterOwnshipDefinition(_simConnect);
        RegisterSystemEvents(_simConnect);
        _requestedOwnshipStream = false;
        _simFlightActive = false;
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
    simConnect.AddToDataDefinition(DefinitionId.Ownship, "ATC AIRLINE", null, SIMCONNECT_DATATYPE.STRING64, 0f, SimConnect.SIMCONNECT_UNUSED);
    simConnect.AddToDataDefinition(DefinitionId.Ownship, "ATC FLIGHT NUMBER", null, SIMCONNECT_DATATYPE.STRING64, 0f, SimConnect.SIMCONNECT_UNUSED);
    simConnect.AddToDataDefinition(DefinitionId.Ownship, "TRANSPONDER CODE:1", "number", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
    simConnect.RegisterDataDefineStruct<OwnshipData>(DefinitionId.Ownship);
  }

  private void RegisterSystemEvents(SimConnect simConnect)
  {
    simConnect.SubscribeToSystemEvent(EventId.SimStart, "SimStart");
    simConnect.SubscribeToSystemEvent(EventId.SimStop, "SimStop");
  }

  private void RequestOwnshipStream(string reason)
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
      _logger.LogInformation("Ownship data stream requested ({Reason}).", reason);
    }
  }

  private void StopOwnshipStream()
  {
    lock (_gate)
    {
      if (_simConnect is null || !_requestedOwnshipStream)
      {
        return;
      }

      _simConnect.RequestDataOnSimObject(
        RequestId.Ownship,
        DefinitionId.Ownship,
        SimConnect.SIMCONNECT_OBJECT_ID_USER,
        SIMCONNECT_PERIOD.NEVER,
        0,
        0,
        0,
        0
      );

      _requestedOwnshipStream = false;
      _snapshotStore.Clear();
      _logger.LogInformation("Ownship data stream stopped.");
    }
  }

  private void OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
  {
    _simApplicationName = NormalizeText(data.szApplicationName);
    _simFlightActive = false;
    _logger.LogInformation("SimConnect session opened: {Version}", data.szApplicationName);
    // SimStart may not re-fire when bridge reconnects mid-flight.
    // Request stream immediately and infer active flight from first valid ownship frame.
    RequestOwnshipStream("session-open");
    _logger.LogInformation("Waiting for first valid ownship telemetry...");
  }

  private void OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
  {
    _simFlightActive = false;
    _snapshotStore.Clear();
    _simApplicationName = null;
    _logger.LogWarning("MSFS session closed. Waiting for simulator restart...");
    Disconnect();
  }

  private void OnRecvEvent(SimConnect sender, SIMCONNECT_RECV_EVENT data)
  {
    if (data.uEventID == (uint)EventId.SimStart)
    {
      if (!_simFlightActive)
      {
        _simFlightActive = true;
        _logger.LogInformation("SimStart received. Flight session active.");
      }
      RequestOwnshipStream("simstart");
      return;
    }

    if (data.uEventID == (uint)EventId.SimStop)
    {
      if (_simFlightActive)
      {
        _logger.LogInformation("SimStop received. Flight session inactive.");
      }
      _simFlightActive = false;
      StopOwnshipStream();
    }
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
    if (
      !IsValidCoordinates(ownship.LatitudeDeg, ownship.LongitudeDeg)
      || IsLikelyMenuPlaceholder(ownship)
    )
    {
      return;
    }

    if (!_simFlightActive)
    {
      _simFlightActive = true;
      _logger.LogInformation("Ownship telemetry detected. Flight session active.");
    }

    var snapshot = new OwnshipSnapshot(
      Id: "msfs_ownship",
      Callsign: ResolveCallsign(ownship),
      TailNumber: NormalizeText(ownship.AtcId),
      AircraftTitle: NormalizeText(ownship.Title),
      Squawk: FormatSquawk(ownship.TransponderCode),
      SimVersionLabel: ResolveSimVersionLabel(_simApplicationName, _options.SimVersionFallback),
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
    _simFlightActive = false;
    _snapshotStore.Clear();
    _simApplicationName = null;

    if (_simConnect is not null)
    {
      _simConnect.OnRecvOpen -= OnRecvOpen;
      _simConnect.OnRecvQuit -= OnRecvQuit;
      _simConnect.OnRecvEvent -= OnRecvEvent;
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

  private static bool IsLikelyMenuPlaceholder(OwnshipData ownship)
  {
    // Menu/non-flight states can emit a zeroed telemetry placeholder near Null Island.
    var nearNullIsland = Math.Abs(ownship.LatitudeDeg) <= 0.05 && Math.Abs(ownship.LongitudeDeg) <= 0.05;
    if (!nearNullIsland)
    {
      return false;
    }

    // Sim menu/teardown placeholders can report unstable on-ground flags.
    // Treat low-altitude + low-speed telemetry near Null Island as invalid.
    var lowAltitude = Math.Abs(ownship.IndicatedAltitudeFt) <= 1000 && Math.Abs(ownship.PlaneAltitudeFt) <= 1000;
    var lowGroundSpeed = Math.Abs(ownship.GroundVelocityKt) <= 30;
    return lowAltitude && lowGroundSpeed;
  }

  private static string? ResolveCallsign(OwnshipData ownship)
  {
    var airline = NormalizeText(ownship.AtcAirline);
    var flightNumber = NormalizeText(ownship.AtcFlightNumber);
    var atcId = NormalizeText(ownship.AtcId);

    if (!string.IsNullOrWhiteSpace(airline) && !string.IsNullOrWhiteSpace(flightNumber))
    {
      return $"{airline}{flightNumber}";
    }

    if (!string.IsNullOrWhiteSpace(airline))
    {
      return airline;
    }

    if (!string.IsNullOrWhiteSpace(atcId))
    {
      return atcId;
    }

    return flightNumber;
  }

  private static string? FormatSquawk(int rawValue)
  {
    if (rawValue < 0)
    {
      return null;
    }

    // Prefer direct decimal form when simulator already emits plain 4-digit code.
    if (rawValue <= 7777)
    {
      var decimalCode = rawValue.ToString("D4", CultureInfo.InvariantCulture);
      if (IsOctalDigits(decimalCode))
      {
        return decimalCode;
      }
    }

    // Fallback for BCD-like representation (nibble-encoded octal digits).
    if (rawValue > 0x7777)
    {
      return null;
    }

    var d0 = rawValue & 0xF;
    var d1 = (rawValue >> 4) & 0xF;
    var d2 = (rawValue >> 8) & 0xF;
    var d3 = (rawValue >> 12) & 0xF;
    if (d0 > 7 || d1 > 7 || d2 > 7 || d3 > 7)
    {
      return null;
    }

    return string.Create(
      4,
      (d3, d2, d1, d0),
      (span, digits) =>
      {
        span[0] = (char)('0' + digits.d3);
        span[1] = (char)('0' + digits.d2);
        span[2] = (char)('0' + digits.d1);
        span[3] = (char)('0' + digits.d0);
      }
    );
  }

  private static bool IsOctalDigits(string value)
  {
    foreach (var ch in value)
    {
      if (ch is < '0' or > '7')
      {
        return false;
      }
    }

    return true;
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

  private static string ResolveSimVersionLabel(string? applicationName, string fallback)
  {
    var normalized = NormalizeText(applicationName);
    if (!string.IsNullOrWhiteSpace(normalized))
    {
      return normalized;
    }
    return fallback;
  }

  private enum DefinitionId : uint
  {
    Ownship = 1,
  }

  private enum RequestId : uint
  {
    Ownship = 1,
  }

  private enum EventId : uint
  {
    SimStart = 1,
    SimStop = 2,
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

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string AtcAirline;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string AtcFlightNumber;

    public int TransponderCode;
  }
}
