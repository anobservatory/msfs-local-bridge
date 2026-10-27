internal sealed class RouteDiagnosticsState
{
  private readonly object _gate = new();

  public string? Provider { get; private set; }
  public bool? Supported { get; private set; }
  public bool RequestActive { get; private set; }
  public long? LastRouteUpdateAtMs { get; private set; }
  public string? OriginAirportId { get; private set; }
  public string? DestinationAirportId { get; private set; }
  public string? LastError { get; private set; }

  public void SetProvider(string provider)
  {
    lock (_gate)
    {
      Provider = provider;
    }
  }

  public void SetSupported(bool supported, string? error = null)
  {
    lock (_gate)
    {
      Supported = supported;
      LastError = error;
      if (!supported)
      {
        RequestActive = false;
      }
    }
  }

  public void SetRequestActive(bool requestActive)
  {
    lock (_gate)
    {
      RequestActive = requestActive;
    }
  }

  public void UpdateRoute(string? originAirportId, string? destinationAirportId)
  {
    lock (_gate)
    {
      OriginAirportId = originAirportId;
      DestinationAirportId = destinationAirportId;
      LastRouteUpdateAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
  }

  public void ClearRoute()
  {
    lock (_gate)
    {
      RequestActive = false;
      OriginAirportId = null;
      DestinationAirportId = null;
      LastRouteUpdateAtMs = null;
    }
  }
}
