using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Diagnostics;
using System.Reflection;
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
builder.Services.AddHttpClient();
builder.Services.AddHostedService<SimConnectOwnshipService>();
builder.Services.AddHostedService<RelayIngestService>();

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
  relayEnabled = options.RelayEnabled,
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
          sessionMeta = new
          {
            hasPairedDevice = true,
            hasAnySession = true,
            deviceId = options.DeviceId,
            deviceName = options.DeviceName,
            companionVersion = options.CompanionVersion,
            simPlatform = options.SimPlatform,
            simVersion = snapshot.SimVersionLabel,
            lastHeartbeatAtMs = snapshot.TimestampMs,
            lastTelemetryAtMs = snapshot.TimestampMs,
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
  if (bridgeOptions.RelayEnabled)
  {
    app.Logger.LogInformation(
      "Relay uplink enabled: {BaseUrl} (credentials file: {CredentialsFile})",
      bridgeOptions.RelayBaseUrl,
      bridgeOptions.RelayCredentialsFile
    );
  }
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
  public string DeviceName { get; }
  public string DeviceId { get; }
  public string CompanionVersion { get; }
  public string SimPlatform { get; }
  public string SimVersionFallback { get; }
  public bool RelayEnabled { get; }
  public string RelayBaseUrl { get; }
  public string? RelayUserId { get; }
  public string? RelayPairCode { get; }
  public string? RelayDeviceId { get; }
  public string? RelayDeviceToken { get; }
  public string RelayCredentialsFile { get; }
  public int RelayLoopMs { get; }
  public int RelayStopAfterNoTelemetrySec { get; }
  public bool HasRelayDirectCredentials => !string.IsNullOrWhiteSpace(RelayDeviceId) && !string.IsNullOrWhiteSpace(RelayDeviceToken);
  public string RelayCredentialsFilePath => Path.IsPathRooted(RelayCredentialsFile)
    ? RelayCredentialsFile
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, RelayCredentialsFile));

  private BridgeOptions(
    string bindHost,
    int port,
    string streamPath,
    int sampleIntervalMs,
    int simConnectPollMs,
    int reconnectDelayMs,
    int reconnectMaxDelayMs,
    string deviceName,
    string deviceId,
    string companionVersion,
    string simPlatform,
    string simVersionFallback,
    bool relayEnabled,
    string relayBaseUrl,
    string? relayUserId,
    string? relayPairCode,
    string? relayDeviceId,
    string? relayDeviceToken,
    string relayCredentialsFile,
    int relayLoopMs,
    int relayStopAfterNoTelemetrySec
  )
  {
    BindHost = bindHost;
    Port = port;
    StreamPath = streamPath;
    SampleIntervalMs = sampleIntervalMs;
    SimConnectPollMs = simConnectPollMs;
    ReconnectDelayMs = reconnectDelayMs;
    ReconnectMaxDelayMs = reconnectMaxDelayMs;
    DeviceName = deviceName;
    DeviceId = deviceId;
    CompanionVersion = companionVersion;
    SimPlatform = simPlatform;
    SimVersionFallback = simVersionFallback;
    RelayEnabled = relayEnabled;
    RelayBaseUrl = relayBaseUrl;
    RelayUserId = relayUserId;
    RelayPairCode = relayPairCode;
    RelayDeviceId = relayDeviceId;
    RelayDeviceToken = relayDeviceToken;
    RelayCredentialsFile = relayCredentialsFile;
    RelayLoopMs = relayLoopMs;
    RelayStopAfterNoTelemetrySec = relayStopAfterNoTelemetrySec;
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
    var deviceName = ReadString("MSFS_BRIDGE_DEVICE_NAME", Environment.MachineName);
    var companionVersion = ReadString("MSFS_BRIDGE_COMPANION_VERSION", ResolveCompanionVersion());
    var simPlatform = ReadString("MSFS_BRIDGE_SIM_PLATFORM", "msfs").ToLowerInvariant();
    var simVersionFallback = ReadString("MSFS_BRIDGE_SIM_VERSION_FALLBACK", "Local Bridge");
    var deviceId = NormalizeDeviceId(deviceName);
    var relayBaseUrl = NormalizeBaseUrl(ReadString("MSFS_RELAY_BASE_URL", "https://anobservatory.com"));
    var relayUserId = ReadOptionalString("MSFS_RELAY_USER_ID", 120);
    var relayPairCode = NormalizePairCode(ReadOptionalString("MSFS_RELAY_PAIR_CODE", 16));
    var relayDeviceId = ReadOptionalString("MSFS_RELAY_DEVICE_ID", 64);
    var relayDeviceToken = ReadOptionalString("MSFS_RELAY_DEVICE_TOKEN", 256);
    var relayCredentialsFile = ReadString("MSFS_RELAY_CREDENTIALS_FILE", "relay-credentials.json");
    var relayLoopMs = ReadInt("MSFS_RELAY_LOOP_MS", fallback: 250, min: 100, max: 2000);
    var relayStopAfterNoTelemetrySec = ReadInt("MSFS_RELAY_STOP_AFTER_NO_TELEMETRY_SEC", fallback: 8, min: 2, max: 120);
    var relayEnabled = ReadBool("MSFS_RELAY_ENABLED", fallback: false)
      || !string.IsNullOrWhiteSpace(relayUserId)
      || !string.IsNullOrWhiteSpace(relayPairCode)
      || !string.IsNullOrWhiteSpace(relayDeviceId)
      || !string.IsNullOrWhiteSpace(relayDeviceToken);

    return new BridgeOptions(
      bindHost: bindHost,
      port: port,
      streamPath: streamPath,
      sampleIntervalMs: sampleIntervalMs,
      simConnectPollMs: simConnectPollMs,
      reconnectDelayMs: reconnectDelayMs,
      reconnectMaxDelayMs: reconnectMaxDelayMs,
      deviceName: deviceName,
      deviceId: deviceId,
      companionVersion: companionVersion,
      simPlatform: simPlatform,
      simVersionFallback: simVersionFallback,
      relayEnabled: relayEnabled,
      relayBaseUrl: relayBaseUrl,
      relayUserId: relayUserId,
      relayPairCode: relayPairCode,
      relayDeviceId: relayDeviceId,
      relayDeviceToken: relayDeviceToken,
      relayCredentialsFile: relayCredentialsFile,
      relayLoopMs: relayLoopMs,
      relayStopAfterNoTelemetrySec: relayStopAfterNoTelemetrySec
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

  private static string? ReadOptionalString(string name, int maxLength)
  {
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value))
    {
      return null;
    }

    var trimmed = value.Trim();
    if (trimmed.Length == 0 || trimmed.Length > maxLength)
    {
      return null;
    }

    return trimmed;
  }

  private static bool ReadBool(string name, bool fallback)
  {
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value))
    {
      return fallback;
    }

    var normalized = value.Trim().ToLowerInvariant();
    return normalized switch
    {
      "1" or "true" or "yes" or "on" => true,
      "0" or "false" or "no" or "off" => false,
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

  private static string NormalizeBaseUrl(string rawValue)
  {
    if (!Uri.TryCreate(rawValue, UriKind.Absolute, out var uri))
    {
      return "https://anobservatory.com";
    }

    var normalized = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    return normalized.Length > 0 ? normalized : "https://anobservatory.com";
  }

  private static string? NormalizePairCode(string? rawValue)
  {
    if (string.IsNullOrWhiteSpace(rawValue))
    {
      return null;
    }

    var normalized = rawValue.Trim().ToUpperInvariant();
    return normalized.Length > 0 ? normalized : null;
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

internal sealed record RelayCredentials(
  string DeviceId,
  string DeviceToken
);

internal sealed record RelaySessionStart(
  string SessionId,
  string IngestSocketUrl,
  int HeartbeatIntervalSec,
  int StaleAfterSec
);

internal sealed record RelayDeviceLinkStart(
  string LinkToken,
  string VerificationUrl,
  int PollIntervalSec,
  int ExpiresInSec
);

internal readonly record struct RelayReceiveOutcome(
  bool SessionInvalid,
  string? Message
);

internal sealed class RelayAuthorizationException : Exception
{
  public RelayAuthorizationException(string message)
    : base(message)
  {
  }
}

internal sealed class RelayIngestService : BackgroundService
{
  private const string PairCodePath = "/api/msfs/v1/pair-code";
  private const string PairDevicePath = "/api/msfs/v1/devices/pair";
  private const string DeviceLinkStartPath = "/api/msfs/v1/devices/link/start";
  private const string DeviceLinkPollPath = "/api/msfs/v1/devices/link/poll";
  private const string SessionStartPath = "/api/msfs/v1/devices/session/start";
  private const string SessionStopPath = "/api/msfs/v1/devices/session/stop";

  private readonly ILogger<RelayIngestService> _logger;
  private readonly OwnshipSnapshotStore _snapshotStore;
  private readonly BridgeOptions _options;
  private readonly IHttpClientFactory _httpClientFactory;
  private DateTimeOffset _lastMissingCredentialWarningAt = DateTimeOffset.MinValue;

  public RelayIngestService(
    ILogger<RelayIngestService> logger,
    OwnshipSnapshotStore snapshotStore,
    BridgeOptions options,
    IHttpClientFactory httpClientFactory
  )
  {
    _logger = logger;
    _snapshotStore = snapshotStore;
    _options = options;
    _httpClientFactory = httpClientFactory;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    await Task.Yield();

    if (!_options.RelayEnabled)
    {
      _logger.LogInformation("Relay uplink disabled (local-only mode).");
      return;
    }

    _logger.LogInformation("Relay uplink worker started.");
    var reconnectDelayMs = _options.ReconnectDelayMs;

    while (!stoppingToken.IsCancellationRequested)
    {
      var httpClient = _httpClientFactory.CreateClient();
      httpClient.Timeout = TimeSpan.FromSeconds(20);

      try
      {
        var credentials = await ResolveCredentialsAsync(httpClient, stoppingToken);
        if (credentials is null)
        {
          MaybeLogMissingCredentials();
          await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
          continue;
        }

        var firstSnapshot = await WaitForOwnshipSnapshotAsync(stoppingToken);
        var session = await StartRelaySessionAsync(httpClient, credentials, firstSnapshot, stoppingToken);
        await RunRelaySessionAsync(httpClient, credentials, session, firstSnapshot, stoppingToken);
        reconnectDelayMs = _options.ReconnectDelayMs;
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        break;
      }
      catch (RelayAuthorizationException ex)
      {
        _logger.LogWarning(ex, "Relay authorization failed. Clearing saved relay credentials and retrying.");
        DeleteSavedRelayCredentials();
        await Task.Delay(reconnectDelayMs, stoppingToken);
        reconnectDelayMs = Math.Min(_options.ReconnectMaxDelayMs, reconnectDelayMs * 2);
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Relay uplink cycle failed. Retrying...");
        await Task.Delay(reconnectDelayMs, stoppingToken);
        reconnectDelayMs = Math.Min(_options.ReconnectMaxDelayMs, reconnectDelayMs * 2);
      }
    }

    _logger.LogInformation("Relay uplink worker stopped.");
  }

  private void MaybeLogMissingCredentials()
  {
    if (DateTimeOffset.UtcNow - _lastMissingCredentialWarningAt < TimeSpan.FromSeconds(15))
    {
      return;
    }

    _lastMissingCredentialWarningAt = DateTimeOffset.UtcNow;
    _logger.LogWarning(
      "Relay credentials unavailable. Set MSFS_RELAY_DEVICE_ID + MSFS_RELAY_DEVICE_TOKEN, " +
      "or provide MSFS_RELAY_PAIR_CODE (optionally MSFS_RELAY_USER_ID), " +
      "or approve device-link in browser when prompted."
    );
  }

  private async Task<RelayCredentials?> ResolveCredentialsAsync(HttpClient httpClient, CancellationToken cancellationToken)
  {
    if (_options.HasRelayDirectCredentials)
    {
      return new RelayCredentials(
        DeviceId: _options.RelayDeviceId!,
        DeviceToken: _options.RelayDeviceToken!
      );
    }

    var fromFile = LoadRelayCredentialsFromFile();
    if (fromFile is not null)
    {
      return fromFile;
    }

    var pairCode = await ResolvePairCodeAsync(httpClient, cancellationToken);
    if (!string.IsNullOrWhiteSpace(pairCode))
    {
      var paired = await PairDeviceAsync(httpClient, pairCode, cancellationToken);
      SaveRelayCredentialsToFile(paired);
      return paired;
    }

    var linked = await TryAutoLinkDeviceAsync(httpClient, cancellationToken);
    if (linked is not null)
    {
      SaveRelayCredentialsToFile(linked);
      return linked;
    }

    return null;
  }

  private async Task<string?> ResolvePairCodeAsync(HttpClient httpClient, CancellationToken cancellationToken)
  {
    if (!string.IsNullOrWhiteSpace(_options.RelayPairCode))
    {
      return _options.RelayPairCode;
    }

    if (string.IsNullOrWhiteSpace(_options.RelayUserId))
    {
      return null;
    }

    var request = new HttpRequestMessage(HttpMethod.Post, BuildRelayUri(PairCodePath));
    request.Headers.TryAddWithoutValidation("x-ao-user-id", _options.RelayUserId);

    using var response = await httpClient.SendAsync(request, cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
      _logger.LogWarning(
        "Failed to request relay pair code ({StatusCode}).",
        (int)response.StatusCode
      );
      return null;
    }

    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    var pairCode = ReadRequiredStringProperty(document.RootElement, "pairCode");
    if (pairCode is null)
    {
      _logger.LogWarning("Relay pair-code response did not include pairCode.");
      return null;
    }

    _logger.LogInformation("Relay pair code issued for scaffold user context.");
    return pairCode;
  }

  private async Task<RelayCredentials?> TryAutoLinkDeviceAsync(
    HttpClient httpClient,
    CancellationToken cancellationToken
  )
  {
    var linkStart = await StartDeviceLinkAsync(httpClient, cancellationToken);
    if (linkStart is null)
    {
      return null;
    }

    _logger.LogInformation("Relay approval required: {VerificationUrl}", linkStart.VerificationUrl);
    TryOpenRelayApprovalInBrowser(linkStart.VerificationUrl);

    var linked = await PollDeviceLinkAsync(httpClient, linkStart, cancellationToken);
    if (linked is null)
    {
      _logger.LogWarning("Relay device-link was not completed. Will retry.");
      return null;
    }

    _logger.LogInformation("Relay device linked successfully ({DeviceId}).", linked.DeviceId);
    return linked;
  }

  private async Task<RelayDeviceLinkStart?> StartDeviceLinkAsync(
    HttpClient httpClient,
    CancellationToken cancellationToken
  )
  {
    using var request = new HttpRequestMessage(HttpMethod.Post, BuildRelayUri(DeviceLinkStartPath))
    {
      Content = CreateJsonContent(new
      {
        deviceName = _options.DeviceName,
        companionVersion = _options.CompanionVersion,
      }),
    };

    using var response = await httpClient.SendAsync(request, cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
      var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
      _logger.LogWarning(
        "Relay link/start failed ({StatusCode}): {Body}",
        (int)response.StatusCode,
        TruncateForLog(errorBody, 200)
      );
      return null;
    }

    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    var root = document.RootElement;
    var linkToken = ReadRequiredStringProperty(root, "linkToken");
    var verificationUrl = ReadRequiredStringProperty(root, "verificationUrl");
    if (linkToken is null || verificationUrl is null)
    {
      _logger.LogWarning("Relay link/start response missing linkToken or verificationUrl.");
      return null;
    }

    var pollIntervalSec = ReadOptionalIntProperty(root, "pollIntervalSec") ?? 2;
    var expiresInSec = ReadOptionalIntProperty(root, "expiresInSec") ?? 300;

    return new RelayDeviceLinkStart(
      LinkToken: linkToken,
      VerificationUrl: verificationUrl,
      PollIntervalSec: Math.Clamp(pollIntervalSec, 1, 10),
      ExpiresInSec: Math.Clamp(expiresInSec, 30, 1200)
    );
  }

  private async Task<RelayCredentials?> PollDeviceLinkAsync(
    HttpClient httpClient,
    RelayDeviceLinkStart linkStart,
    CancellationToken cancellationToken
  )
  {
    var expiresAt = DateTimeOffset.UtcNow.AddSeconds(linkStart.ExpiresInSec);
    var pollIntervalSec = Math.Clamp(linkStart.PollIntervalSec, 1, 10);
    var nextPendingLogAt = DateTimeOffset.MinValue;

    while (!cancellationToken.IsCancellationRequested && DateTimeOffset.UtcNow < expiresAt)
    {
      using var request = new HttpRequestMessage(HttpMethod.Post, BuildRelayUri(DeviceLinkPollPath))
      {
        Content = CreateJsonContent(new { linkToken = linkStart.LinkToken }),
      };

      using var response = await httpClient.SendAsync(request, cancellationToken);

      if ((int)response.StatusCode == 202)
      {
        using var pendingDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var pendingPollAfterSec = ReadOptionalIntProperty(pendingDocument.RootElement, "pollAfterSec");
        if (pendingPollAfterSec is int value)
        {
          pollIntervalSec = Math.Clamp(value, 1, 10);
        }

        if (DateTimeOffset.UtcNow >= nextPendingLogAt)
        {
          _logger.LogInformation("Relay approval pending. Complete approval in browser.");
          nextPendingLogAt = DateTimeOffset.UtcNow.AddSeconds(10);
        }

        await Task.Delay(TimeSpan.FromSeconds(pollIntervalSec), cancellationToken);
        continue;
      }

      if (response.IsSuccessStatusCode)
      {
        using var approvedDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var deviceId = ReadRequiredStringProperty(approvedDocument.RootElement, "deviceId");
        var deviceToken = ReadRequiredStringProperty(approvedDocument.RootElement, "deviceToken");
        if (deviceId is null || deviceToken is null)
        {
          throw new InvalidOperationException("Relay link/poll response missing device credentials.");
        }

        return new RelayCredentials(deviceId, deviceToken);
      }

      if ((int)response.StatusCode == 400)
      {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogWarning(
          "Relay link/poll invalid request ({StatusCode}): {Body}",
          (int)response.StatusCode,
          TruncateForLog(body, 200)
        );
        return null;
      }

      if (IsAuthStatusCode(response.StatusCode))
      {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogWarning(
          "Relay link/poll unauthorized ({StatusCode}): {Body}",
          (int)response.StatusCode,
          TruncateForLog(body, 200)
        );
        return null;
      }

      var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
      throw new InvalidOperationException(
        $"Relay link/poll failed ({(int)response.StatusCode}): {TruncateForLog(errorBody, 200)}"
      );
    }

    return null;
  }

  private void TryOpenRelayApprovalInBrowser(string verificationUrl)
  {
    try
    {
      using var process = Process.Start(new ProcessStartInfo
      {
        FileName = verificationUrl,
        UseShellExecute = true,
      });

      if (process is null)
      {
        _logger.LogWarning(
          "Could not auto-open browser. Open manually to approve relay: {VerificationUrl}",
          verificationUrl
        );
      }
    }
    catch (Exception ex)
    {
      _logger.LogWarning(
        ex,
        "Failed to open browser for relay approval. Open manually: {VerificationUrl}",
        verificationUrl
      );
    }
  }

  private async Task<RelayCredentials> PairDeviceAsync(
    HttpClient httpClient,
    string pairCode,
    CancellationToken cancellationToken
  )
  {
    var requestBody = new
    {
      pairCode,
      deviceName = _options.DeviceName,
      companionVersion = _options.CompanionVersion,
    };

    using var request = new HttpRequestMessage(HttpMethod.Post, BuildRelayUri(PairDevicePath))
    {
      Content = CreateJsonContent(requestBody),
    };

    using var response = await httpClient.SendAsync(request, cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
      var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
      throw new InvalidOperationException(
        $"Relay pair failed ({(int)response.StatusCode}): {TruncateForLog(errorBody, 200)}"
      );
    }

    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    var deviceId = ReadRequiredStringProperty(document.RootElement, "deviceId");
    var deviceToken = ReadRequiredStringProperty(document.RootElement, "deviceToken");
    if (deviceId is null || deviceToken is null)
    {
      throw new InvalidOperationException("Relay pair response is missing device credentials.");
    }

    _logger.LogInformation("Relay device paired successfully ({DeviceId}).", deviceId);
    return new RelayCredentials(deviceId, deviceToken);
  }

  private async Task<OwnshipSnapshot> WaitForOwnshipSnapshotAsync(CancellationToken cancellationToken)
  {
    var nextLogAt = DateTimeOffset.MinValue;

    while (!cancellationToken.IsCancellationRequested)
    {
      if (_snapshotStore.TryRead(out var snapshot))
      {
        return snapshot;
      }

      if (DateTimeOffset.UtcNow >= nextLogAt)
      {
        _logger.LogInformation("Relay waiting for first ownship telemetry (start a flight in MSFS).");
        nextLogAt = DateTimeOffset.UtcNow.AddSeconds(10);
      }

      await Task.Delay(_options.RelayLoopMs, cancellationToken);
    }

    throw new OperationCanceledException(cancellationToken);
  }

  private async Task<RelaySessionStart> StartRelaySessionAsync(
    HttpClient httpClient,
    RelayCredentials credentials,
    OwnshipSnapshot firstSnapshot,
    CancellationToken cancellationToken
  )
  {
    var simVersion = NormalizeRelaySimVersion(firstSnapshot.SimVersionLabel, _options.SimVersionFallback);
    var requestBody = new
    {
      deviceId = credentials.DeviceId,
      sim = new
      {
        platform = _options.SimPlatform,
        version = simVersion,
      },
    };

    using var request = new HttpRequestMessage(HttpMethod.Post, BuildRelayUri(SessionStartPath))
    {
      Content = CreateJsonContent(requestBody),
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.DeviceToken);

    using var response = await httpClient.SendAsync(request, cancellationToken);
    if (IsAuthStatusCode(response.StatusCode))
    {
      throw new RelayAuthorizationException($"Relay session start unauthorized ({(int)response.StatusCode}).");
    }
    if (!response.IsSuccessStatusCode)
    {
      var body = await response.Content.ReadAsStringAsync(cancellationToken);
      throw new InvalidOperationException(
        $"Relay session start failed ({(int)response.StatusCode}): {TruncateForLog(body, 200)}"
      );
    }

    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    var sessionId = ReadRequiredStringProperty(document.RootElement, "sessionId");
    var ingestSocketUrl = ReadRequiredStringProperty(document.RootElement, "ingestSocketUrl");
    if (sessionId is null || ingestSocketUrl is null)
    {
      throw new InvalidOperationException("Relay session start response missing session metadata.");
    }

    var heartbeatIntervalSec = ReadOptionalIntProperty(document.RootElement, "heartbeatIntervalSec") ?? 15;
    var staleAfterSec = ReadOptionalIntProperty(document.RootElement, "staleAfterSec") ?? 10;

    _logger.LogInformation(
      "Relay session started ({SessionId}). heartbeat={HeartbeatSec}s staleAfter={StaleSec}s",
      sessionId,
      heartbeatIntervalSec,
      staleAfterSec
    );

    return new RelaySessionStart(
      SessionId: sessionId,
      IngestSocketUrl: ingestSocketUrl,
      HeartbeatIntervalSec: Math.Clamp(heartbeatIntervalSec, 5, 120),
      StaleAfterSec: Math.Clamp(staleAfterSec, 5, 300)
    );
  }

  private async Task RunRelaySessionAsync(
    HttpClient httpClient,
    RelayCredentials credentials,
    RelaySessionStart session,
    OwnshipSnapshot initialSnapshot,
    CancellationToken stoppingToken
  )
  {
    using var socket = new ClientWebSocket();
    socket.Options.SetRequestHeader("Authorization", $"Bearer {credentials.DeviceToken}");
    await socket.ConnectAsync(new Uri(session.IngestSocketUrl), stoppingToken);
    _logger.LogInformation("Relay ingest socket connected.");

    using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    var receiveTask = ReceiveLoopAsync(socket, relayCts.Token);
    RelayReceiveOutcome? receiveOutcome = null;

    var heartbeatIntervalSec = Math.Clamp(session.HeartbeatIntervalSec, 5, 120);
    var telemetryRefreshIntervalSec = Math.Max(2, Math.Min(heartbeatIntervalSec, 3));
    var nextHeartbeatAt = DateTimeOffset.UtcNow.AddSeconds(heartbeatIntervalSec);
    var lastSentSnapshotTs = 0L;
    var lastTelemetrySentAt = DateTimeOffset.MinValue;
    var sessionStopSent = false;

    async Task PublishSnapshotAsync(
      OwnshipSnapshot snapshot,
      CancellationToken cancellationToken,
      long? telemetryTsOverride = null
    )
    {
      await SendTelemetryAsync(
        socket,
        session.SessionId,
        snapshot,
        cancellationToken,
        telemetryTsOverride
      );
      lastSentSnapshotTs = snapshot.TimestampMs;
      lastTelemetrySentAt = DateTimeOffset.UtcNow;
    }

    await PublishSnapshotAsync(initialSnapshot, relayCts.Token);

    while (!stoppingToken.IsCancellationRequested && socket.State == WebSocketState.Open)
    {
      if (receiveTask.IsCompleted)
      {
        receiveOutcome = await receiveTask;
        if (receiveOutcome.Value.SessionInvalid)
        {
          throw new RelayAuthorizationException(receiveOutcome.Value.Message ?? "Relay ingest session invalid.");
        }

        if (!string.IsNullOrWhiteSpace(receiveOutcome.Value.Message))
        {
          _logger.LogWarning("Relay ingest closed: {Message}", receiveOutcome.Value.Message);
        }
        break;
      }

      var now = DateTimeOffset.UtcNow;
      var hasSnapshot = _snapshotStore.TryRead(out var snapshot);

      if (hasSnapshot)
      {
        if (snapshot.TimestampMs > lastSentSnapshotTs)
        {
          await PublishSnapshotAsync(snapshot, relayCts.Token);
        }
        else if (
          lastTelemetrySentAt == DateTimeOffset.MinValue
          || now - lastTelemetrySentAt >= TimeSpan.FromSeconds(telemetryRefreshIntervalSec)
        )
        {
          await PublishSnapshotAsync(
            snapshot,
            relayCts.Token,
            telemetryTsOverride: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
          );
        }
      }

      if (now >= nextHeartbeatAt)
      {
        await SendHeartbeatAsync(socket, session.SessionId, relayCts.Token);
        nextHeartbeatAt = now.AddSeconds(heartbeatIntervalSec);
      }

      if (
        !hasSnapshot
        && lastTelemetrySentAt != DateTimeOffset.MinValue
        && now - lastTelemetrySentAt >= TimeSpan.FromSeconds(_options.RelayStopAfterNoTelemetrySec)
      )
      {
        await SendSessionStopAsync(socket, session.SessionId, relayCts.Token);
        sessionStopSent = true;
        _logger.LogInformation(
          "Relay session paused after {IdleSeconds}s without telemetry.",
          _options.RelayStopAfterNoTelemetrySec
        );
        break;
      }

      await Task.Delay(_options.RelayLoopMs, stoppingToken);
    }

    relayCts.Cancel();

    if (receiveOutcome is null)
    {
      try
      {
        receiveOutcome = await receiveTask;
      }
      catch (OperationCanceledException)
      {
        receiveOutcome = null;
      }
    }

    if (!sessionStopSent)
    {
      await StopRelaySessionAsync(httpClient, credentials, session.SessionId, stoppingToken);
    }

    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
    {
      try
      {
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "session-end", CancellationToken.None);
      }
      catch
      {
        // Ignore close errors.
      }
    }
  }

  private async Task<RelayReceiveOutcome> ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
  {
    var buffer = new byte[4096];
    using var messageBuffer = new MemoryStream();

    while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
    {
      WebSocketReceiveResult result;
      try
      {
        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        break;
      }
      catch (WebSocketException ex)
      {
        return new RelayReceiveOutcome(false, $"Relay ingest receive failed: {ex.Message}");
      }

      if (result.MessageType == WebSocketMessageType.Close)
      {
        return new RelayReceiveOutcome(false, "Relay ingest socket closed.");
      }

      if (result.Count > 0)
      {
        messageBuffer.Write(buffer, 0, result.Count);
      }

      if (!result.EndOfMessage)
      {
        continue;
      }

      if (messageBuffer.Length == 0)
      {
        continue;
      }

      var message = Encoding.UTF8.GetString(messageBuffer.ToArray());
      messageBuffer.SetLength(0);

      if (!TryParseIngestError(message, out var code, out var errorMessage, out var retryable))
      {
        continue;
      }

      _logger.LogWarning(
        "Relay ingest error event: {Code} ({Message})",
        code ?? "unknown",
        errorMessage ?? "no message"
      );

      if (
        retryable
        || string.Equals(code, "INGEST_SESSION_INVALID", StringComparison.OrdinalIgnoreCase)
      )
      {
        return new RelayReceiveOutcome(true, errorMessage ?? code ?? "Relay ingest session invalid.");
      }
    }

    return new RelayReceiveOutcome(false, null);
  }

  private static bool TryParseIngestError(
    string message,
    out string? code,
    out string? errorMessage,
    out bool retryable
  )
  {
    code = null;
    errorMessage = null;
    retryable = false;

    try
    {
      using var document = JsonDocument.Parse(message);
      var root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Object)
      {
        return false;
      }

      if (
        !root.TryGetProperty("type", out var typeElement)
        || !string.Equals(typeElement.GetString(), "error", StringComparison.OrdinalIgnoreCase)
      )
      {
        return false;
      }

      if (root.TryGetProperty("code", out var codeElement))
      {
        code = codeElement.GetString();
      }
      if (root.TryGetProperty("message", out var messageElement))
      {
        errorMessage = messageElement.GetString();
      }
      if (
        root.TryGetProperty("retryable", out var retryableElement)
        && retryableElement.ValueKind == JsonValueKind.True
      )
      {
        retryable = true;
      }

      return true;
    }
    catch
    {
      return false;
    }
  }

  private async Task SendTelemetryAsync(
    ClientWebSocket socket,
    string sessionId,
    OwnshipSnapshot snapshot,
    CancellationToken cancellationToken,
    long? telemetryTsOverride = null
  )
  {
    var message = new
    {
      type = "telemetry",
      sessionId,
      payload = new
      {
        source = "msfs_local",
        version = 1,
        ts = telemetryTsOverride ?? snapshot.TimestampMs,
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
      },
    };

    await SendJsonAsync(socket, message, cancellationToken);
  }

  private async Task SendHeartbeatAsync(
    ClientWebSocket socket,
    string sessionId,
    CancellationToken cancellationToken
  )
  {
    await SendJsonAsync(
      socket,
      new
      {
        type = "heartbeat",
        sessionId,
        ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
      },
      cancellationToken
    );
  }

  private async Task SendSessionStopAsync(
    ClientWebSocket socket,
    string sessionId,
    CancellationToken cancellationToken
  )
  {
    await SendJsonAsync(
      socket,
      new
      {
        type = "session_stop",
        sessionId,
        ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
      },
      cancellationToken
    );
  }

  private static async Task SendJsonAsync(
    ClientWebSocket socket,
    object payload,
    CancellationToken cancellationToken
  )
  {
    if (socket.State != WebSocketState.Open)
    {
      return;
    }

    var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
    await socket.SendAsync(
      new ArraySegment<byte>(bytes),
      WebSocketMessageType.Text,
      true,
      cancellationToken
    );
  }

  private async Task StopRelaySessionAsync(
    HttpClient httpClient,
    RelayCredentials credentials,
    string sessionId,
    CancellationToken cancellationToken
  )
  {
    using var request = new HttpRequestMessage(HttpMethod.Post, BuildRelayUri(SessionStopPath))
    {
      Content = CreateJsonContent(new { sessionId }),
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.DeviceToken);

    try
    {
      using var response = await httpClient.SendAsync(request, cancellationToken);
      if (response.IsSuccessStatusCode)
      {
        return;
      }

      _logger.LogDebug(
        "Relay session stop returned {StatusCode}.",
        (int)response.StatusCode
      );
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      // shutdown path
    }
    catch (Exception ex)
    {
      _logger.LogDebug(ex, "Relay session stop request failed.");
    }
  }

  private Uri BuildRelayUri(string path)
  {
    var baseUrl = _options.RelayBaseUrl.TrimEnd('/');
    return new Uri($"{baseUrl}{path}", UriKind.Absolute);
  }

  private RelayCredentials? LoadRelayCredentialsFromFile()
  {
    var path = _options.RelayCredentialsFilePath;
    if (!File.Exists(path))
    {
      return null;
    }

    try
    {
      using var stream = File.OpenRead(path);
      using var document = JsonDocument.Parse(stream);
      var root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Object)
      {
        return null;
      }

      var deviceId = ReadRequiredStringProperty(root, "deviceId");
      var deviceToken = ReadRequiredStringProperty(root, "deviceToken");
      if (deviceId is null || deviceToken is null)
      {
        return null;
      }

      return new RelayCredentials(deviceId, deviceToken);
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to read relay credentials file: {Path}", path);
      return null;
    }
  }

  private void SaveRelayCredentialsToFile(RelayCredentials credentials)
  {
    var path = _options.RelayCredentialsFilePath;
    try
    {
      var directory = Path.GetDirectoryName(path);
      if (!string.IsNullOrWhiteSpace(directory))
      {
        Directory.CreateDirectory(directory);
      }

      var payload = new
      {
        deviceId = credentials.DeviceId,
        deviceToken = credentials.DeviceToken,
        updatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
      };

      var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
      {
        WriteIndented = true,
      });
      File.WriteAllText(path, json, Encoding.UTF8);
      _logger.LogInformation("Relay credentials saved to {Path}", path);
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to save relay credentials file: {Path}", path);
    }
  }

  private void DeleteSavedRelayCredentials()
  {
    if (_options.HasRelayDirectCredentials)
    {
      return;
    }

    var path = _options.RelayCredentialsFilePath;
    try
    {
      if (File.Exists(path))
      {
        File.Delete(path);
      }
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to delete relay credentials file: {Path}", path);
    }
  }

  private static bool IsAuthStatusCode(HttpStatusCode statusCode)
  {
    return statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
  }

  private static HttpContent CreateJsonContent<T>(T value)
  {
    return new StringContent(
      JsonSerializer.Serialize(value),
      Encoding.UTF8,
      "application/json"
    );
  }

  private static string NormalizeRelaySimVersion(string? simVersionLabel, string fallback)
  {
    var normalized = string.IsNullOrWhiteSpace(simVersionLabel)
      ? fallback
      : simVersionLabel.Trim();
    if (normalized.Length <= 16)
    {
      return normalized;
    }
    return normalized[..16];
  }

  private static string? ReadRequiredStringProperty(JsonElement root, string propertyName)
  {
    if (!root.TryGetProperty(propertyName, out var element))
    {
      return null;
    }
    if (element.ValueKind != JsonValueKind.String)
    {
      return null;
    }
    var value = element.GetString()?.Trim();
    return string.IsNullOrWhiteSpace(value) ? null : value;
  }

  private static int? ReadOptionalIntProperty(JsonElement root, string propertyName)
  {
    if (!root.TryGetProperty(propertyName, out var element))
    {
      return null;
    }

    if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var numeric))
    {
      return numeric;
    }

    if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var parsed))
    {
      return parsed;
    }

    return null;
  }

  private static string TruncateForLog(string value, int maxLength)
  {
    var trimmed = value.Trim();
    if (trimmed.Length <= maxLength)
    {
      return trimmed;
    }
    return $"{trimmed[..maxLength]}...";
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

  private void RequestOwnshipStream()
  {
    lock (_gate)
    {
      if (_simConnect is null || _requestedOwnshipStream || !_simFlightActive)
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
    _logger.LogInformation("Waiting for active flight session (SimStart)...");
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
      RequestOwnshipStream();
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
    if (!_simFlightActive || data.dwRequestID != (uint)RequestId.Ownship || data.dwData.Length == 0)
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
