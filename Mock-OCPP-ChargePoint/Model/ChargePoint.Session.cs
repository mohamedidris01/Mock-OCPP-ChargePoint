using System.Text.Json.Nodes;
using MockOcpp.Protocol;

namespace MockOcpp.Model;

/// <summary>Transaction lifecycle plus the local (physical) events that drive it.</summary>
public sealed partial class ChargePoint
{
    // --- local events ---------------------------------------------------

    public async Task PlugAsync(int connectorId)
    {
        var changed = await BrainAsync(() =>
        {
            var c = Connector(connectorId);
            if (c is null || c.Id == 0) return false;
            c.Plugged = true;
            if (!c.InTransaction && c.Status is ConnectorStatus.Available or ConnectorStatus.Preparing)
                return c.SetStatus(ConnectorStatus.Preparing);
            return false;
        });
        Emit($"connector {connectorId}: cable plugged");
        if (changed) await SendStatusNotificationAsync(connectorId);
    }

    public async Task UnplugAsync(int connectorId)
    {
        var c = Connector(connectorId);
        if (c is null) return;

        if (c.InTransaction && Config.GetBool("StopTransactionOnEVSideDisconnect", true))
        {
            await StopTransactionAsync(connectorId, StopReason.EVDisconnected);
            await BrainAsync(() => { c.Plugged = false; return 0; });
            Emit($"connector {connectorId}: cable removed");
            return;
        }

        var changed = await BrainAsync(() =>
        {
            c.Plugged = false;
            return !c.InTransaction && c.SetStatus(ConnectorStatus.Available);
        });
        Emit($"connector {connectorId}: cable removed");
        if (changed) await SendStatusNotificationAsync(connectorId);
    }

    /// <summary>An RFID tap: starts a session, or stops the one this tag started.</summary>
    public async Task PresentTagAsync(int connectorId, string idTag)
    {
        var c = Connector(connectorId);
        if (c is null || c.Id == 0) return;

        if (c.InTransaction)
        {
            if (c.Transaction!.IdTag == idTag)
                await StopTransactionAsync(connectorId, StopReason.Local);
            else
                Emit($"connector {connectorId}: tag mismatch, ignoring stop");
            return;
        }

        if (!c.Operative)
        {
            Emit($"connector {connectorId} is inoperative");
            return;
        }
        if (c.ReservationId is not null && c.ReservedForTag is not null && c.ReservedForTag != idTag)
        {
            Emit($"connector {connectorId} is reserved for another tag");
            return;
        }

        Emit($"connector {connectorId}: tag {idTag} presented — authorizing");
        if (await SendAuthorizeAsync(idTag))
            await StartTransactionAsync(connectorId, idTag);
        else
            Emit($"connector {connectorId}: tag {idTag} rejected");
    }

    public async Task FaultAsync(int connectorId, ChargePointErrorCode code, string info = "")
    {
        if (code != ChargePointErrorCode.NoError)
        {
            var c0 = Connector(connectorId);
            if (c0 is { InTransaction: true })
                await StopTransactionAsync(connectorId, StopReason.EmergencyStop);
        }

        var changed = await BrainAsync(() =>
        {
            var c = Connector(connectorId);
            if (c is null) return false;
            c.ErrorInfo = info;
            var e = c.SetError(code);
            var s = false;
            if (code == ChargePointErrorCode.NoError)
                s = c.SetStatus(c.Plugged ? ConnectorStatus.Preparing : ConnectorStatus.Available);
            return e || s;
        });
        Emit($"connector {connectorId}: {(code == ChargePointErrorCode.NoError ? "fault cleared" : "fault " + code)}");
        if (changed) await SendStatusNotificationAsync(connectorId);
    }

    public Task ClearFaultAsync(int connectorId) => FaultAsync(connectorId, ChargePointErrorCode.NoError);

    public async Task SetPowerAsync(int connectorId, double watts)
    {
        await BrainAsync(() =>
        {
            var c = Connector(connectorId);
            if (c is null) return 0;
            c.Meter.Settle();
            c.Meter.PowerOfferedW = watts;
            return 0;
        });
        Emit($"connector {connectorId}: EVSE offers {watts:0} W");
    }

    /// <summary>Model a suspended session. evSide=true → SuspendedEV (car stops
    /// pulling), false → SuspendedEVSE (station withholds power).</summary>
    public async Task SuspendAsync(int connectorId, bool evSide, string reason = "")
    {
        var changed = await BrainAsync(() =>
        {
            var c = Connector(connectorId);
            if (c is null || !c.InTransaction) return false;
            c.Meter.Settle();
            c.Meter.DrawFactor = 0.0;
            return c.SetStatus(evSide ? ConnectorStatus.SuspendedEV : ConnectorStatus.SuspendedEVSE);
        });
        if (changed)
        {
            Emit($"connector {connectorId}: {(evSide ? "SuspendedEV" : "SuspendedEVSE")}{(reason.Length > 0 ? " (" + reason + ")" : "")}");
            await SendStatusNotificationAsync(connectorId);
            await SendMeterValuesAsync(connectorId, ReadingContext.Trigger);
        }
    }

    public async Task ResumeAsync(int connectorId)
    {
        var changed = await BrainAsync(() =>
        {
            var c = Connector(connectorId);
            if (c is null || !c.InTransaction) return false;
            c.Meter.Settle();
            c.Meter.DrawFactor = c.Meter.Soc >= 100.0 ? 0.0 : 1.0;
            return c.SetStatus(ConnectorStatus.Charging);
        });
        if (changed)
        {
            Emit($"connector {connectorId}: charging resumed");
            await SendStatusNotificationAsync(connectorId);
        }
    }

