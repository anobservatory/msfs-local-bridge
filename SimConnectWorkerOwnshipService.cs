using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

internal sealed class SimConnectWorkerOwnshipService : BackgroundService
{
  private readonly ILogger<SimConnectWorkerOwnshipService> _logger;
  private readonly OwnshipSnapshotStore _snapshotStore;
  private readonly BridgeOptions _options;
  private DateTimeOffset _lastWorkerWarningAt = DateTimeOffset.MinValue;

  public SimConnectWorkerOwnshipService(
    ILogger<SimConnectWorkerOwnshipService> logger,
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
    await Task.Yield();

    var restartDelayMs = _options.ReconnectDelayMs;

    while (!stoppingToken.IsCancellationRequested)
    {
      var workerPath = ResolveWorkerExecutablePath(_options.SimConnectWorkerExecutablePath);
      if (!File.Exists(workerPath))
      {
        LogWorkerUnavailable(workerPath, "Worker executable not found.");
        await Task.Delay(restartDelayMs, stoppingToken);
        restartDelayMs = Math.Min(_options.ReconnectMaxDelayMs, restartDelayMs * 2);
        continue;
      }

      using var process = CreateWorkerProcess(workerPath);

      try
      {
        process.Start();
      }
      catch (Exception ex)
      {
        LogWorkerUnavailable(workerPath, $"Worker launch failed: {ex.Message}");
        await Task.Delay(restartDelayMs, stoppingToken);
        restartDelayMs = Math.Min(_options.ReconnectMaxDelayMs, restartDelayMs * 2);
        continue;
      }

      _logger.LogInformation("SimConnect worker started (path={WorkerPath}).", workerPath);
      restartDelayMs = _options.ReconnectDelayMs;

      try
      {
        var stdoutTask = PumpStandardOutputAsync(process.StandardOutput, stoppingToken);
        var stderrTask = PumpStandardErrorAsync(process.StandardError, stoppingToken);
        await process.WaitForExitAsync(stoppingToken);
        await Task.WhenAll(stdoutTask, stderrTask);
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        TryTerminateProcess(process);
        break;
      }
      finally
      {
        _snapshotStore.Clear();
      }

      if (!stoppingToken.IsCancellationRequested)
      {
        _logger.LogWarning(
          "SimConnect worker exited with code {ExitCode}. Restarting in {DelayMs}ms.",
          process.ExitCode,
          restartDelayMs
        );
        await Task.Delay(restartDelayMs, stoppingToken);
        restartDelayMs = Math.Min(_options.ReconnectMaxDelayMs, restartDelayMs * 2);
      }
    }
  }

  private Process CreateWorkerProcess(string workerPath)
  {
    var startInfo = new ProcessStartInfo
    {
      FileName = workerPath,
      Arguments = _options.SimConnectWorkerArguments,
      WorkingDirectory = Path.GetDirectoryName(workerPath) ?? AppContext.BaseDirectory,
      UseShellExecute = false,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      CreateNoWindow = true,
    };

    startInfo.Environment["MSFS_BRIDGE_SAMPLE_MS"] = _options.SampleIntervalMs.ToString(CultureInfo.InvariantCulture);
    startInfo.Environment["MSFS_BRIDGE_POLL_MS"] = _options.SimConnectPollMs.ToString(CultureInfo.InvariantCulture);
    startInfo.Environment["MSFS_BRIDGE_SIM_VERSION_FALLBACK"] = _options.SimVersionFallback;
    startInfo.Environment["MSFS_BRIDGE_DEVICE_ID"] = _options.DeviceId;
    startInfo.Environment["MSFS_BRIDGE_DEVICE_NAME"] = _options.DeviceName;

    return new Process
    {
      StartInfo = startInfo,
      EnableRaisingEvents = true,
    };
  }

