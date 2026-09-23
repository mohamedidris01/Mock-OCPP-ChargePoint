using System.Text.Json.Nodes;
using MockOcpp.Protocol;

namespace MockOcpp.Model;

/// <summary>Every CP-&gt;CS message this charge point can originate.</summary>
public sealed partial class ChargePoint
{
    private static void AddOptional(JsonObject o, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value)) o[key] = value;
    }

    public async Task SendBootNotificationAsync()
    {
        var payload = new JsonObject
        {
            ["chargePointVendor"] = _identity.Vendor,
            ["chargePointModel"] = _identity.Model,
        };
        AddOptional(payload, "chargePointSerialNumber", _identity.SerialNumber ?? _identity.Id);
        AddOptional(payload, "firmwareVersion", _identity.FirmwareVersion);
        AddOptional(payload, "iccid", _identity.Iccid);
        AddOptional(payload, "imsi", _identity.Imsi);

        try
        {
            var conf = await _conn.SendCallAsync("BootNotification", payload);
            var status = conf["status"]?.GetValue<string>() ?? "Rejected";
            if (OcppClock.TryParse(conf["currentTime"]?.GetValue<string>(), out var t))
                Clock.Sync(t);
            var interval = conf["interval"]?.GetValue<int>() ?? 0;

            if (status == "Accepted")
            {
                await Brain(() =>
                {
                    Booted = true;
                    if (interval > 0) Config.Set("HeartbeatInterval", interval.ToString());
                    _lastHeartbeat = Clock.Now;
                });
                _bootGate.TrySetResult();
                Emit($"boot accepted — heartbeat {interval}s");
                foreach (var c in _connectors)
                    await SendStatusNotificationAsync(c.Id);
            }
            else
            {
                _nextBootAttempt = Clock.Now.AddSeconds(interval > 0 ? interval : 15);
                Emit($"boot {status} — retrying in {interval}s");
            }
        }
        catch (OcppCallException ex)
        {
            Emit($"BootNotification failed: {ex.Message}");
        }
    }

    public async Task SendHeartbeatAsync()
    {
        await WaitBootedAsync();
        try
        {
            var conf = await _conn.SendCallAsync("Heartbeat", new JsonObject());
            if (OcppClock.TryParse(conf["currentTime"]?.GetValue<string>(), out var t))
                Clock.Sync(t);
        }
        catch (OcppCallException) { /* the tick will try again */ }
    }

    public async Task SendStatusNotificationAsync(int connectorId)
    {
        var c = Connector(connectorId);
        if (c is null) return;
        await WaitBootedAsync();

        var payload = new JsonObject
        {
            ["connectorId"] = connectorId,
            ["errorCode"] = Wire.Name(c.ErrorCode),
            ["status"] = Wire.Name(c.Status),
        };
        AddOptional(payload, "info", c.ErrorInfo);
        if (Clock.Synced) payload["timestamp"] = Clock.NowIso;

        try { await _conn.SendCallAsync("StatusNotification", payload); }
        catch (OcppCallException) { }
    }

    public async Task<bool> SendAuthorizeAsync(string idTag)
    {
        // LocalPreAuthorize / offline: consult the local list first.
        if (Config.GetBool("LocalAuthListEnabled", true) && AuthList.TryAuthorize(idTag, out _))
            return true;

        await WaitBootedAsync();
        try
        {
            var conf = await _conn.SendCallAsync("Authorize", new JsonObject { ["idTag"] = idTag });
            var status = conf["idTagInfo"]?["status"]?.GetValue<string>() ?? "Invalid";
            return status == "Accepted";
        }
        catch (OcppCallException)
        {
            // Unreachable CSMS: fall back to the local list / offline policy.
            if (Config.GetBool("LocalAuthorizeOffline", true) && AuthList.TryAuthorize(idTag, out _))
                return true;
            return Config.GetBool("AllowOfflineTxForUnknownId", false);
        }
    }

    private async Task SendMeterValuesAsync(int connectorId, ReadingContext context)
    {
        var c = Connector(connectorId);
        if (c is null) return;
        await WaitBootedAsync();

        var sample = BuildSample(c, context);
        var payload = new JsonObject
        {
            ["connectorId"] = connectorId,
            ["meterValue"] = new JsonArray(sample),
        };
        if (c.Transaction is { HasId: true } tx)
            payload["transactionId"] = tx.Id;

        try { await _conn.SendCallAsync("MeterValues", payload); }
        catch (OcppCallException) { }
    }

    private JsonObject BuildSample(Connector c, ReadingContext context)
    {
        var m = c.Meter;
        JsonObject V(string value, Measurand measurand, UnitOfMeasure unit) => new()
        {
            ["value"] = value,
            ["context"] = Wire.Name(context),
            ["format"] = "Raw",
            ["measurand"] = Wire.Name(measurand),
            ["unit"] = Wire.Name(unit),
        };

        return new JsonObject
        {
            ["timestamp"] = Clock.NowIso,
            ["sampledValue"] = new JsonArray(
                V(m.EnergyWh.ToString(), Measurand.EnergyActiveImportRegister, UnitOfMeasure.Wh),
                V(((int)m.ActivePowerW).ToString(), Measurand.PowerActiveImport, UnitOfMeasure.W),
                V(m.CurrentA.ToString("0.0"), Measurand.CurrentImport, UnitOfMeasure.A),
                V(m.VoltageV.ToString("0.0"), Measurand.Voltage, UnitOfMeasure.V),
                V(m.Soc.ToString("0"), Measurand.SoC, UnitOfMeasure.Percent)),
        };
    }

    public async Task<JsonObject?> SendDataTransferAsync(string vendorId, string? messageId, string? data)
    {
        await WaitBootedAsync();
        var payload = new JsonObject { ["vendorId"] = vendorId };
        AddOptional(payload, "messageId", messageId);
        AddOptional(payload, "data", data);
        try { return await _conn.SendCallAsync("DataTransfer", payload); }
        catch (OcppCallException ex) { Emit(ex.Message); return null; }
    }

    // Firmware / diagnostics status — the notifications only, sent when the
    // CSMS asks via TriggerMessage. The download/install actions themselves are
    // intentionally not simulated.
    public async Task SendDiagnosticsStatusAsync(string status)
    {
        await WaitBootedAsync();
        try { await _conn.SendCallAsync("DiagnosticsStatusNotification", new JsonObject { ["status"] = status }); }
        catch (OcppCallException) { }
    }

    public async Task SendFirmwareStatusAsync(string status)
    {
        await WaitBootedAsync();
        try { await _conn.SendCallAsync("FirmwareStatusNotification", new JsonObject { ["status"] = status }); }
        catch (OcppCallException) { }
    }
}
