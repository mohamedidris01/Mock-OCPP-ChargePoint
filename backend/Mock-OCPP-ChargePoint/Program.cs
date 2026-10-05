using MockOcpp.Api;
using MockOcpp.Cli;
using MockOcpp.Sim;

var opt = CliOptions.Parse(args);
if (opt.ShowHelp)
{
    Console.WriteLine(CliOptions.Usage);
    return 0;
}
if (opt.ParseError is not null)
{
    Console.Error.WriteLine(opt.ParseError);
    Console.Error.WriteLine();
    Console.Error.WriteLine(CliOptions.Usage);
    return 1;
}

var logLock = new object();
void Log(string message)
{
    lock (logLock) Console.WriteLine(message);
}

// Web mode: serve the HTTP API (+ the built Angular UI from wwwroot, if present).
// The fleet starts empty and is created from the UI; the console prompt is not used.
if (opt.HttpPort is { } httpPort)
{
    // wwwroot is looked up next to the working directory first (dotnet run), then the binaries.
    var contentRoot = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }
        .FirstOrDefault(d => Directory.Exists(Path.Combine(d, "wwwroot"))) ?? Directory.GetCurrentDirectory();

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = contentRoot });
    builder.WebHost.UseUrls($"http://localhost:{httpPort}");
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Services.ConfigureHttpJsonOptions(o =>
    {
        foreach (var c in ApiEndpoints.Json.Converters) o.SerializerOptions.Converters.Add(c);
    });
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
        .WithOrigins("http://localhost:4200", "http://127.0.0.1:4200")
        .AllowAnyHeader().AllowAnyMethod()));

    var hub = new EventHub();
    await using var fleet = new Fleet(hub, Log);

    var app = builder.Build();
    app.UseCors();
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapApi(fleet, hub);
    app.MapFallbackToFile("index.html");

    if (opt.UrlSpecified)
    {
        var pre = opt.Clone();
        await fleet.ConnectAsync(pre, replace: false);
    }

    Log($"web UI + API on http://localhost:{httpPort}  (Ctrl+C to stop)");
    await app.RunAsync();
    return 0;
}

// Build the fleet.
var nodes = new List<ChargePointNode>();
var width = Math.Max(4, (opt.First + opt.Count - 1).ToString().Length);
for (var i = opt.First; i < opt.First + opt.Count; i++)
{
    var id = opt.Count == 1 && opt.ExplicitId is not null
        ? opt.ExplicitId
        : opt.IdPrefix + i.ToString().PadLeft(width, '0');
    nodes.Add(new ChargePointNode(id, opt, Log));
}

Log($"starting {nodes.Count} charge point(s) → {opt.Url}");
Log($"  {opt.Connectors} connector(s) each, {opt.PowerW:0} W" +
    (opt.Auto ? ", auto-cycling sessions" : "") +
    (opt.SuspendedEv ? " (SuspendedEV)" : "") +
    (opt.Passive ? ", passive" : ""));

foreach (var n in nodes)
{
    n.Start();
    await Task.Delay(100);   // stagger the socket opens
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };

// The prompt runs whenever stdin is a console or a script; it exits on `quit`,
// EOF or Ctrl+C. After EOF we fall through to a headless summary loop so a
// piped-in command file doesn't end the process.
var repl = new Repl(nodes);
try { await repl.RunAsync(shutdown.Token); }
catch (OperationCanceledException) { }

if (!shutdown.IsCancellationRequested)
{
    Log("input closed — running headless until Ctrl+C");
    while (!shutdown.IsCancellationRequested)
    {
        try { await Task.Delay(5000, shutdown.Token); } catch { break; }
        var connected = nodes.Count(n => n.Cp.Connected);
        var charging = nodes.Sum(n => n.Cp.Connectors.Count(c => c.Id > 0 && c.InTransaction));
        var wh = nodes.Sum(n => (long)n.Cp.Connectors.Where(c => c.Id > 0).Sum(c => c.Meter.EnergyWh));
        Log($"[summary] {connected}/{nodes.Count} connected, {charging} charging, {wh} Wh delivered");
    }
}

Log("shutting down…");
foreach (var n in nodes)
    await n.DisposeAsync();
return 0;
