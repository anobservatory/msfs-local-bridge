using System.Globalization;
using System.Reflection;

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
  public string TlsPfxPath { get; }
  public string TlsPfxPassword { get; }
  public bool HasPfxCertificate => TlsPfxPath.Length > 0;
  public bool BootstrapEnabled { get; }
  public string BootstrapPath { get; }
  public string BootstrapHostIp { get; }
  public string BootstrapCaPath { get; }
  public int SampleIntervalMs { get; }
  public int SimConnectPollMs { get; }
  public int ReconnectDelayMs { get; }
  public int ReconnectMaxDelayMs { get; }
  public string DeviceName { get; }
  public string DeviceId { get; }
  public string CompanionVersion { get; }
  public string SimPlatform { get; }
  public string SimVersionFallback { get; }
  public string SimConnectMode { get; }
  public string SimConnectWorkerExecutablePath { get; }
  public string SimConnectWorkerArguments { get; }
  public bool UseWorkerSimConnect => string.Equals(SimConnectMode, "worker", StringComparison.OrdinalIgnoreCase);

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
    string tlsPfxPath,
    string tlsPfxPassword,
    bool bootstrapEnabled,
    string bootstrapPath,
    string bootstrapHostIp,
    string bootstrapCaPath,
    int sampleIntervalMs,
    int simConnectPollMs,
    int reconnectDelayMs,
    int reconnectMaxDelayMs,
    string deviceName,
    string deviceId,
    string companionVersion,
    string simPlatform,
    string simVersionFallback,
    string simConnectMode,
    string simConnectWorkerExecutablePath,
    string simConnectWorkerArguments
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
    TlsPfxPath = tlsPfxPath;
    TlsPfxPassword = tlsPfxPassword;
    BootstrapEnabled = bootstrapEnabled;
    BootstrapPath = bootstrapPath;
    BootstrapHostIp = bootstrapHostIp;
    BootstrapCaPath = bootstrapCaPath;
    SampleIntervalMs = sampleIntervalMs;
    SimConnectPollMs = simConnectPollMs;
    ReconnectDelayMs = reconnectDelayMs;
    ReconnectMaxDelayMs = reconnectMaxDelayMs;
    DeviceName = deviceName;
    DeviceId = deviceId;
    CompanionVersion = companionVersion;
    SimPlatform = simPlatform;
    SimVersionFallback = simVersionFallback;
    SimConnectMode = simConnectMode;
    SimConnectWorkerExecutablePath = simConnectWorkerExecutablePath;
    SimConnectWorkerArguments = simConnectWorkerArguments;
  }

  public static BridgeOptions FromEnvironment()
  {
    var bindHost = ReadString("MSFS_BRIDGE_BIND", "127.0.0.1");
    var port = ReadInt("MSFS_BRIDGE_PORT", fallback: 39000, min: 1025, max: 65535);
    var streamPath = NormalizePath(ReadString("MSFS_BRIDGE_PATH", "/stream"));
    var wssEnabled = ReadBool("MSFS_BRIDGE_WSS_ENABLED", false);
    var wssBindHost = ReadString("MSFS_BRIDGE_WSS_BIND", bindHost);
    var wssPort = ReadInt("MSFS_BRIDGE_WSS_PORT", fallback: 39002, min: 1025, max: 65535);
    var wssPublicHost = ReadString("MSFS_BRIDGE_PUBLIC_WSS_HOST", "ao.home.arpa");
    var tlsCertPath = ReadString("MSFS_BRIDGE_TLS_CERT_PATH", Path.Combine("certs", "ao.home.arpa.pem"));
    var tlsKeyPath = ReadString("MSFS_BRIDGE_TLS_KEY_PATH", Path.Combine("certs", "ao.home.arpa-key.pem"));
    var tlsPfxPath = ReadString("MSFS_BRIDGE_TLS_PFX_PATH", string.Empty);
    var tlsPfxPassword = ReadString("MSFS_BRIDGE_TLS_PFX_PASSWORD", "changeit");
    var bootstrapEnabled = ReadBool("MSFS_BRIDGE_BOOTSTRAP_ENABLED", true);
    var bootstrapPath = NormalizePath(ReadString("MSFS_BRIDGE_BOOTSTRAP_PATH", "/bootstrap"));
    var bootstrapHostIp = ReadString("MSFS_BRIDGE_BOOTSTRAP_HOST_IP", string.Empty);
    var bootstrapCaPath = ReadString("MSFS_BRIDGE_BOOTSTRAP_CA_PATH", Path.Combine("certs", "rootCA.pem"));
    var sampleIntervalMs = ReadInt("MSFS_BRIDGE_SAMPLE_MS", fallback: 200, min: 80, max: 2000);
    var simConnectPollMs = ReadInt("MSFS_BRIDGE_POLL_MS", fallback: 25, min: 5, max: 1000);
    var reconnectDelayMs = ReadInt("MSFS_BRIDGE_RECONNECT_MS", fallback: 2000, min: 500, max: 30000);
    var reconnectMaxDelayMsRaw = ReadInt("MSFS_BRIDGE_RECONNECT_MAX_MS", fallback: 10000, min: 1000, max: 120000);
    var reconnectMaxDelayMs = Math.Max(reconnectDelayMs, reconnectMaxDelayMsRaw);
    var deviceName = ReadString("MSFS_BRIDGE_DEVICE_NAME", Environment.MachineName);
    var companionVersion = ReadString("MSFS_BRIDGE_COMPANION_VERSION", ResolveCompanionVersion());
    var simPlatform = ReadString("MSFS_BRIDGE_SIM_PLATFORM", "msfs").ToLowerInvariant();
    var simVersionFallback = ReadString("MSFS_BRIDGE_SIM_VERSION_FALLBACK", "Local Bridge");
    var simConnectMode = ReadSimConnectMode("MSFS_BRIDGE_SIMCONNECT_MODE", "embedded");
    var simConnectWorkerExecutablePath = ReadString(
      "MSFS_BRIDGE_SIMCONNECT_WORKER_PATH",
      Path.Combine("workers", "simconnect-native", "dist", "msfs-simconnect-worker.exe")
    );
    var simConnectWorkerArguments = ReadString("MSFS_BRIDGE_SIMCONNECT_WORKER_ARGS", "--stdio-json");
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
      tlsPfxPath: tlsPfxPath,
      tlsPfxPassword: tlsPfxPassword,
      bootstrapEnabled: bootstrapEnabled,
      bootstrapPath: bootstrapPath,
      bootstrapHostIp: bootstrapHostIp,
      bootstrapCaPath: bootstrapCaPath,
      sampleIntervalMs: sampleIntervalMs,
      simConnectPollMs: simConnectPollMs,
      reconnectDelayMs: reconnectDelayMs,
      reconnectMaxDelayMs: reconnectMaxDelayMs,
      deviceName: deviceName,
      deviceId: deviceId,
      companionVersion: companionVersion,
      simPlatform: simPlatform,
      simVersionFallback: simVersionFallback,
      simConnectMode: simConnectMode,
      simConnectWorkerExecutablePath: simConnectWorkerExecutablePath,
      simConnectWorkerArguments: simConnectWorkerArguments
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

  private static string ReadSimConnectMode(string name, string fallback)
  {
    var mode = ReadString(name, fallback).Trim().ToLowerInvariant();
    return mode switch
    {
      "embedded" => "embedded",
      "worker" => "worker",
      _ => fallback,
    };
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
