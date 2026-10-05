using MockOcpp.Protocol;

namespace MockOcpp.Model;

public enum Mutability { ReadWrite, ReadOnly, RebootRequired }

public enum ConfigSetResult { Accepted, Rejected, RebootRequired, NotSupported }

public sealed record ConfigKey(string Name, Mutability Mutability, string Value)
{
    public string Value { get; set; } = Value;
}

/// <summary>
/// The standard OCPP 1.6 configuration store. All Core keys plus the ones the
/// optional profiles need. Unknown keys answer NotSupported, read-only keys
/// answer Rejected — exactly what a compliance suite checks.
/// </summary>
public sealed class OcppConfiguration
{
    private readonly Dictionary<string, ConfigKey> _keys;
    public event Action<string, string>? Changed;

    public OcppConfiguration(int numberOfConnectors)
    {
        var defaults = new (string, Mutability, string)[]
        {
            // Core — required
            ("AllowOfflineTxForUnknownId", Mutability.ReadWrite, "false"),
            ("AuthorizationCacheEnabled", Mutability.ReadWrite, "true"),
            ("AuthorizeRemoteTxRequests", Mutability.ReadWrite, "false"),
            ("ClockAlignedDataInterval", Mutability.ReadWrite, "0"),
            ("ConnectionTimeOut", Mutability.ReadWrite, "60"),
            ("ConnectorPhaseRotation", Mutability.ReadWrite, "0.RST"),
            ("GetConfigurationMaxKeys", Mutability.ReadOnly, "100"),
            ("HeartbeatInterval", Mutability.ReadWrite, "300"),
            ("LocalAuthorizeOffline", Mutability.ReadWrite, "true"),
            ("LocalPreAuthorize", Mutability.ReadWrite, "false"),
            ("MeterValuesAlignedData", Mutability.ReadWrite, "Energy.Active.Import.Register"),
            ("MeterValuesSampledData", Mutability.ReadWrite, "Energy.Active.Import.Register,Power.Active.Import"),
            ("MeterValueSampleInterval", Mutability.ReadWrite, "60"),
            ("NumberOfConnectors", Mutability.ReadOnly, numberOfConnectors.ToString()),
            ("ResetRetries", Mutability.ReadWrite, "1"),
            ("StopTransactionOnEVSideDisconnect", Mutability.ReadWrite, "true"),
            ("StopTransactionOnInvalidId", Mutability.ReadWrite, "true"),
            ("StopTxnAlignedData", Mutability.ReadWrite, ""),
            ("StopTxnSampledData", Mutability.ReadWrite, "Energy.Active.Import.Register"),
            ("SupportedFeatureProfiles", Mutability.ReadOnly,
                "Core,FirmwareManagement,LocalAuthListManagement,Reservation,SmartCharging,RemoteTrigger"),
            ("TransactionMessageAttempts", Mutability.ReadWrite, "3"),
            ("TransactionMessageRetryInterval", Mutability.ReadWrite, "60"),
            ("UnlockConnectorOnEVSideDisconnect", Mutability.ReadWrite, "true"),
            // Core — optional
            ("WebSocketPingInterval", Mutability.RebootRequired, "30"),
            ("MeterValuesSampledDataMaxLength", Mutability.ReadOnly, "8"),
            ("BlinkRepeat", Mutability.ReadWrite, "0"),
            ("LightIntensity", Mutability.ReadWrite, "100"),
            ("MaxEnergyOnInvalidId", Mutability.ReadWrite, "0"),
            // LocalAuthListManagement
            ("LocalAuthListEnabled", Mutability.ReadWrite, "true"),
            ("LocalAuthListMaxLength", Mutability.ReadOnly, "1000"),
            ("SendLocalListMaxLength", Mutability.ReadOnly, "100"),
            // Reservation
            ("ReserveConnectorZeroSupported", Mutability.ReadOnly, "false"),
            // SmartCharging
            ("ChargeProfileMaxStackLevel", Mutability.ReadOnly, "8"),
            ("ChargingScheduleAllowedChargingRateUnit", Mutability.ReadOnly, "Current,Power"),
            ("ChargingScheduleMaxPeriods", Mutability.ReadOnly, "16"),
            ("ConnectorSwitch3to1PhaseSupported", Mutability.ReadOnly, "false"),
            ("MaxChargingProfilesInstalled", Mutability.ReadOnly, "16"),
        };

        _keys = defaults.ToDictionary(
            d => d.Item1,
            d => new ConfigKey(d.Item1, d.Item2, d.Item3),
            StringComparer.Ordinal);
    }

    public IReadOnlyCollection<ConfigKey> All => _keys.Values;

    public bool TryGet(string key, out ConfigKey value) => _keys.TryGetValue(key, out value!);

    public string? GetString(string key) => _keys.TryGetValue(key, out var k) ? k.Value : null;

    public int GetInt(string key, int fallback)
        => int.TryParse(GetString(key), out var v) ? v : fallback;

    public bool GetBool(string key, bool fallback)
        => bool.TryParse(GetString(key), out var v) ? v : fallback;

    public ConfigSetResult Set(string key, string value)
    {
        if (!_keys.TryGetValue(key, out var k))
            return ConfigSetResult.NotSupported;
        if (k.Mutability == Mutability.ReadOnly)
            return ConfigSetResult.Rejected;
        if (!Validate(k.Value, value))
            return ConfigSetResult.Rejected;

        k.Value = value;
        if (k.Mutability == Mutability.RebootRequired)
            return ConfigSetResult.RebootRequired;

        Changed?.Invoke(key, value);
        return ConfigSetResult.Accepted;
    }

    // The spec types every value as a string; a bool key must still only take
    // "true"/"false" and an int key only digits.
    private static bool Validate(string current, string proposed)
    {
        if (current is "true" or "false")
            return proposed is "true" or "false";
        if (current.Length > 0 && current.All(char.IsDigit))
            return proposed.Length > 0 && proposed.All(char.IsDigit);
        return true;
    }
}
