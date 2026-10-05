using System.Text.Json.Nodes;

namespace MockOcpp.Model;

/// <summary>Local Authorization List (LocalAuthListManagement profile).</summary>
public sealed class LocalAuthList
{
    private readonly Dictionary<string, JsonObject> _entries = new(StringComparer.Ordinal);

    public int Version { get; private set; }

    public IReadOnlyDictionary<string, JsonObject> Entries => _entries;

    public void ReplaceFull(int version, IEnumerable<(string idTag, JsonObject? info)> items)
    {
        _entries.Clear();
        foreach (var (tag, info) in items)
            if (info is not null) _entries[tag] = info;
        Version = version;
    }

    public void ApplyDifferential(int version, IEnumerable<(string idTag, JsonObject? info)> items)
    {
        foreach (var (tag, info) in items)
        {
            if (info is null) _entries.Remove(tag);
            else _entries[tag] = info;
        }
        Version = version;
    }

    public bool TryAuthorize(string idTag, out string status)
    {
        status = "Invalid";
        if (!_entries.TryGetValue(idTag, out var info))
            return false;
        status = info["status"]?.GetValue<string>() ?? "Invalid";
        return status == "Accepted";
    }
}

/// <summary>An installed charging profile (SmartCharging profile).</summary>
public sealed record ChargingProfile(
    int Id,
    int StackLevel,
    string Purpose,
    string Kind,
    int? TransactionId,
    JsonObject Raw);

public sealed class ChargingProfileStore
{
    // connectorId -> profiles
    private readonly Dictionary<int, List<ChargingProfile>> _byConnector = new();

    public void Set(int connectorId, ChargingProfile profile)
    {
        var list = _byConnector.TryGetValue(connectorId, out var l) ? l : _byConnector[connectorId] = new();
        list.RemoveAll(p => p.Id == profile.Id ||
            (p.Purpose == profile.Purpose && p.StackLevel == profile.StackLevel));
        list.Add(profile);
    }

    public int Clear(int? id, int? connectorId, string? purpose, int? stackLevel)
    {
        int removed = 0;
        foreach (var (conn, list) in _byConnector)
        {
            if (connectorId is not null && connectorId != conn) continue;
            removed += list.RemoveAll(p =>
                (id is null || p.Id == id) &&
                (purpose is null || p.Purpose == purpose) &&
                (stackLevel is null || p.StackLevel == stackLevel));
        }
        return removed;
    }

    public IEnumerable<ChargingProfile> ForConnector(int connectorId)
        => _byConnector.TryGetValue(connectorId, out var l) ? l : Enumerable.Empty<ChargingProfile>();

    /// <summary>The limit currently in force for a connector, if any profile sets one.</summary>
    public (double limitW, string unit)? EffectiveLimit(int connectorId, double voltage)
    {
        ChargingProfile? winner = null;
        foreach (var p in ForConnector(connectorId))
            if (winner is null || p.StackLevel > winner.StackLevel)
                winner = p;
        if (winner is null) return null;

        var schedule = winner.Raw["chargingSchedule"] as JsonObject;
        var periods = schedule?["chargingSchedulePeriod"] as JsonArray;
        if (periods is null || periods.Count == 0) return null;

        var unit = schedule!["chargingRateUnit"]?.GetValue<string>() ?? "W";
        var limit = periods[0]?["limit"]?.GetValue<double>() ?? 0.0;
        var watts = unit.Equals("A", StringComparison.OrdinalIgnoreCase) ? limit * voltage : limit;
        return (watts, unit);
    }
}
