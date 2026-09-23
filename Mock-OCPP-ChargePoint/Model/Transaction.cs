using MockOcpp.Protocol;

namespace MockOcpp.Model;

/// <summary>One charging session on a connector.</summary>
public sealed class Transaction
{
    public string IdTag { get; init; } = "";
    public int MeterStartWh { get; init; }
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>Assigned by the CSMS in StartTransaction.conf. -1 until it lands.</summary>
    public int Id { get; set; } = -1;
    public bool HasId => Id >= 0;

    public bool Active { get; set; } = true;
    public int MeterStopWh { get; set; }
    public DateTimeOffset StoppedAt { get; set; }
    public StopReason Reason { get; set; } = StopReason.Local;

    public int EnergyWh(int meterNow) => Math.Max(0, meterNow - MeterStartWh);
}
