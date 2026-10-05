using MockOcpp.Protocol;

namespace MockOcpp.Model;

/// <summary>
/// A single connector and its OCPP status. Connector 0 is the charge point
/// itself — no hardware, but ChangeAvailability / StatusNotification address it.
/// </summary>
public sealed class Connector
{
    public Connector(int id, double powerOfferedW)
    {
        Id = id;
        Meter = new MeterSim(powerOfferedW);
    }

    public int Id { get; }
    public MeterSim Meter { get; }

    public ConnectorStatus Status { get; private set; } = ConnectorStatus.Available;
    public ChargePointErrorCode ErrorCode { get; private set; } = ChargePointErrorCode.NoError;
    public string ErrorInfo { get; set; } = "";

    public bool Plugged { get; set; }
    public bool Operative { get; set; } = true;
    public bool AvailabilityChangePending { get; set; }

    public Transaction? Transaction { get; set; }
    public bool InTransaction => Transaction is { Active: true };

    // Reservation (Reservation profile)
    public int? ReservationId { get; set; }
    public string? ReservedForTag { get; set; }
    public DateTimeOffset ReservationExpiry { get; set; }

    /// <summary>Returns true when the status actually changed — the only time a
    /// StatusNotification is due.</summary>
    public bool SetStatus(ConnectorStatus status)
    {
        if (Status == status) return false;
        Status = status;
        return true;
    }

    public bool SetError(ChargePointErrorCode code)
    {
        if (ErrorCode == code) return false;
        ErrorCode = code;
        if (code != ChargePointErrorCode.NoError)
            Status = ConnectorStatus.Faulted;
        return true;
    }
}