  private async Task PumpStandardOutputAsync(StreamReader reader, CancellationToken cancellationToken)
  {
    while (!cancellationToken.IsCancellationRequested)
    {
      var line = await reader.ReadLineAsync(cancellationToken);
      if (line is null)
      {
        break;
      }

      HandleWorkerMessage(line);
    }
  }

  private async Task PumpStandardErrorAsync(StreamReader reader, CancellationToken cancellationToken)
  {
    while (!cancellationToken.IsCancellationRequested)
    {
      var line = await reader.ReadLineAsync(cancellationToken);
      if (line is null)
      {
        break;
      }

      if (!string.IsNullOrWhiteSpace(line))
      {
        _logger.LogWarning("SimConnect worker stderr: {Line}", line.Trim());
      }
    }
  }

  private void HandleWorkerMessage(string line)
  {
    if (string.IsNullOrWhiteSpace(line))
    {
      return;
    }

    try
    {
      using var document = JsonDocument.Parse(line);
      var root = document.RootElement;
      var type = ReadRequiredString(root, "type");

      switch (type)
      {
        case "telemetry":
          var snapshotElement = root.TryGetProperty("snapshot", out var nestedSnapshot)
            ? nestedSnapshot
            : root;
          if (TryParseSnapshot(snapshotElement, out var snapshot))
          {
            _snapshotStore.Write(snapshot);
          }
          break;
        case "status":
          LogWorkerStatus(root);
          break;
        case "warning":
          _logger.LogWarning("SimConnect worker: {Message}", ReadRequiredString(root, "message"));
          break;
        case "error":
          _logger.LogError("SimConnect worker: {Message}", ReadRequiredString(root, "message"));
          break;
        default:
          _logger.LogDebug("Ignoring unknown SimConnect worker message type '{Type}'.", type);
          break;
      }
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to parse SimConnect worker message: {Line}", line);
    }
  }

  private void LogWorkerStatus(JsonElement root)
  {
    var state = ReadOptionalString(root, "state") ?? "unknown";
    var message = ReadOptionalString(root, "message") ?? string.Empty;

    if (string.Equals(state, "ready", StringComparison.OrdinalIgnoreCase))
    {
      _logger.LogInformation("SimConnect worker ready. {Message}", message);
      return;
    }

    if (string.Equals(state, "not_implemented", StringComparison.OrdinalIgnoreCase))
    {
      _logger.LogWarning("SimConnect worker skeleton active. {Message}", message);
      return;
    }

    if (string.Equals(state, "waiting_for_sim", StringComparison.OrdinalIgnoreCase))
    {
      _logger.LogWarning("SimConnect worker waiting for simulator. {Message}", message);
      return;
    }

    _logger.LogInformation("SimConnect worker status {State}. {Message}", state, message);
  }

  private bool TryParseSnapshot(JsonElement snapshotElement, out OwnshipSnapshot snapshot)
  {
    var id = ReadOptionalString(snapshotElement, "id") ?? _options.DeviceId;
    var simVersionLabel = ReadOptionalString(snapshotElement, "simVersionLabel") ?? _options.SimVersionFallback;
    var lat = ReadDouble(snapshotElement, "lat");
    var lon = ReadDouble(snapshotElement, "lon");
    var altBaroFt = ReadDouble(snapshotElement, "altBaroFt");
    var altGeomFt = ReadDouble(snapshotElement, "altGeomFt");
    var gsKt = ReadDouble(snapshotElement, "gsKt");
    var headingDegTrue = ReadDouble(snapshotElement, "headingDegTrue");
    var trackDegTrue = ReadDouble(snapshotElement, "trackDegTrue");
    var vsFpm = ReadDouble(snapshotElement, "vsFpm");
    var onGround = ReadBool(snapshotElement, "onGround");
    var timestampMs = ReadLong(snapshotElement, "timestampMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    snapshot = new OwnshipSnapshot(
      Id: id,
      Callsign: ReadOptionalString(snapshotElement, "callsign"),
      TailNumber: ReadOptionalString(snapshotElement, "tailNumber"),
      AircraftTitle: ReadOptionalString(snapshotElement, "aircraftTitle"),
      Squawk: ReadOptionalString(snapshotElement, "squawk"),
      SimVersionLabel: simVersionLabel,
      Lat: lat,
      Lon: lon,
      AltBaroFt: altBaroFt,
      AltGeomFt: altGeomFt,
      GsKt: gsKt,
      HeadingDegTrue: headingDegTrue,
      TrackDegTrue: trackDegTrue,
      VsFpm: vsFpm,
      OnGround: onGround,
      TimestampMs: timestampMs
    );

    return true;
  }

