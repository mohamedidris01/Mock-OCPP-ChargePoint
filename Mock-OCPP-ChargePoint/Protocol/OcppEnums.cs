namespace MockOcpp.Protocol;

/// <summary>OCPP-J message type ids — element [0] of every frame.</summary>
public enum MessageType
{
    Call = 2,
    CallResult = 3,
    CallError = 4,
}

/// <summary>
/// The CALLERROR codes from OCPP-J 1.6. Spelled exactly as they go on the wire —
/// a CSMS matches on the string.
/// </summary>
public static class RpcErrorCode
{
    public const string NotImplemented = "NotImplemented";
    public const string NotSupported = "NotSupported";
    public const string InternalError = "InternalError";
    public const string ProtocolError = "ProtocolError";
    public const string SecurityError = "SecurityError";
    public const string FormationViolation = "FormationViolation";
    public const string PropertyConstraintViolation = "PropertyConstraintViolation";
    public const string OccurenceConstraintViolation = "OccurenceConstraintViolation";
    public const string TypeConstraintViolation = "TypeConstraintViolation";
    public const string GenericError = "GenericError";
}

/// <summary>connectorId status as reported by StatusNotification.</summary>
public enum ConnectorStatus
{
    Available,
    Preparing,
    Charging,
    SuspendedEVSE,
    SuspendedEV,
    Finishing,
    Reserved,
    Unavailable,
    Faulted,
}

public enum ChargePointErrorCode
{
    ConnectorLockFailure,
    EVCommunicationError,
    GroundFailure,
    HighTemperature,
    InternalError,
    LocalListConflict,
    NoError,
    OtherError,
    OverCurrentFailure,
    OverVoltage,
    PowerMeterFailure,
    PowerSwitchFailure,
    ReaderFailure,
    ResetFailure,
    UnderVoltage,
    WeakSignal,
}

public enum RegistrationStatus { Accepted, Pending, Rejected }

public enum AuthorizationStatus { Accepted, Blocked, Expired, Invalid, ConcurrentTx }

public enum StopReason
{
    EmergencyStop,
    EVDisconnected,
    HardReset,
    Local,
    Other,
    PowerLoss,
    Reboot,
    Remote,
    SoftReset,
    UnlockCommand,
    DeAuthorized,
}

public enum ReadingContext
{
    [WireName("Interruption.Begin")] InterruptionBegin,
    [WireName("Interruption.End")] InterruptionEnd,
    [WireName("Other")] Other,
    [WireName("Sample.Clock")] SampleClock,
    [WireName("Sample.Periodic")] SamplePeriodic,
    [WireName("Transaction.Begin")] TransactionBegin,
    [WireName("Transaction.End")] TransactionEnd,
    [WireName("Trigger")] Trigger,
}

public enum Measurand
{
    [WireName("Current.Import")] CurrentImport,
    [WireName("Current.Offered")] CurrentOffered,
    [WireName("Energy.Active.Import.Register")] EnergyActiveImportRegister,
    [WireName("Power.Active.Import")] PowerActiveImport,
    [WireName("Power.Offered")] PowerOffered,
    [WireName("SoC")] SoC,
    [WireName("Temperature")] Temperature,
    [WireName("Voltage")] Voltage,
}

public enum UnitOfMeasure { Wh, kWh, W, kW, A, V, Celsius, Percent }

[AttributeUsage(AttributeTargets.Field)]
public sealed class WireNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>Turns an enum value into its OCPP wire spelling, honouring [WireName].</summary>
public static class Wire
{
    public static string Name<T>(T value) where T : struct, Enum
    {
        var field = typeof(T).GetField(value.ToString());
        var attr = field?.GetCustomAttributes(typeof(WireNameAttribute), false);
        if (attr is { Length: > 0 })
            return ((WireNameAttribute)attr[0]).Name;
        return value.ToString();
    }

    public static bool TryParse<T>(string? text, out T value) where T : struct, Enum
    {
        value = default;
        if (string.IsNullOrEmpty(text))
            return false;
        foreach (var v in Enum.GetValues<T>())
        {
            if (string.Equals(Name(v), text, StringComparison.OrdinalIgnoreCase))
            {
                value = v;
                return true;
            }
        }
        return Enum.TryParse(text, ignoreCase: true, out value);
    }
}
