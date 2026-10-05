using System.Text.Json.Nodes;
using MockOcpp.Protocol;

namespace MockOcpp.Model;

/// <summary>Every CS-&gt;CP action this charge point answers.</summary>
public sealed partial class ChargePoint
{
    private static int? Int(JsonObject o, string key)
        => o.TryGetPropertyValue(key, out var n) && n is not null ? (int?)n.GetValue<double>() : null;

    private static string? Str(JsonObject o, string key)
        => o.TryGetPropertyValue(key, out var n) && n is not null ? n.GetValue<string>() : null;

    private async Task<CallResult> HandleCsCallAsync(RpcFrame frame)
    {
        var p = frame.Payload;
        Emit($"← {frame.Action} {p.ToJsonString()}");

        switch (frame.Action)
        {
            case "GetConfiguration": return GetConfiguration(p);
            case "ChangeConfiguration": return ChangeConfiguration(p);
            case "ChangeAvailability": return await ChangeAvailability(p);
            case "ClearCache": return ClearCache();
            case "DataTransfer": return DataTransferIn(p);
            case "RemoteStartTransaction": return await RemoteStart(p);
            case "RemoteStopTransaction": return await RemoteStop(p);
            case "Reset": return await Reset(p);
            case "UnlockConnector": return await UnlockConnector(p);
            case "GetLocalListVersion": return CallResult.Success(new JsonObject { ["listVersion"] = AuthList.Version });
            case "SendLocalList": return SendLocalList(p);
            case "ReserveNow": return await ReserveNow(p);
            case "CancelReservation": return await CancelReservation(p);
            case "SetChargingProfile": return SetChargingProfile(p);
            case "ClearChargingProfile": return ClearChargingProfile(p);
            case "GetCompositeSchedule": return GetCompositeSchedule(p);
            case "TriggerMessage": return TriggerMessage(p);
            case "GetDiagnostics": return GetDiagnostics(p);
            case "UpdateFirmware": return UpdateFirmware();
            default:
                return CallResult.Fail(RpcErrorCode.NotImplemented, $"{frame.Action} is not supported");
        }
    }

    // --- Core ---------------------------------------------------------

