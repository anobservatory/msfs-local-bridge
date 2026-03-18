using System.Xml.Linq;

internal sealed class CustomFlightRouteService : BackgroundService
{
  private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
  private static readonly string[] CandidateProfileDirectories =
  {
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft Flight Simulator"),
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft Flight Simulator 2024"),
  };

  private readonly ILogger<CustomFlightRouteService> _logger;
  private readonly OwnshipSnapshotStore _snapshotStore;
  private readonly RouteDiagnosticsState _routeDiagnostics;
  private string? _resolvedCustomFlightDirectory;
  private string? _lastLoggedRoutePath;
  private DateTimeOffset _lastWarningAt = DateTimeOffset.MinValue;

  public CustomFlightRouteService(
    ILogger<CustomFlightRouteService> logger,
    OwnshipSnapshotStore snapshotStore,
    RouteDiagnosticsState routeDiagnostics
  )
  {
    _logger = logger;
    _snapshotStore = snapshotStore;
    _routeDiagnostics = routeDiagnostics;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    await Task.Yield();

    using var timer = new PeriodicTimer(PollInterval);
    while (await timer.WaitForNextTickAsync(stoppingToken))
    {
      try
      {
        if (!TryResolveRoute(out var route))
        {
          continue;
        }

        _routeDiagnostics.SetProvider("customflight_files");
        _routeDiagnostics.SetSupported(true);
        _routeDiagnostics.SetRequestActive(route.SourcePath is not null);

        var routeChanged = _snapshotStore.UpdateRoute(route.OriginAirportId, route.DestinationAirportId, RouteDataSource.CustomFlightFile);
        var diagnosticsChanged =
          string.Equals(_routeDiagnostics.Provider, "customflight_files", StringComparison.OrdinalIgnoreCase)
          && (
            !string.Equals(_routeDiagnostics.OriginAirportId, route.OriginAirportId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_routeDiagnostics.DestinationAirportId, route.DestinationAirportId, StringComparison.OrdinalIgnoreCase)
          );

        if (routeChanged || diagnosticsChanged)
        {
          _routeDiagnostics.UpdateRoute(route.OriginAirportId, route.DestinationAirportId);
        }

        if (!string.IsNullOrWhiteSpace(route.SourcePath)
            && !string.Equals(_lastLoggedRoutePath, route.SourcePath, StringComparison.OrdinalIgnoreCase))
        {
          _logger.LogInformation("CustomFlight route provider active (path={Path}).", route.SourcePath);
          _lastLoggedRoutePath = route.SourcePath;
        }
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        break;
      }
      catch (Exception ex)
      {
        if (DateTimeOffset.UtcNow - _lastWarningAt > TimeSpan.FromSeconds(15))
        {
          _logger.LogWarning(ex, "CustomFlight route polling failed.");
          _lastWarningAt = DateTimeOffset.UtcNow;
        }
      }
    }
  }

  private bool TryResolveRoute(out CustomFlightRoute route)
  {
    route = default;

    var customFlightDirectory = ResolveCustomFlightDirectory();
    if (customFlightDirectory is null)
    {
      return false;
    }

    var fltPath = Path.Combine(customFlightDirectory, "CustomFlight.FLT");
    var plnPath = Path.Combine(customFlightDirectory, "CUSTOMFLIGHT.PLN");

    var routeCandidates = new List<ParsedRouteFile>(capacity: 2);

    if (File.Exists(fltPath))
    {
      TryParseFltRoute(fltPath, out var fltOrigin, out var fltDestination);
      routeCandidates.Add(new ParsedRouteFile(
        fltPath,
        File.GetLastWriteTimeUtc(fltPath),
        fltOrigin,
        fltDestination
      ));
    }

    if (File.Exists(plnPath))
    {
      TryParsePlnRoute(plnPath, out var plnOrigin, out var plnDestination);
      routeCandidates.Add(new ParsedRouteFile(
        plnPath,
        File.GetLastWriteTimeUtc(plnPath),
        plnOrigin,
        plnDestination
      ));
    }

    if (routeCandidates.Count == 0)
    {
      route = new CustomFlightRoute(null, null, null);
      return true;
    }

    routeCandidates.Sort(static (left, right) => right.LastWriteUtc.CompareTo(left.LastWriteUtc));

    var primaryCandidate = routeCandidates[0];
    route = new CustomFlightRoute(
      primaryCandidate.OriginAirportId,
      primaryCandidate.DestinationAirportId,
      primaryCandidate.SourcePath
    );
    return true;
  }

