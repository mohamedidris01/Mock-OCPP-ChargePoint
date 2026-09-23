namespace MockOcpp.Cli;

public sealed class CliOptions
{
    public string Url { get; set; } = "ws://127.0.0.1:9000/ocpp";
    public string IdPrefix { get; set; } = "CP";
    public string? ExplicitId { get; set; }
    public int Count { get; set; } = 1;
    public int Connectors { get; set; } = 1;
    public double PowerW { get; set; } = 7400;

    public string Vendor { get; set; } = "MockOCPP";
    public string Model { get; set; } = "CS-1";
    public string Firmware { get; set; } = "1.0.0-mock";

    public int HeartbeatInterval { get; set; } = 300;
    public int MeterInterval { get; set; } = 30;

    public string? BasicAuthUser { get; set; }
    public string? BasicAuthPassword { get; set; }

    public string IdTag { get; set; } = "MOCKTAG01";
    public bool Auto { get; set; }
    public bool SuspendedEv { get; set; }
    public bool Passive { get; set; }
    public bool Wire { get; set; }
    public double TimeScale { get; set; } = 1.0;

    public bool ShowHelp { get; set; }
    public string? ParseError { get; set; }

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new FormatException($"{a} needs a value");
            try
            {
                switch (a)
                {
                    case "--url" or "-u": o.Url = Next(); break;
                    case "--id": o.ExplicitId = Next(); break;
                    case "--id-prefix": o.IdPrefix = Next(); break;
                    case "--count" or "-n": o.Count = int.Parse(Next()); break;
                    case "--connectors" or "-c": o.Connectors = int.Parse(Next()); break;
                    case "--power" or "-p": o.PowerW = double.Parse(Next()); break;
                    case "--vendor": o.Vendor = Next(); break;
                    case "--model": o.Model = Next(); break;
                    case "--firmware": o.Firmware = Next(); break;
                    case "--heartbeat": o.HeartbeatInterval = int.Parse(Next()); break;
                    case "--meter-interval": o.MeterInterval = int.Parse(Next()); break;
                    case "--user": o.BasicAuthUser = Next(); break;
                    case "--pass": o.BasicAuthPassword = Next(); break;
                    case "--tag": o.IdTag = Next(); break;
                    case "--auto": o.Auto = true; break;
                    case "--suspended-ev": o.SuspendedEv = true; o.Auto = true; break;
                    case "--passive": o.Passive = true; break;
                    case "--wire": o.Wire = true; break;
                    case "--speed": o.TimeScale = double.Parse(Next()); break;
                    case "--help" or "-h": o.ShowHelp = true; break;
                    default: o.ParseError = $"unknown option: {a}"; return o;
                }
            }
            catch (Exception ex)
            {
                o.ParseError = ex.Message;
                return o;
            }
        }

        if (o.Count < 1) o.ParseError = "--count must be >= 1";
        if (o.Connectors < 1) o.ParseError = "--connectors must be >= 1";
        return o;
    }

    public const string Usage = """
    mock-ocpp-cp — a mock OCPP 1.6J charge point

      --url, -u <ws://host:port/path>  CSMS base URL; charge point id is appended
                                      (default ws://127.0.0.1:9000/ocpp)
      --id <id>                       exact charge point id (single unit only)
      --id-prefix <prefix>            id prefix for a fleet (default CP → CP0001…)
      --count, -n <n>                 number of charge points to run (default 1)
      --connectors, -c <n>            connectors per charge point (default 1)
      --power, -p <watts>             power the EVSE offers per connector (default 7400)
      --vendor / --model / --firmware  BootNotification identity fields
      --heartbeat <seconds>          initial HeartbeatInterval (default 300)
      --meter-interval <seconds>     MeterValueSampleInterval (default 30)
      --user <u> --pass <p>          HTTP Basic auth for the WebSocket upgrade
      --tag <idTag>                  idTag presented by auto sessions (default MOCKTAG01)
      --auto                         auto-cycle sessions: plug → tag → charge → stop
      --suspended-ev                 auto mode, but every session parks in SuspendedEV
      --passive                      connect, boot and heartbeat only; wait for the CSMS
      --speed <factor>               compress simulated time (10 = 10× meter speed)
      --wire                         print raw JSON frames
      --help, -h

    With a single unit the interactive prompt is available (type `help` at cp>).
    In fleet or auto mode the prompt still works — use `cp <n>` to target a unit.
    """;
}
