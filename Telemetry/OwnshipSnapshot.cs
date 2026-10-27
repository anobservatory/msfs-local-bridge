internal readonly record struct OwnshipSnapshot(
  string Id,
  string? Callsign,
  string? TailNumber,
    string? AircraftTitle,
    string? TypeCode,
  string? OriginAirportId,
  string? DestinationAirportId,
  string? Squawk,
  string SimVersionLabel,
  double Lat,
  double Lon,
  double AltBaroFt,
  double AltGeomFt,
  double GsKt,
  double HeadingDegTrue,
  double TrackDegTrue,
  double VsFpm,
  bool OnGround,
  long TimestampMs
);

internal enum RouteDataSource
{
  None = 0,
  CustomFlightFile = 1,
  ExternalSimConnect = 2,
  WorkerStream = 2,
}
