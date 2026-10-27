internal sealed class OwnshipSnapshotStore
{
  private readonly object _gate = new();
  private OwnshipSnapshot? _latest;
  private string? _originAirportId;
  private string? _destinationAirportId;
  private RouteDataSource _routeDataSource = RouteDataSource.None;

  public void Write(OwnshipSnapshot snapshot)
  {
    lock (_gate)
    {
      _latest = snapshot with
      {
        OriginAirportId = _originAirportId,
        DestinationAirportId = _destinationAirportId,
      };
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
      _originAirportId = null;
      _destinationAirportId = null;
      _routeDataSource = RouteDataSource.None;
    }
  }

  public bool UpdateRoute(string? originAirportId, string? destinationAirportId, RouteDataSource source)
  {
    lock (_gate)
    {
      var hasCandidate = !string.IsNullOrWhiteSpace(originAirportId) || !string.IsNullOrWhiteSpace(destinationAirportId);
      var hasCurrent = !string.IsNullOrWhiteSpace(_originAirportId) || !string.IsNullOrWhiteSpace(_destinationAirportId);

      if (source < _routeDataSource && hasCurrent)
      {
        return false;
      }

      if (!hasCandidate && source > _routeDataSource && hasCurrent)
      {
        return false;
      }

      _routeDataSource = hasCandidate || !hasCurrent
        ? source
        : _routeDataSource;
      _originAirportId = originAirportId;
      _destinationAirportId = destinationAirportId;

      if (_latest is OwnshipSnapshot latest)
      {
        _latest = latest with
        {
          OriginAirportId = originAirportId,
          DestinationAirportId = destinationAirportId,
        };
      }

      return true;
    }
  }
}
