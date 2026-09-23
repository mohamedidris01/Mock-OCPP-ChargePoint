using MockOcpp.Cli;
using MockOcpp.Model;
using MockOcpp.Protocol;

namespace MockOcpp.Sim;

/// <summary>
/// One running charge point: its transport, its state machine, a 1 Hz tick loop
/// and — in auto mode — a driver thread that cycles it through charging sessions.
/// </summary>
public sealed class ChargePointNode : IAsyncDisposable
{
    private readonly OcppConnection _conn;
    private readonly CliOptions _opt;
    private readonly CancellationTokenSource _cts = new();
    private Task? _tick;
    private Task? _driver;

    public ChargePoint Cp { get; }
    public string Id { get; }

    public ChargePointNode(string id, CliOptions opt, Action<string> log)
    {
        _opt = opt;
        Id = id;

        var connOpt = new OcppConnectionOptions
        {
            BasicAuthUser = opt.BasicAuthUser,
            BasicAuthPassword = opt.BasicAuthPassword,
            Trace = opt.Wire,
        };
        _conn = new OcppConnection(opt.Url, id, connOpt);
        _conn.Log += m => log($"[{id}] {m}");
        _conn.Trace += (outbound, text) =>
        {
            if (connOpt.Trace) log($"[{id}] {(outbound ? "→" : "←")} {text}");
        };

        var identity = new ChargePointIdentity
        {
            Id = id,
            Vendor = opt.Vendor,
            Model = opt.Model,
            FirmwareVersion = opt.Firmware,
            SerialNumber = id,
        };
        Cp = new ChargePoint(_conn, identity, opt.Connectors, opt.PowerW);
        Cp.Info += m => log($"[{id}] {m}");
        Cp.Config.Set("HeartbeatInterval", opt.HeartbeatInterval.ToString());
        Cp.Config.Set("MeterValueSampleInterval", opt.MeterInterval.ToString());
        foreach (var c in Cp.Connectors) c.Meter.TimeScale = opt.TimeScale;
    }

    public void ForceReconnect() => _conn.Reconnect();

    public void Start()
    {
        _conn.Start();
        _tick = Task.Run(() => TickLoopAsync(_cts.Token));
        if (_opt is { Auto: true, Passive: false })
            _driver = Task.Run(() => DriveLoopAsync(_cts.Token));
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Cp.TickAsync(); }
            catch { /* keep ticking */ }
            try { await Task.Delay(1000, ct); } catch { break; }
        }
    }

    private async Task DriveLoopAsync(CancellationToken ct)
    {
        var rng = new Random(Id.GetHashCode() ^ Environment.TickCount);
        while (!Cp.Booted && !ct.IsCancellationRequested)
            await Task.Delay(300, ct);

        // Each connector cycles on its own schedule, in parallel.
        var perConnector = Cp.Connectors
            .Where(c => c.Id > 0)
            .Select(c => Task.Run(() => CycleConnectorAsync(c.Id, new Random(rng.Next()), ct), ct))
            .ToArray();
        try { await Task.WhenAll(perConnector); }
        catch (OperationCanceledException) { }
    }

    private async Task CycleConnectorAsync(int connectorId, Random rng, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(rng.Next(3, 12)), ct);

                await Cp.PlugAsync(connectorId);
                await Task.Delay(1500, ct);
                await Cp.SetPowerAsync(connectorId, rng.Next(3, 22) * 1000);
                await Cp.PresentTagAsync(connectorId, _opt.IdTag);
                await Task.Delay(2000, ct);

                if (_opt.SuspendedEv)
                {
                    await Cp.SuspendAsync(connectorId, evSide: true, "auto");
                    await Task.Delay(TimeSpan.FromSeconds(rng.Next(8, 20)), ct);
                    await Cp.ResumeAsync(connectorId);
                }

                await Task.Delay(TimeSpan.FromSeconds(rng.Next(10, 30)), ct);
                await Cp.PresentTagAsync(connectorId, _opt.IdTag);   // same tag stops it
                await Task.Delay(1000, ct);
                await Cp.UnplugAsync(connectorId);
            }
            catch (OperationCanceledException) { break; }
            catch { try { await Task.Delay(2000, ct); } catch { break; } }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_tick is not null) { try { await _tick; } catch { } }
        if (_driver is not null) { try { await _driver; } catch { } }
        await _conn.DisposeAsync();
        _cts.Dispose();
    }
}