  private string? ResolveCustomFlightDirectory()
  {
    if (!string.IsNullOrWhiteSpace(_resolvedCustomFlightDirectory) && Directory.Exists(_resolvedCustomFlightDirectory))
    {
      return _resolvedCustomFlightDirectory;
    }

    var explicitDirectory = Environment.GetEnvironmentVariable("MSFS_BRIDGE_CUSTOMFLIGHT_DIR");
    if (!string.IsNullOrWhiteSpace(explicitDirectory) && Directory.Exists(explicitDirectory))
    {
      _resolvedCustomFlightDirectory = explicitDirectory;
      return _resolvedCustomFlightDirectory;
    }

    foreach (var profileDirectory in CandidateProfileDirectories)
    {
      var candidate = Path.Combine(profileDirectory, "MISSIONS", "Custom", "CustomFlight");
      if (Directory.Exists(candidate))
      {
        _resolvedCustomFlightDirectory = candidate;
        return _resolvedCustomFlightDirectory;
      }
    }

    return null;
  }

  private static bool TryParseFltRoute(string path, out string? originAirportId, out string? destinationAirportId)
  {
    originAirportId = null;
    destinationAirportId = null;
    var requestedOriginAirportId = (string?)null;
    var requestedDestinationAirportId = (string?)null;
    var currentSection = string.Empty;

    foreach (var rawLine in File.ReadLines(path))
    {
      var line = rawLine.Trim();
      if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
      {
        continue;
      }

      if (line.StartsWith('[') && line.EndsWith(']'))
      {
        currentSection = line[1..^1];
        continue;
      }

      var equalsIndex = line.IndexOf('=');
      if (equalsIndex <= 0)
      {
        continue;
      }

      var key = line[..equalsIndex].Trim();
      var value = line[(equalsIndex + 1)..].Trim();

      if (currentSection.Equals("Departure", StringComparison.OrdinalIgnoreCase)
          && key.Equals("ICAO", StringComparison.OrdinalIgnoreCase))
      {
        originAirportId = NormalizeAirportIdent(value);
        continue;
      }

      if (currentSection.Equals("Arrival", StringComparison.OrdinalIgnoreCase)
          && key.Equals("ICAO", StringComparison.OrdinalIgnoreCase))
      {
        destinationAirportId = NormalizeAirportIdent(value);
        continue;
      }

      if (!currentSection.Equals("ATC_RequestedFlightPlan.0", StringComparison.OrdinalIgnoreCase)
          && !currentSection.Equals("ATC_ActiveFlightPlan.0", StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      if (key.Equals("departure_id", StringComparison.OrdinalIgnoreCase))
      {
        requestedOriginAirportId = ParseFlightPlanIdent(value);
        continue;
      }

      if (key.Equals("destination_id", StringComparison.OrdinalIgnoreCase))
      {
        requestedDestinationAirportId = ParseFlightPlanIdent(value);
      }
    }

    originAirportId ??= requestedOriginAirportId;
    destinationAirportId ??= requestedDestinationAirportId;
    return originAirportId is not null || destinationAirportId is not null;
  }

  private static bool TryParsePlnRoute(string path, out string? originAirportId, out string? destinationAirportId)
  {
    originAirportId = null;
    destinationAirportId = null;

    var document = XDocument.Load(path, LoadOptions.None);
    originAirportId = NormalizeAirportIdent(document.Descendants("DepartureID").FirstOrDefault()?.Value);
    destinationAirportId = NormalizeAirportIdent(document.Descendants("DestinationID").FirstOrDefault()?.Value);
    return originAirportId is not null || destinationAirportId is not null;
  }

  private static string? ParseFlightPlanIdent(string value)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return null;
    }

    var token = value.Split(',', 2)[0].Trim();
    return NormalizeAirportIdent(token);
  }

  private static string? NormalizeAirportIdent(string? value)
  {
    var trimmed = value?.Trim();
    return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed.ToUpperInvariant();
  }

  private readonly record struct CustomFlightRoute(
    string? OriginAirportId,
    string? DestinationAirportId,
    string? SourcePath
  );

  private readonly record struct ParsedRouteFile(
    string SourcePath,
    DateTime LastWriteUtc,
    string? OriginAirportId,
    string? DestinationAirportId
  );
}
