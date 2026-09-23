using MockOcpp.Model;
using MockOcpp.Protocol;
using MockOcpp.Sim;

namespace MockOcpp.Cli;

/// <summary>The interactive prompt. Mirrors the C++ simulator's REPL, extended
/// for multiple charge points and connectors.</summary>
public sealed class Repl
{
    private readonly IReadOnlyList<ChargePointNode> _nodes;
    private int _active;
    private int _connector = 1;

    public Repl(IReadOnlyList<ChargePointNode> nodes) => _nodes = nodes;

    private ChargePoint Cp => _nodes[_active].Cp;

    public async Task RunAsync(CancellationToken ct)
    {
        PrintHelp();
        while (!ct.IsCancellationRequested)
        {
            Console.Write($"cp[{_nodes[_active].Id}:{_connector}]> ");
            var line = await Console.In.ReadLineAsync(ct);
            if (line is null) break;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) continue;

            try
            {
                if (!await ExecuteAsync(parts)) break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  error: {ex.Message}");
            }
        }
    }

    private int Conn(string[] p, int idx) => idx < p.Length && int.TryParse(p[idx], out var v) ? v : _connector;

    private async Task<bool> ExecuteAsync(string[] p)
    {
        switch (p[0].ToLowerInvariant())
        {
            case "quit" or "exit":
                return false;

            case "help" or "?":
                PrintHelp();
                break;

            case "list":
                foreach (var n in _nodes)
                {
                    var flags = $"{(n.Cp.Connected ? "conn" : "----")} {(n.Cp.Booted ? "boot" : "----")}";
                    var conns = string.Join(", ", n.Cp.Connectors.Where(c => c.Id > 0)
                        .Select(c => $"{c.Id}:{c.Status}"));
                    Console.WriteLine($"  {n.Id,-10} {flags}  {conns}");
                }
                break;

            case "cp":
                if (p.Length > 1 && int.TryParse(p[1], out var i) && i >= 1 && i <= _nodes.Count)
                    _active = i - 1;
                else if (p.Length > 1)
                {
                    var found = -1;
                    for (var k = 0; k < _nodes.Count; k++)
                        if (_nodes[k].Id.Equals(p[1], StringComparison.OrdinalIgnoreCase)) found = k;
                    if (found >= 0) _active = found; else Console.WriteLine("  no such charge point");
                }
                Console.WriteLine($"  active: {_nodes[_active].Id}");
                break;

            case "connector" or "conn":
                if (p.Length > 1 && int.TryParse(p[1], out var cc)) _connector = cc;
                Console.WriteLine($"  active connector: {_connector}");
                break;

            case "status":
                PrintStatus();
                break;

            case "plug": await Cp.PlugAsync(Conn(p, 1)); break;
            case "unplug": await Cp.UnplugAsync(Conn(p, 1)); break;

            case "tag":
                if (p.Length < 2) { Console.WriteLine("  usage: tag <idTag> [connector]"); break; }
                await Cp.PresentTagAsync(Conn(p, 2), p[1]);
                break;

            case "remotestop":
                await Cp.StopTransactionAsync(Conn(p, 1), StopReason.Remote);
                break;

            case "fault":
                if (p.Length < 2 || !TryParseError(p[1], out var code))
                { Console.WriteLine("  codes: ground overcurrent overvoltage undervoltage temperature lock comms reader meter switch internal other"); break; }
                await Cp.FaultAsync(Conn(p, 2), code);
                break;

            case "clear":
                await Cp.ClearFaultAsync(Conn(p, 1));
                break;

            case "power":
                if (p.Length < 2) { Console.WriteLine("  usage: power <watts> [connector]"); break; }
                await Cp.SetPowerAsync(Conn(p, 2), double.Parse(p[1]));
                break;

            case "suspendev": await Cp.SuspendAsync(Conn(p, 1), evSide: true); break;
            case "suspendevse": await Cp.SuspendAsync(Conn(p, 1), evSide: false); break;
            case "resume": await Cp.ResumeAsync(Conn(p, 1)); break;

            case "availability" or "avail":
                if (p.Length < 2) { Console.WriteLine("  usage: avail <operative|inoperative> [connector]"); break; }
                await Cp.ApplyAvailabilityAsync(Conn(p, 2), p[1].StartsWith("op", StringComparison.OrdinalIgnoreCase));
                break;

            case "boot": await Cp.SendBootNotificationAsync(); break;
            case "heartbeat" or "hb": await Cp.SendHeartbeatAsync(); break;
            case "meter" or "mv":
                await Cp.SendStatusNotificationAsync(Conn(p, 1));
                break;

            case "datatransfer" or "dt":
                if (p.Length < 2) { Console.WriteLine("  usage: dt <vendorId> [messageId] [data]"); break; }
                var conf = await Cp.SendDataTransferAsync(p[1], p.ElementAtOrDefault(2), p.ElementAtOrDefault(3));
                Console.WriteLine($"  → {conf?.ToJsonString() ?? "(no reply)"}");
                break;

            case "get":
                foreach (var k in Cp.Config.All)
                    if (p.Length < 2 || k.Name.Contains(p[1], StringComparison.OrdinalIgnoreCase))
                        Console.WriteLine($"  {(k.Mutability == Mutability.ReadOnly ? "ro" : "rw")} {k.Name} = {k.Value}");
                break;

            case "set":
                if (p.Length < 3) { Console.WriteLine("  usage: set <key> <value>"); break; }
                Console.WriteLine($"  {p[1]} → {Cp.Config.Set(p[1], p[2])}");
                break;

            case "speed":
                var f = p.Length > 1 ? double.Parse(p[1]) : 1.0;
                foreach (var c in Cp.Connectors) { c.Meter.Settle(); c.Meter.TimeScale = f; }
                Console.WriteLine($"  meter time scale ×{f}");
                break;

            case "reconnect":
                Console.WriteLine("  forcing reconnect");
                _nodes[_active].ForceReconnect();
                break;

            default:
                Console.WriteLine($"  unknown command: {p[0]} (try `help`)");
                break;
        }
        return true;
    }

    private void PrintStatus()
    {
        var cp = Cp;
        Console.WriteLine($"  {_nodes[_active].Id}: link {(cp.Connected ? "connected" : "disconnected")}, boot {(cp.Booted ? "accepted" : "pending")}, clock {(cp.Clock.Synced ? cp.Clock.NowIso : "unsynced")}");
        foreach (var c in cp.Connectors.Where(c => c.Id > 0))
        {
            Console.WriteLine($"  connector {c.Id}: {c.Status}, error {c.ErrorCode}, {(c.Operative ? "operative" : "inoperative")}{(c.Plugged ? ", cable in" : "")}{(c.ReservationId is { } r ? $", reserved #{r}" : "")}");
            var m = c.Meter;
            Console.WriteLine($"    meter {m.EnergyWh} Wh, offering {m.PowerOfferedW:0} W, drawing {m.ActivePowerW:0} W, SoC {m.Soc:0}%");
            if (c.Transaction is { } tx)
                Console.WriteLine($"    transaction {(tx.HasId ? tx.Id.ToString() : "pending id")}, tag {tx.IdTag}, {tx.EnergyWh(m.EnergyWh)} Wh");
        }
    }

    private static bool TryParseError(string text, out ChargePointErrorCode code)
    {
        code = text.ToLowerInvariant() switch
        {
            "ground" => ChargePointErrorCode.GroundFailure,
            "overcurrent" => ChargePointErrorCode.OverCurrentFailure,
            "overvoltage" => ChargePointErrorCode.OverVoltage,
            "undervoltage" => ChargePointErrorCode.UnderVoltage,
            "temperature" => ChargePointErrorCode.HighTemperature,
            "lock" => ChargePointErrorCode.ConnectorLockFailure,
            "comms" => ChargePointErrorCode.EVCommunicationError,
            "reader" => ChargePointErrorCode.ReaderFailure,
            "meter" => ChargePointErrorCode.PowerMeterFailure,
            "switch" => ChargePointErrorCode.PowerSwitchFailure,
            "internal" => ChargePointErrorCode.InternalError,
            "other" => ChargePointErrorCode.OtherError,
            "none" => ChargePointErrorCode.NoError,
            _ => (ChargePointErrorCode)(-1),
        };
        return (int)code >= 0;
    }

    private static void PrintHelp() => Console.WriteLine("""

    Commands
      list                        every charge point and its connectors
      cp <n|id>                   select the charge point commands act on
      connector <n>               select the connector commands default to
      status                      link / boot / connector / transaction state
      plug|unplug [conn]          cable in / out
      tag <idTag> [conn]          present an RFID tag (same tag again = stop)
      remotestop [conn]           stop the active transaction
      fault <code> [conn]         raise a fault   (clear with `clear`)
      clear [conn]                clear the fault
      power <watts> [conn]        power the EVSE offers (SetChargingProfile also sets this)
      suspendev [conn]            → SuspendedEV   (car stops drawing)
      suspendevse [conn]          → SuspendedEVSE (station withholds)
      resume [conn]               back to Charging
      avail <op|inop> [conn]      ChangeAvailability, locally
      boot | heartbeat | meter    send that message now
      dt <vendorId> [msgId] [data]  send a DataTransfer
      get [filter] | set <k> <v>  configuration keys
      speed <factor>              compress simulated time
      help | quit

    """);
}