    private CallResult GetConfiguration(JsonObject p)
    {
        var requested = (p["key"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList() ?? new();
        var known = new JsonArray();
        var unknown = new JsonArray();

        IEnumerable<ConfigKey> keys = requested.Count == 0
            ? Config.All
            : requested.Select(k => Config.TryGet(k, out var v) ? v : null!).Where(v => v is not null);

        foreach (var k in keys)
        {
            var entry = new JsonObject { ["key"] = k.Name, ["readonly"] = k.Mutability == Mutability.ReadOnly };
            entry["value"] = k.Value;
            known.Add(entry);
        }
        if (requested.Count > 0)
            foreach (var k in requested.Where(k => !Config.TryGet(k, out _)))
                unknown.Add(k);

        var conf = new JsonObject { ["configurationKey"] = known };
        if (unknown.Count > 0) conf["unknownKey"] = unknown;
        return CallResult.Success(conf);
    }

    private CallResult ChangeConfiguration(JsonObject p)
    {
        var key = Str(p, "key");
        var value = Str(p, "value") ?? "";
        if (string.IsNullOrEmpty(key))
            return CallResult.Fail(RpcErrorCode.FormationViolation, "key is required");

        var result = Config.Set(key, value);
        Emit($"config {key} = {value} → {result}");
        return CallResult.Success(new JsonObject { ["status"] = result.ToString() });
    }

    private async Task<CallResult> ChangeAvailability(JsonObject p)
    {
        var connectorId = Int(p, "connectorId");
        var type = Str(p, "type");
        if (connectorId is null || type is null)
            return CallResult.Fail(RpcErrorCode.FormationViolation, "connectorId and type are required");
        if (Connector(connectorId.Value) is null)
            return CallResult.Success(new JsonObject { ["status"] = "Rejected" });

        var status = await ApplyAvailabilityAsync(connectorId.Value, type == "Operative");
        return CallResult.Success(new JsonObject { ["status"] = status });
    }

    private CallResult ClearCache()
    {
        var enabled = Config.GetBool("AuthorizationCacheEnabled", true);
        return CallResult.Success(new JsonObject { ["status"] = enabled ? "Accepted" : "Rejected" });
    }

    private CallResult DataTransferIn(JsonObject p)
    {
        var vendor = Str(p, "vendorId");
        if (string.IsNullOrEmpty(vendor))
            return CallResult.Fail(RpcErrorCode.FormationViolation, "vendorId is required");
        // Echo a known vendor, reject the rest — a realistic default.
        var status = vendor == _identity.Vendor ? "Accepted" : "UnknownVendorId";
        var conf = new JsonObject { ["status"] = status };
        if (status == "Accepted") conf["data"] = "ack";
        return CallResult.Success(conf);
    }

    private Task<CallResult> RemoteStart(JsonObject p)
    {
        var idTag = Str(p, "idTag");
        if (string.IsNullOrEmpty(idTag))
            return Task.FromResult(CallResult.Fail(RpcErrorCode.FormationViolation, "idTag is required"));
        var connectorId = Int(p, "connectorId") ?? FirstFreeConnector();
        var c = Connector(connectorId);

        if (c is null || connectorId == 0 || c.InTransaction || !c.Operative)
            return Task.FromResult(CallResult.Success(new JsonObject { ["status"] = "Rejected" }));

        // chargingProfile may ride along — install it if present.
        if (p["chargingProfile"] is JsonObject profile)
            InstallProfile(connectorId, profile);

        _ = Task.Run(async () =>
        {
            if (Config.GetBool("AuthorizeRemoteTxRequests", false))
            {
                if (await SendAuthorizeAsync(idTag)) await StartTransactionAsync(connectorId, idTag);
            }
            else
            {
                await StartTransactionAsync(connectorId, idTag);
            }
        });
        return Task.FromResult(CallResult.Success(new JsonObject { ["status"] = "Accepted" }));
    }

    private async Task<CallResult> RemoteStop(JsonObject p)
    {
        var txId = Int(p, "transactionId");
        var target = _connectors.FirstOrDefault(c => c.Transaction is { Active: true } t && t.Id == txId);
        if (target is null)
            return CallResult.Success(new JsonObject { ["status"] = "Rejected" });
        await StopTransactionAsync(target.Id, StopReason.Remote);
        return CallResult.Success(new JsonObject { ["status"] = "Accepted" });
    }

    private Task<CallResult> Reset(JsonObject p)
    {
        var hard = Str(p, "type") == "Hard";
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            foreach (var c in _connectors.Where(c => c.InTransaction).ToList())
                await StopTransactionAsync(c.Id, hard ? StopReason.HardReset : StopReason.SoftReset);
            Emit($"{(hard ? "hard" : "soft")} reset — dropping the connection");
            await Task.Delay(500);
            _conn.Reconnect();
        });
        return Task.FromResult(CallResult.Success(new JsonObject { ["status"] = "Accepted" }));
    }

    private async Task<CallResult> UnlockConnector(JsonObject p)
    {
        var connectorId = Int(p, "connectorId");
        var c = connectorId is null ? null : Connector(connectorId.Value);
        if (c is null || connectorId == 0)
            return CallResult.Success(new JsonObject { ["status"] = "NotSupported" });

        if (c.InTransaction)
            await StopTransactionAsync(connectorId!.Value, StopReason.UnlockCommand);
        await BrainAsync(() => { c.Plugged = false; return 0; });
        return CallResult.Success(new JsonObject { ["status"] = "Unlocked" });
    }

    // --- LocalAuthListManagement ------------------------------------

