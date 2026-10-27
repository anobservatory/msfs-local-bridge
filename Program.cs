using System.Security.Cryptography.X509Certificates;
using static BridgeAddresses;

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
builder.Services.AddSingleton<RouteDiagnosticsState>();
builder.Services.AddHostedService<CustomFlightRouteService>();
if (bridgeOptions.UseWorkerSimConnect)
{
  builder.Services.AddHostedService<SimConnectWorkerOwnshipService>();
}
else
{
  builder.Services.AddHostedService<SimConnectOwnshipService>();
}

if (bridgeOptions.WssEnabled)
{
  wssCertificate = BridgeTls.LoadWssCertificate(bridgeOptions);
  builder.WebHost.ConfigureKestrel(serverOptions =>
  {
    serverOptions.ConfigureHttpsDefaults(httpsOptions =>
    {
      httpsOptions.ServerCertificate = wssCertificate;
    });
  });
}

var listenUrls = new List<string>
{
  $"http://{bridgeOptions.BindHost}:{bridgeOptions.Port}",
};

if (bridgeOptions.WssEnabled)
{
  listenUrls.Add($"https://{bridgeOptions.WssBindHost}:{bridgeOptions.WssPort}");
}

builder.WebHost.UseUrls(listenUrls.ToArray());

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions
{
  KeepAliveInterval = TimeSpan.FromSeconds(15),
});

HealthEndpoints.Map(app, bridgeOptions);
BootstrapEndpoints.Map(app, bridgeOptions);
TelemetryEndpoints.Map(app, bridgeOptions);

app.Lifetime.ApplicationStarted.Register(() =>
{
  app.Logger.LogInformation("SimConnect ingestion mode: {Mode}", bridgeOptions.SimConnectMode);
  if (bridgeOptions.UseWorkerSimConnect)
  {
    app.Logger.LogInformation(
      "SimConnect worker path: {WorkerPath}",
      bridgeOptions.SimConnectWorkerExecutablePath
    );
  }
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
  if (bridgeOptions.BootstrapEnabled)
  {
    app.Logger.LogInformation(
      "Listener bootstrap available at {BootstrapUrl}",
      BuildBootstrapBaseUrl(bridgeOptions)
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
