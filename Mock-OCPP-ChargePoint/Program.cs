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

// Build the fleet.
var nodes = new List<ChargePointNode>();
var width = Math.Max(4, opt.Count.ToString().Length);
for (var i = 1; i <= opt.Count; i++)
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
