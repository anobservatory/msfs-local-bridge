using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;

internal sealed class SimConnectOwnshipService : BackgroundService
{
  private readonly object _gate = new();
  private readonly ILogger<SimConnectOwnshipService> _logger;
  private readonly OwnshipSnapshotStore _snapshotStore;
  private readonly RouteDiagnosticsState _routeDiagnostics;
  private readonly BridgeOptions _options;

  private SimConnect? _simConnect;
  private AutoResetEvent? _messageSignal;
  private bool _requestedOwnshipStream;
  private bool _requestedOwnshipRouteStream;
  private bool _ownshipRouteDataEnabled;
  private bool _simFlightActive;
  private DateTimeOffset _lastConnectWarningAt = DateTimeOffset.MinValue;
  private volatile string? _simApplicationName;

  public SimConnectOwnshipService(
    ILogger<SimConnectOwnshipService> logger,
    OwnshipSnapshotStore snapshotStore,
    RouteDiagnosticsState routeDiagnostics,
    BridgeOptions options
  )
  {
    _logger = logger;
    _snapshotStore = snapshotStore;
    _routeDiagnostics = routeDiagnostics;
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
        TryRegisterOwnshipRouteDefinition(_simConnect);
        RegisterSystemEvents(_simConnect);
        _requestedOwnshipStream = false;
        _requestedOwnshipRouteStream = false;
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
    simConnect.AddToDataDefinition(DefinitionId.Ownship, "GPS GROUND TRUE TRACK", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
    simConnect.AddToDataDefinition(DefinitionId.Ownship, "VERTICAL SPEED", "feet per minute", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
    simConnect.AddToDataDefinition(DefinitionId.Ownship, "SIM ON GROUND", "bool", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
    simConnect.AddToDataDefinition(DefinitionId.Ownship, "TITLE", null, SIMCONNECT_DATATYPE.STRING256, 0f, SimConnect.SIMCONNECT_UNUSED);
    simConnect.AddToDataDefinition(DefinitionId.Ownship, "ATC MODEL", null, SIMCONNECT_DATATYPE.STRING64, 0f, SimConnect.SIMCONNECT_UNUSED);
    simConnect.AddToDataDefinition(DefinitionId.Ownship, "ATC ID", null, SIMCONNECT_DATATYPE.STRING64, 0f, SimConnect.SIMCONNECT_UNUSED);
    simConnect.AddToDataDefinition(DefinitionId.Ownship, "ATC AIRLINE", null, SIMCONNECT_DATATYPE.STRING64, 0f, SimConnect.SIMCONNECT_UNUSED);
    simConnect.AddToDataDefinition(DefinitionId.Ownship, "ATC FLIGHT NUMBER", null, SIMCONNECT_DATATYPE.STRING64, 0f, SimConnect.SIMCONNECT_UNUSED);
    simConnect.AddToDataDefinition(DefinitionId.Ownship, "TRANSPONDER CODE:1", "number", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
    simConnect.RegisterDataDefineStruct<OwnshipData>(DefinitionId.Ownship);
  }

  private void TryRegisterOwnshipRouteDefinition(SimConnect simConnect)
  {
    _routeDiagnostics.SetProvider("external_simconnect_fs9gps");
    try
    {
      simConnect.AddToDataDefinition(DefinitionId.OwnshipRoute, "C:fs9gps:FlightPlanDepartureAirportIdent", null, SIMCONNECT_DATATYPE.STRING64, 0f, SimConnect.SIMCONNECT_UNUSED);
      simConnect.AddToDataDefinition(DefinitionId.OwnshipRoute, "C:fs9gps:FlightPlanDestinationAirportIdent", null, SIMCONNECT_DATATYPE.STRING64, 0f, SimConnect.SIMCONNECT_UNUSED);
      simConnect.RegisterDataDefineStruct<OwnshipRouteData>(DefinitionId.OwnshipRoute);
      _ownshipRouteDataEnabled = true;
      _routeDiagnostics.SetSupported(true);
      _logger.LogInformation("Ownship route identifier stream enabled.");
    }
    catch (Exception ex)
    {
      _ownshipRouteDataEnabled = false;
      _routeDiagnostics.SetSupported(false, ex.Message);
      _logger.LogWarning(ex, "Ownship route identifier stream unavailable; origin/destination datablock display disabled.");
    }
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

      if (_ownshipRouteDataEnabled && !_requestedOwnshipRouteStream)
      {
        _simConnect.RequestDataOnSimObject(
          RequestId.OwnshipRoute,
          DefinitionId.OwnshipRoute,
          SimConnect.SIMCONNECT_OBJECT_ID_USER,
          SIMCONNECT_PERIOD.SECOND,
          0,
          0,
          0,
          0
        );

        _requestedOwnshipRouteStream = true;
        _routeDiagnostics.SetRequestActive(true);
        _logger.LogInformation("Ownship route identifier stream requested ({Reason}).", reason);
      }
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

      if (_ownshipRouteDataEnabled && _requestedOwnshipRouteStream)
      {
        _simConnect.RequestDataOnSimObject(
          RequestId.OwnshipRoute,
          DefinitionId.OwnshipRoute,
          SimConnect.SIMCONNECT_OBJECT_ID_USER,
          SIMCONNECT_PERIOD.NEVER,
          0,
          0,
          0,
          0
        );

        _requestedOwnshipRouteStream = false;
        _routeDiagnostics.SetRequestActive(false);
      }

      _snapshotStore.Clear();
      _routeDiagnostics.ClearRoute();
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
    if (data.dwData.Length == 0)
    {
      return;
    }

    if (data.dwRequestID == (uint)RequestId.OwnshipRoute)
    {
      var routeData = (OwnshipRouteData)data.dwData[0];
      var originAirportId = NormalizeAirportIdent(routeData.OriginAirportId);
      var destinationAirportId = NormalizeAirportIdent(routeData.DestinationAirportId);
      if (_snapshotStore.UpdateRoute(originAirportId, destinationAirportId, RouteDataSource.ExternalSimConnect))
      {
        _routeDiagnostics.SetProvider("external_simconnect_fs9gps");
        _routeDiagnostics.SetSupported(true);
        _routeDiagnostics.SetRequestActive(true);
        _routeDiagnostics.UpdateRoute(originAirportId, destinationAirportId);
      }
      return;
    }

    if (data.dwRequestID != (uint)RequestId.Ownship)
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
      TypeCode: NormalizeTypeCode(ownship.AtcModel),
      OriginAirportId: null,
      DestinationAirportId: null,
      Squawk: FormatSquawk(ownship.TransponderCode),
      SimVersionLabel: ResolveSimVersionLabel(_simApplicationName, _options.SimVersionFallback),
      Lat: ownship.LatitudeDeg,
      Lon: ownship.LongitudeDeg,
      AltBaroFt: ownship.IndicatedAltitudeFt,
      AltGeomFt: ownship.PlaneAltitudeFt,
      GsKt: Math.Max(0, ownship.GroundVelocityKt),
      HeadingDegTrue: NormalizeHeading(ownship.TrueHeadingDeg),
      TrackDegTrue: NormalizeHeading(ownship.GroundTrackDeg),
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
    _requestedOwnshipRouteStream = false;
    _ownshipRouteDataEnabled = false;
    _simFlightActive = false;
    _snapshotStore.Clear();
    _routeDiagnostics.ClearRoute();
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

  private static string? NormalizeAirportIdent(string? value)
  {
    var normalized = NormalizeText(value);
    if (normalized is null)
    {
      return null;
    }

    return normalized.ToUpperInvariant();
  }

  private static string? NormalizeTypeCode(string? value)
  {
    var normalized = NormalizeText(value);
    if (normalized is null)
    {
      return null;
    }

    var compact = new string(normalized.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    if (compact.Length is < 3 or > 5)
    {
      return null;
    }

    return compact == "MSFS" ? null : compact;
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
    OwnshipRoute = 2,
  }

  private enum RequestId : uint
  {
    Ownship = 1,
    OwnshipRoute = 2,
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
    public double GroundTrackDeg;
    public double VerticalSpeedFpm;
    public int SimOnGround;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string Title;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string AtcModel;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string AtcId;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string AtcAirline;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string AtcFlightNumber;

    public int TransponderCode;
  }

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
  private struct OwnshipRouteData
  {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string OriginAirportId;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string DestinationAirportId;
  }
}
