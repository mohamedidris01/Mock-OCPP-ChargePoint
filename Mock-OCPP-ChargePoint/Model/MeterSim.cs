using System.Diagnostics;

namespace MockOcpp.Model;

/// <summary>
/// A simulated energy meter. Integrates <c>Wh += power_w * dt / 3600</c> while
/// the contactor is closed and the EV is actually drawing. <see cref="TimeScale"/>
/// compresses simulated time so a full session takes seconds at the prompt.
/// </summary>
public sealed class MeterSim
{
    private readonly object _lock = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private double _accumulatedWh;
    private double _lastSeconds;

    public double PowerOfferedW { get; set; }          // what the EVSE makes available
    public double DrawFactor { get; set; } = 1.0;       // 0 = SuspendedEV, taper for a full battery
    public bool ContactorClosed { get; set; }
    public double TimeScale { get; set; } = 1.0;
    public double VoltageV { get; set; } = 230.0;
    public double Soc { get; set; } = 20.0;             // %, climbs while charging

    public MeterSim(double powerOfferedW) => PowerOfferedW = powerOfferedW;

    public double ActivePowerW => ContactorClosed ? PowerOfferedW * DrawFactor : 0.0;
    public double CurrentA => VoltageV > 0 ? ActivePowerW / VoltageV : 0.0;

    private void Integrate()
    {
        var now = _sw.Elapsed.TotalSeconds;
        var dt = (now - _lastSeconds) * TimeScale;
        _lastSeconds = now;
        if (dt <= 0) return;

        if (ContactorClosed)
        {
            var deliveredWh = ActivePowerW * dt / 3600.0;
            _accumulatedWh += deliveredWh;
            // ~50 kWh nominal pack: nudge SoC up as energy flows in.
            Soc = Math.Min(100.0, Soc + deliveredWh / 50_000.0 * 100.0);
        }
    }

    /// <summary>Call before every mutation of power/contactor/scale so the old
    /// rate applies right up to the change.</summary>
    public void Settle()
    {
        lock (_lock) Integrate();
    }

    public int EnergyWh
    {
        get { lock (_lock) { Integrate(); return (int)_accumulatedWh; } }
    }

    public void Reset()
    {
        lock (_lock) { _accumulatedWh = 0; _lastSeconds = _sw.Elapsed.TotalSeconds; }
    }
}
