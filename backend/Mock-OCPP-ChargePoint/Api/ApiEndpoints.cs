using System.Text.Json;
using System.Text.Json.Serialization;
using MockOcpp.Cli;
using MockOcpp.Model;
using MockOcpp.Protocol;
using MockOcpp.Sim;

namespace MockOcpp.Api;

/// <summary>Body of POST /api/fleet/connect. Mirrors the CLI options; anything omitted keeps the CLI default.</summary>
public sealed record ConnectRequest(
    string? BaseUrl,
    int? Count,
    string? IdPrefix,
    int? First,
    string? Id,
    int? Connectors,
    double? PowerW,
    string? Vendor,
    string? Model,
    string? Firmware,
    int? HeartbeatInterval,
    int? MeterInterval,
    double? Speed,
    string? User,
    string? Password,
    string? Tag,
    bool? Auto,
    bool? SuspendedEv,
    bool? Passive,
    bool? Replace);

public sealed record DisconnectRequest(string[]? Ids);
public sealed record TagRequest(string IdTag);
public sealed record FaultRequest(string Code, string? Info);
public sealed record PowerRequest(double Watts);
public sealed record AvailabilityRequest(bool Operative);
public sealed record SpeedRequest(double Factor);
public sealed record ConfigSetRequest(string Value);
public sealed record DataTransferRequest(string VendorId, string? MessageId, string? Data);