  private static string ResolveWorkerExecutablePath(string configuredPath)
  {
    if (Path.IsPathRooted(configuredPath))
    {
      return Path.GetFullPath(configuredPath);
    }

    var candidates = new List<string>();
    var baseDirectory = AppContext.BaseDirectory;
    candidates.Add(Path.GetFullPath(Path.Combine(baseDirectory, configuredPath)));

    var current = new DirectoryInfo(baseDirectory);
    for (var depth = 0; depth < 6 && current is not null; depth++)
    {
      candidates.Add(Path.GetFullPath(Path.Combine(current.FullName, configuredPath)));
      current = current.Parent;
    }

    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var candidate in candidates)
    {
      if (!seen.Add(candidate))
      {
        continue;
      }

      if (File.Exists(candidate))
      {
        return candidate;
      }
    }

    return candidates[0];
  }

  private void LogWorkerUnavailable(string workerPath, string reason)
  {
    if (DateTimeOffset.UtcNow - _lastWorkerWarningAt <= TimeSpan.FromSeconds(10))
    {
      return;
    }

    _logger.LogWarning(
      "SimConnect worker unavailable (mode=worker, path={WorkerPath}). {Reason}",
      workerPath,
      reason
    );
    _lastWorkerWarningAt = DateTimeOffset.UtcNow;
  }

  private static void TryTerminateProcess(Process process)
  {
    try
    {
      if (!process.HasExited)
      {
        process.Kill(entireProcessTree: true);
      }
    }
    catch
    {
      // Best effort during shutdown.
    }
  }

  private static string ReadRequiredString(JsonElement element, string propertyName)
  {
    return ReadOptionalString(element, propertyName) ?? string.Empty;
  }

  private static string? ReadOptionalString(JsonElement element, string propertyName)
  {
    if (!element.TryGetProperty(propertyName, out var property))
    {
      return null;
    }

    return property.ValueKind switch
    {
      JsonValueKind.String => property.GetString(),
      JsonValueKind.Number => property.GetRawText(),
      JsonValueKind.True => bool.TrueString,
      JsonValueKind.False => bool.FalseString,
      _ => null,
    };
  }

  private static double ReadDouble(JsonElement element, string propertyName)
  {
    if (!element.TryGetProperty(propertyName, out var property))
    {
      return 0;
    }

    if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var numeric))
    {
      return numeric;
    }

    if (property.ValueKind == JsonValueKind.String && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
    {
      return parsed;
    }

    return 0;
  }

  private static long ReadLong(JsonElement element, string propertyName, long fallback)
  {
    if (!element.TryGetProperty(propertyName, out var property))
    {
      return fallback;
    }

    if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var numeric))
    {
      return numeric;
    }

    if (property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
    {
      return parsed;
    }

    return fallback;
  }

  private static bool ReadBool(JsonElement element, string propertyName)
  {
    if (!element.TryGetProperty(propertyName, out var property))
    {
      return false;
    }

    return property.ValueKind switch
    {
      JsonValueKind.True => true,
      JsonValueKind.False => false,
      JsonValueKind.Number => property.TryGetInt32(out var numeric) && numeric != 0,
      JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) && parsed,
      _ => false,
    };
  }
}