    // --- transaction lifecycle ----------------------------------------

    public async Task<bool> StartTransactionAsync(int connectorId, string idTag)
    {
        var c = Connector(connectorId);
        if (c is null || c.Id == 0 || c.InTransaction || !c.Operative) return false;

        // Contactor
        var relayOk = await BrainAsync(() =>
        {
            if (c.Meter.ContactorClosed && c.ErrorCode == ChargePointErrorCode.PowerSwitchFailure)
                return false;
            c.Meter.Settle();
            c.Meter.ContactorClosed = true;
            c.Meter.DrawFactor = 1.0;
            return true;
        });
        if (!relayOk)
        {
            await FaultAsync(connectorId, ChargePointErrorCode.PowerSwitchFailure, "contactor did not close");
            return false;
        }

        var meterStart = c.Meter.EnergyWh;
        var startedAt = Clock.Now;
        await Brain(() =>
        {
            c.Transaction = new Transaction { IdTag = idTag, MeterStartWh = meterStart, StartedAt = startedAt };
            c.SetStatus(ConnectorStatus.Charging);
            c.ReservationId = null;
            c.ReservedForTag = null;
        });
        await SendStatusNotificationAsync(connectorId);

        var payload = new JsonObject
        {
            ["connectorId"] = connectorId,
            ["idTag"] = idTag,
            ["meterStart"] = meterStart,
            ["timestamp"] = OcppClock.ToIso(startedAt),
        };

        _ = HandleStartConfAsync(connectorId, payload);
        await SendMeterValuesAsync(connectorId, ReadingContext.TransactionBegin);
        Emit($"connector {connectorId}: transaction started (tag {idTag}, meter {meterStart} Wh)");
        return true;
    }

    private async Task HandleStartConfAsync(int connectorId, JsonObject payload)
    {
        try
        {
            await WaitBootedAsync();
            var conf = await _conn.SendCallAsync("StartTransaction", payload, transactional: true);
            var txId = conf["transactionId"]?.GetValue<int>() ?? -1;
            var status = conf["idTagInfo"]?["status"]?.GetValue<string>() ?? "Accepted";

            await Brain(() =>
            {
                var c = Connector(connectorId);
                if (c?.Transaction is { } tx) tx.Id = txId;
            });
            Emit($"connector {connectorId}: transaction id {txId} assigned");

            if (status != "Accepted" && Config.GetBool("StopTransactionOnInvalidId", true))
                await StopTransactionAsync(connectorId, StopReason.DeAuthorized);
        }
        catch (OcppCallException ex)
        {
            Emit($"StartTransaction not confirmed: {ex.Message}");
        }
    }

    public async Task StopTransactionAsync(int connectorId, StopReason reason)
    {
        var c = Connector(connectorId);
        if (c is null || !c.InTransaction) return;

        var (tx, meterStop, stoppedAt) = await BrainAsync(() =>
        {
            c.Meter.Settle();
            c.Meter.ContactorClosed = false;
            var t = c.Transaction!;
            t.Active = false;
            var stop = c.Meter.EnergyWh;
            t.MeterStopWh = stop;
            t.StoppedAt = Clock.Now;
            t.Reason = reason;
            c.SetStatus(c.Plugged ? ConnectorStatus.Finishing : ConnectorStatus.Available);
            return (t, stop, t.StoppedAt);
        });
        await SendStatusNotificationAsync(connectorId);

        Emit($"connector {connectorId}: transaction stopped ({reason}), {tx.EnergyWh(meterStop)} Wh delivered");

        if (tx.HasId)
        {
            var payload = new JsonObject
            {
                ["transactionId"] = tx.Id,
                ["meterStop"] = meterStop,
                ["timestamp"] = OcppClock.ToIso(stoppedAt),
                ["idTag"] = tx.IdTag,
                ["reason"] = Wire.Name(reason),
                ["transactionData"] = new JsonArray(BuildSample(c, ReadingContext.TransactionEnd)),
            };
            try
            {
                await WaitBootedAsync();
                await _conn.SendCallAsync("StopTransaction", payload, transactional: true);
            }
            catch (OcppCallException ex) { Emit($"StopTransaction not confirmed: {ex.Message}"); }
        }

        await Brain(() =>
        {
            c.Transaction = null;
            c.Meter.DrawFactor = 1.0;
            c.Meter.Soc = _rng.Next(15, 45);
            if (c.AvailabilityChangePending)
            {
                c.AvailabilityChangePending = false;
                c.Operative = false;
                c.SetStatus(ConnectorStatus.Unavailable);
            }
        });
        if (c.Status == ConnectorStatus.Unavailable)
            await SendStatusNotificationAsync(connectorId);
    }

    /// <summary>Apply a ChangeAvailability decision. Returns the wire status.</summary>
    public async Task<string> ApplyAvailabilityAsync(int connectorId, bool operative)
    {
        var targets = connectorId == 0
            ? _connectors.Where(c => c.Id > 0).ToList()
            : Connector(connectorId) is { } one ? new List<Connector> { one } : new();
        if (targets.Count == 0) return "Rejected";

        if (!operative && targets.Any(c => c.InTransaction))
        {
            foreach (var c in targets.Where(c => c.InTransaction))
                await BrainAsync(() => { c.AvailabilityChangePending = true; return 0; });
            return "Scheduled";
        }

        foreach (var c in targets)
        {
            await BrainAsync(() =>
            {
                c.Operative = operative;
                c.SetStatus(operative ? ConnectorStatus.Available : ConnectorStatus.Unavailable);
                return 0;
            });
            await SendStatusNotificationAsync(c.Id);
        }
        return "Accepted";
    }
}