public static class ApiEndpoints
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void MapApi(this WebApplication app, Fleet fleet, EventHub hub)
    {
        var api = app.MapGroup("/api");

        // Any failure in a command becomes a 400 with the message, like the REPL's "error: ...".
        api.AddEndpointFilter(async (ctx, next) =>
        {
            try { return await next(ctx); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // ---- fleet ---------------------------------------------------------
        api.MapGet("/fleet", () => Snapshot(fleet));

        api.MapPost("/fleet/connect", async (ConnectRequest r) =>
        {
            var opt = ToOptions(r);
            var ids = await fleet.ConnectAsync(opt, r.Replace ?? false);
            return Results.Ok(new { added = ids });
        });

        api.MapPost("/fleet/disconnect", async (DisconnectRequest? r) =>
            Results.Ok(new { removed = await fleet.DisconnectAsync(r?.Ids is { Length: > 0 } ? r.Ids : null) }));

        // ---- live events (SSE) + backlog -----------------------------------
        api.MapGet("/log", (long? after, int? limit) => hub.Recent(after ?? 0, Math.Clamp(limit ?? 500, 1, 2000)));
        api.MapDelete("/log", () => { hub.Clear(); return Results.NoContent(); });
        api.MapGet("/events", (HttpContext ctx, CancellationToken ct) => StreamEvents(ctx, fleet, hub, ct));

        // ---- per charge point ----------------------------------------------
        var cp = api.MapGroup("/cp/{id}");

        cp.MapPost("/reconnect", (string id) => Act(fleet, id, n => { n.ForceReconnect(); return Task.CompletedTask; }));
        cp.MapPost("/boot", (string id) => Act(fleet, id, n => n.Cp.SendBootNotificationAsync()));
        cp.MapPost("/heartbeat", (string id) => Act(fleet, id, n => n.Cp.SendHeartbeatAsync()));

        cp.MapPost("/speed", (string id, SpeedRequest r) => Act(fleet, id, n =>
        {
            foreach (var c in n.Cp.Connectors) { c.Meter.Settle(); c.Meter.TimeScale = r.Factor; }
            return Task.CompletedTask;
        }));

        cp.MapPost("/datatransfer", async (string id, DataTransferRequest r) =>
        {
            var n = Need(fleet, id);
            var reply = await n.Cp.SendDataTransferAsync(r.VendorId, r.MessageId, r.Data);
            return Results.Ok(new { reply = reply?.ToJsonString() });
        });

        cp.MapGet("/config", (string id, string? filter) => Need(fleet, id).Cp.Config.All
            .Where(k => string.IsNullOrEmpty(filter) || k.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(k => new { k.Name, readOnly = k.Mutability == Mutability.ReadOnly, mutability = k.Mutability, k.Value }));

        cp.MapPut("/config/{key}", (string id, string key, ConfigSetRequest r) =>
            Results.Ok(new { result = Need(fleet, id).Cp.Config.Set(key, r.Value).ToString() }));

        cp.MapGet("/authlist", (string id) =>
        {
            var list = Need(fleet, id).Cp.AuthList;
            return new
            {
                version = list.Version,
                entries = list.Entries.Select(e => new { idTag = e.Key, info = e.Value.ToJsonString() }),
            };
        });

        cp.MapGet("/profiles", (string id) =>
        {
            var c = Need(fleet, id).Cp;
            return c.Connectors.SelectMany(conn => c.Profiles.ForConnector(conn.Id)
                .Select(p => new { connectorId = conn.Id, p.Id, p.StackLevel, p.Purpose, p.Kind, p.TransactionId, raw = p.Raw.ToJsonString() }));
        });

        // ---- per connector -------------------------------------------------
        var cn = cp.MapGroup("/connector/{connector:int}");

        cn.MapPost("/plug", (string id, int connector) => Act(fleet, id, n => n.Cp.PlugAsync(connector)));
        cn.MapPost("/unplug", (string id, int connector) => Act(fleet, id, n => n.Cp.UnplugAsync(connector)));
        cn.MapPost("/tag", (string id, int connector, TagRequest r) =>
            Act(fleet, id, n => n.Cp.PresentTagAsync(connector, r.IdTag)));
        cn.MapPost("/remotestop", (string id, int connector) =>
            Act(fleet, id, n => n.Cp.StopTransactionAsync(connector, StopReason.Remote)));
        cn.MapPost("/fault", (string id, int connector, FaultRequest r) =>
        {
            if (!Repl.TryParseError(r.Code, out var code))
                throw new ArgumentException("unknown fault code: ground overcurrent overvoltage undervoltage temperature lock comms reader meter switch internal other none");
            return Act(fleet, id, n => n.Cp.FaultAsync(connector, code, r.Info ?? ""));
        });
        cn.MapPost("/clear", (string id, int connector) => Act(fleet, id, n => n.Cp.ClearFaultAsync(connector)));
        cn.MapPost("/power", (string id, int connector, PowerRequest r) =>
            Act(fleet, id, n => n.Cp.SetPowerAsync(connector, r.Watts)));
        cn.MapPost("/suspendev", (string id, int connector) =>
            Act(fleet, id, n => n.Cp.SuspendAsync(connector, evSide: true)));
        cn.MapPost("/suspendevse", (string id, int connector) =>
            Act(fleet, id, n => n.Cp.SuspendAsync(connector, evSide: false)));
        cn.MapPost("/resume", (string id, int connector) => Act(fleet, id, n => n.Cp.ResumeAsync(connector)));
        cn.MapPost("/availability", (string id, int connector, AvailabilityRequest r) =>
            Act(fleet, id, n => n.Cp.ApplyAvailabilityAsync(connector, r.Operative)));
        // The REPL's `meter` command sends a StatusNotification for the connector.
        cn.MapPost("/status-notification", (string id, int connector) =>
            Act(fleet, id, n => n.Cp.SendStatusNotificationAsync(connector)));
    }

    private static ChargePointNode Need(Fleet fleet, string id) =>
        fleet.Find(id) ?? throw new InvalidOperationException($"no such charge point: {id}");

    /// <summary>How long a command may block the HTTP call. Commands that wait on the CSMS
    /// (e.g. a StatusNotification with the link down) keep running in the background; the
    /// caller still gets the current state, and live updates arrive over the event stream.</summary>
    private static readonly TimeSpan CommandGrace = TimeSpan.FromSeconds(2);

    private static async Task<IResult> Act(Fleet fleet, string id, Func<ChargePointNode, Task> action)
    {
        var node = Need(fleet, id);
        var task = action(node);
        if (await Task.WhenAny(task, Task.Delay(CommandGrace)) == task)
            await task;   // finished in time: surface validation errors to the caller
        else
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return Results.Ok(Describe(node));
    }

    private static CliOptions ToOptions(ConnectRequest r)
    {
        var o = new CliOptions();
        if (!string.IsNullOrWhiteSpace(r.BaseUrl)) o.Url = r.BaseUrl.Trim();
        if (!Uri.TryCreate(o.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss"))
            throw new ArgumentException("baseUrl must be an absolute ws:// or wss:// URL");

        o.Count = r.Count ?? 1;
        if (o.Count is < 1 or > 100) throw new ArgumentException("count must be between 1 and 100");
        o.First = r.First ?? 1;
        if (o.First < 0) throw new ArgumentException("first must be >= 0");
        if (!string.IsNullOrWhiteSpace(r.IdPrefix)) o.IdPrefix = r.IdPrefix.Trim();
        o.ExplicitId = string.IsNullOrWhiteSpace(r.Id) ? null : r.Id.Trim();
        o.Connectors = r.Connectors ?? 1;
        if (o.Connectors is < 1 or > 16) throw new ArgumentException("connectors must be between 1 and 16");

        if (r.PowerW is { } p) o.PowerW = p;
        if (!string.IsNullOrWhiteSpace(r.Vendor)) o.Vendor = r.Vendor;
        if (!string.IsNullOrWhiteSpace(r.Model)) o.Model = r.Model;
        if (!string.IsNullOrWhiteSpace(r.Firmware)) o.Firmware = r.Firmware;
        if (r.HeartbeatInterval is { } hb) o.HeartbeatInterval = hb;
        if (r.MeterInterval is { } mi) o.MeterInterval = mi;
        if (r.Speed is { } sp and > 0) o.TimeScale = sp;
        if (!string.IsNullOrEmpty(r.User)) { o.BasicAuthUser = r.User; o.BasicAuthPassword = r.Password; }
        if (!string.IsNullOrWhiteSpace(r.Tag)) o.IdTag = r.Tag;

        o.SuspendedEv = r.SuspendedEv ?? false;
        o.Auto = (r.Auto ?? false) || o.SuspendedEv;
        o.Passive = r.Passive ?? false;
        return o;
    }

    // ---- state -------------------------------------------------------------

    private static object Snapshot(Fleet fleet) => new
    {
        time = DateTimeOffset.UtcNow,
        nodes = fleet.Nodes.Select(Describe).ToArray(),
    };

    private static object Describe(ChargePointNode n)
    {
        var cp = n.Cp;
        return new
        {
            id = n.Id,
            connected = cp.Connected,
            booted = cp.Booted,
            clockSynced = cp.Clock.Synced,
            clock = cp.Clock.Synced ? cp.Clock.NowIso : null,
            connectors = cp.Connectors.Where(c => c.Id > 0).Select(c => new
            {
                id = c.Id,
                status = c.Status,
                errorCode = c.ErrorCode,
                errorInfo = c.ErrorInfo,
                operative = c.Operative,
                plugged = c.Plugged,
                reservationId = c.ReservationId,
                reservedForTag = c.ReservedForTag,
                reservationExpiry = c.ReservationId is null ? (DateTimeOffset?)null : c.ReservationExpiry,
                meter = new
                {
                    energyWh = c.Meter.EnergyWh,
                    powerOfferedW = c.Meter.PowerOfferedW,
                    activePowerW = c.Meter.ActivePowerW,
                    soc = c.Meter.Soc,
                    timeScale = c.Meter.TimeScale,
                },
                transaction = c.Transaction is { } tx ? new
                {
                    id = tx.HasId ? (int?)tx.Id : null,
                    idTag = tx.IdTag,
                    active = tx.Active,
                    startedAt = tx.StartedAt,
                    energyWh = tx.EnergyWh(c.Meter.EnergyWh),
                } : null,
            }).ToArray(),
        };
    }

    private static async Task StreamEvents(HttpContext ctx, Fleet fleet, EventHub hub, CancellationToken ct)
    {
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        async Task Send(string name, object data)
        {
            await ctx.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data, Json)}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }

        var reader = hub.Subscribe(ct);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            await Send("state", Snapshot(fleet));
            var read = reader.WaitToReadAsync(ct).AsTask();
            var tick = timer.WaitForNextTickAsync(ct).AsTask();
            while (!ct.IsCancellationRequested)
            {
                var done = await Task.WhenAny(read, tick);
                if (done == tick)
                {
                    if (!await tick) break;
                    await Send("state", Snapshot(fleet));
                    tick = timer.WaitForNextTickAsync(ct).AsTask();
                }
                else
                {
                    if (!await read) break;
                    while (reader.TryRead(out var e)) await Send("log", e);
                    read = reader.WaitToReadAsync(ct).AsTask();
                }
            }
        }
        catch (OperationCanceledException) { /* client went away */ }
    }
}