    private CallResult SendLocalList(JsonObject p)
    {
        var version = Int(p, "listVersion") ?? 0;
        var updateType = Str(p, "updateType") ?? "Full";
        var items = (p["localAuthorizationList"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .Select(e => (
                idTag: Str(e, "idTag") ?? "",
                info: e["idTagInfo"] as JsonObject))
            .Where(x => x.idTag.Length > 0)
            .ToList();

        if (updateType == "Full") AuthList.ReplaceFull(version, items!);
        else AuthList.ApplyDifferential(version, items!);

        Emit($"local auth list → v{version} ({updateType}, {items.Count} entries)");
        return CallResult.Success(new JsonObject { ["status"] = "Accepted" });
    }

    // --- Reservation ----------------------------------------------

    private async Task<CallResult> ReserveNow(JsonObject p)
    {
        var connectorId = Int(p, "connectorId") ?? 0;
        var reservationId = Int(p, "reservationId") ?? 0;
        var idTag = Str(p, "idTag") ?? "";
        var expiry = Str(p, "expiryDate");
        var c = Connector(connectorId);

        if (c is null || connectorId == 0)
            return CallResult.Success(new JsonObject { ["status"] = "Rejected" });
        if (!c.Operative) return CallResult.Success(new JsonObject { ["status"] = "Unavailable" });
        if (c.Status == ConnectorStatus.Faulted) return CallResult.Success(new JsonObject { ["status"] = "Faulted" });
        if (c.InTransaction) return CallResult.Success(new JsonObject { ["status"] = "Occupied" });

        var updating = c.ReservationId is not null;
        await BrainAsync(() =>
        {
            c.ReservationId = reservationId;
            c.ReservedForTag = idTag;
            OcppClock.TryParse(expiry, out var e);
            c.ReservationExpiry = e;
            c.SetStatus(ConnectorStatus.Reserved);
            return 0;
        });
        await SendStatusNotificationAsync(connectorId);
        Emit($"connector {connectorId} reserved (#{reservationId} for {idTag})");
        return CallResult.Success(new JsonObject { ["status"] = updating ? "Accepted" : "Accepted" });
    }

    private async Task<CallResult> CancelReservation(JsonObject p)
    {
        var reservationId = Int(p, "reservationId");
        var c = _connectors.FirstOrDefault(x => x.ReservationId == reservationId);
        if (c is null) return CallResult.Success(new JsonObject { ["status"] = "Rejected" });

        await BrainAsync(() =>
        {
            c.ReservationId = null;
            c.ReservedForTag = null;
            c.SetStatus(c.Plugged ? ConnectorStatus.Preparing : ConnectorStatus.Available);
            return 0;
        });
        await SendStatusNotificationAsync(c.Id);
        Emit($"reservation #{reservationId} cancelled");
        return CallResult.Success(new JsonObject { ["status"] = "Accepted" });
    }

    // --- SmartCharging ------------------------------------------

    private void InstallProfile(int connectorId, JsonObject profile)
    {
        var id = Int(profile, "chargingProfileId") ?? 0;
        var stack = Int(profile, "stackLevel") ?? 0;
        var purpose = Str(profile, "chargingProfilePurpose") ?? "TxProfile";
        var kind = Str(profile, "chargingProfileKind") ?? "Absolute";
        var txId = Int(profile, "transactionId");
        Profiles.Set(connectorId, new ChargingProfile(id, stack, purpose, kind, txId, (JsonObject)profile.DeepClone()));

        var limit = Profiles.EffectiveLimit(connectorId, 230.0);
        if (limit is { } l && Connector(connectorId) is { } c)
        {
            c.Meter.Settle();
            c.Meter.PowerOfferedW = l.limitW;
            Emit($"connector {connectorId}: charging profile caps power at {l.limitW:0} W");
        }
    }

    private CallResult SetChargingProfile(JsonObject p)
    {
        var connectorId = Int(p, "connectorId") ?? 0;
        if (p["csChargingProfiles"] is not JsonObject profile)
            return CallResult.Fail(RpcErrorCode.FormationViolation, "csChargingProfiles is required");
        InstallProfile(connectorId, profile);
        return CallResult.Success(new JsonObject { ["status"] = "Accepted" });
    }

    private CallResult ClearChargingProfile(JsonObject p)
    {
        var removed = Profiles.Clear(
            Int(p, "id"), Int(p, "connectorId"),
            Str(p, "chargingProfilePurpose"), Int(p, "stackLevel"));
        return CallResult.Success(new JsonObject { ["status"] = removed > 0 ? "Accepted" : "Unknown" });
    }

    private CallResult GetCompositeSchedule(JsonObject p)
    {
        var connectorId = Int(p, "connectorId") ?? 0;
        var duration = Int(p, "duration") ?? 0;
        var limit = Profiles.EffectiveLimit(connectorId, 230.0);
        if (limit is null)
            return CallResult.Success(new JsonObject { ["status"] = "Rejected" });

        var schedule = new JsonObject
        {
            ["duration"] = duration,
            ["chargingRateUnit"] = "W",
            ["chargingSchedulePeriod"] = new JsonArray(new JsonObject
            {
                ["startPeriod"] = 0,
                ["limit"] = Math.Round(limit.Value.limitW, 1),
            }),
        };
        return CallResult.Success(new JsonObject
        {
            ["status"] = "Accepted",
            ["connectorId"] = connectorId,
            ["scheduleStart"] = Clock.NowIso,
            ["chargingSchedule"] = schedule,
        });
    }

    // --- RemoteTrigger ----------------------------------------

    private CallResult TriggerMessage(JsonObject p)
    {
        var requested = Str(p, "requestedMessage") ?? "";
        var connectorId = Int(p, "connectorId") ?? 0;

        Func<Task>? action = requested switch
        {
            "BootNotification" => SendBootNotificationAsync,
            "Heartbeat" => SendHeartbeatAsync,
            "StatusNotification" => () => SendStatusNotificationAsync(connectorId),
            "MeterValues" => () => SendMeterValuesAsync(connectorId == 0 ? FirstActiveConnector() : connectorId, ReadingContext.Trigger),
            "DiagnosticsStatusNotification" => () => SendDiagnosticsStatusAsync("Idle"),
            "FirmwareStatusNotification" => () => SendFirmwareStatusAsync("Idle"),
            _ => null,
        };

        if (action is null)
            return CallResult.Success(new JsonObject { ["status"] = "NotImplemented" });

        _ = Task.Run(async () => { await Task.Delay(100); await action(); });
        return CallResult.Success(new JsonObject { ["status"] = "Accepted" });
    }

    // --- FirmwareManagement (stubs — no real download/flash) --------

    private CallResult GetDiagnostics(JsonObject p)
    {
        _ = Task.Run(async () =>
        {
            await SendDiagnosticsStatusAsync("Uploading");
            await Task.Delay(1500);
            await SendDiagnosticsStatusAsync("Uploaded");
        });
        return CallResult.Success(new JsonObject { ["fileName"] = $"diagnostics-{Id}-{DateTime.UtcNow:yyyyMMddHHmmss}.tar.gz" });
    }

    private CallResult UpdateFirmware()
    {
        _ = Task.Run(async () =>
        {
            foreach (var s in new[] { "Downloading", "Downloaded", "Installing", "Installed" })
            {
                await SendFirmwareStatusAsync(s);
                await Task.Delay(1500);
            }
        });
        return CallResult.Success(new JsonObject());
    }

    // --- helpers -------------------------------------------------

    private int FirstFreeConnector()
        => _connectors.FirstOrDefault(c => c.Id > 0 && !c.InTransaction && c.Operative)?.Id ?? 1;

    private int FirstActiveConnector()
        => _connectors.FirstOrDefault(c => c.Id > 0 && c.InTransaction)?.Id ?? 1;
}
