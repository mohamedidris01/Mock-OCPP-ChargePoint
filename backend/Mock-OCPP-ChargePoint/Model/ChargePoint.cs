using System.Text.Json.Nodes;
using MockOcpp.Protocol;

namespace MockOcpp.Model;

public sealed class ChargePointIdentity
{
    public string Id { get; init; } = "CP0001";
    public string Vendor { get; init; } = "MockOCPP";
    public string Model { get; init; } = "CS-1";
    public string FirmwareVersion { get; init; } = "1.0.0-mock";
    public string? SerialNumber { get; init; }
    public string? Iccid { get; init; }
    public string? Imsi { get; init; }
}

/// <summary>
/// The application layer for one charge point: owns the connectors, drives the
/// boot handshake, and turns local events (plug, tag, fault, suspend) into OCPP
/// messages. All state changes are serialised through <see cref="_brain"/>.
/// </summary>
public sealed partial class ChargePoint
{
    private readonly OcppConnection _conn;
    private readonly ChargePointIdentity _identity;
    private readonly SemaphoreSlim _brain = new(1, 1);
    private readonly List<Connector> _connectors = new();
    private readonly Random _rng = new();

    public OcppConfiguration Config { get; }
    public OcppClock Clock { get; } = new();
    public LocalAuthList AuthList { get; } = new();
    public ChargingProfileStore Profiles { get; } = new();

    public bool Booted { get; private set; }
    public bool Connected => _conn.IsConnected;
    public string Id => _identity.Id;
    public IReadOnlyList<Connector> Connectors => _connectors;

    // Completes when BootNotification is Accepted; reset on every disconnect.
    // Session messages (Authorize/Start/Stop/Status/Meter) wait on it — the
    // spec forbids sending anything but BootNotification until boot is accepted.
    private volatile TaskCompletionSource _bootGate =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private DateTimeOffset _lastHeartbeat = DateTimeOffset.MinValue;
    private DateTimeOffset _lastMeterSample = DateTimeOffset.MinValue;
    private DateTimeOffset _nextBootAttempt = DateTimeOffset.MinValue;

    public event Action<string>? Info;

    public ChargePoint(OcppConnection conn, ChargePointIdentity identity, int connectorCount, double powerW)
    {
        _conn = conn;
        _identity = identity;
        Config = new OcppConfiguration(connectorCount);

        for (var i = 0; i <= connectorCount; i++)
            _connectors.Add(new Connector(i, powerW));

        Config.Changed += (key, value) =>
        {
            if (key == "MeterValueSampleInterval" || key == "HeartbeatInterval")
                _lastHeartbeat = _lastMeterSample = Clock.Now;
        };

        _conn.ConnectionChanged += OnConnectionChanged;
        _conn.OnCall = HandleCsCallAsync;
    }

    public Connector? Connector(int id) => id >= 0 && id < _connectors.Count ? _connectors[id] : null;

    private void Emit(string message) => Info?.Invoke(message);

    private void OnConnectionChanged(bool connected)
    {
        if (connected)
        {
            _nextBootAttempt = DateTimeOffset.MinValue;   // re-boot on every new socket
        }
        else
        {
            Booted = false;
            if (_bootGate.Task.IsCompleted)
                _bootGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Await boot acceptance before sending a session message.</summary>
    private Task WaitBootedAsync() => _bootGate.Task;

    // --- periodic tick (call ~1 Hz) --------------------------------------

    public async Task TickAsync()
    {
        if (!_conn.IsConnected) return;
        var now = Clock.Now;

        if (!Booted)
        {
            if (now >= _nextBootAttempt)
            {
                _nextBootAttempt = now.AddSeconds(15);
                await SendBootNotificationAsync();
            }
            return;
        }

        var hbInterval = Config.GetInt("HeartbeatInterval", 300);
        if (hbInterval > 0 && (now - _lastHeartbeat).TotalSeconds >= hbInterval)
        {
            _lastHeartbeat = now;
            _ = SendHeartbeatAsync();
        }

        var mvInterval = Config.GetInt("MeterValueSampleInterval", 60);
        if (mvInterval > 0 && (now - _lastMeterSample).TotalSeconds >= mvInterval)
        {
            _lastMeterSample = now;
            foreach (var c in _connectors)
                if (c.Id > 0 && c.InTransaction)
                    _ = SendMeterValuesAsync(c.Id, ReadingContext.SamplePeriodic);
        }

        // SoC taper: a nearly-full battery drops its draw, then the EVSE
        // settles into SuspendedEV — the classic end-of-charge behaviour.
        foreach (var c in _connectors)
        {
            if (!c.InTransaction) continue;
            if (c.Meter.Soc >= 100.0 && c.Status == ConnectorStatus.Charging)
                await SuspendAsync(c.Id, evSide: true, "battery full");
            else if (c.Meter.Soc > 90.0)
                c.Meter.DrawFactor = Math.Max(0.15, (100.0 - c.Meter.Soc) / 10.0);
        }
    }

    private async Task<T> BrainAsync<T>(Func<T> work)
    {
        await _brain.WaitAsync();
        try { return work(); }
        finally { _brain.Release(); }
    }

    private async Task Brain(Action work)
    {
        await _brain.WaitAsync();
        try { work(); }
        finally { _brain.Release(); }
    }
}
