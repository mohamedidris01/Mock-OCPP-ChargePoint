using MockOcpp.Cli;

namespace MockOcpp.Sim;

/// <summary>The set of running charge points, changeable at runtime (the web UI
/// connects and disconnects units on demand).</summary>
public sealed class Fleet : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly List<ChargePointNode> _nodes = new();
    private readonly EventHub _hub;
    private readonly Action<string> _consoleLog;

    public Fleet(EventHub hub, Action<string> consoleLog)
    {
        _hub = hub;
        _consoleLog = consoleLog;
    }

    public IReadOnlyList<ChargePointNode> Nodes
    {
        get { lock (_lock) return _nodes.ToArray(); }
    }

    public ChargePointNode? Find(string id)
    {
        lock (_lock)
            return _nodes.FirstOrDefault(n => n.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The charge point ids <paramref name="opt"/> describes, same scheme as the CLI.</summary>
    public static List<string> PlanIds(CliOptions opt)
    {
        var width = Math.Max(4, (opt.First + opt.Count - 1).ToString().Length);
        var ids = new List<string>();
        for (var i = opt.First; i < opt.First + opt.Count; i++)
            ids.Add(opt.Count == 1 && opt.ExplicitId is not null
                ? opt.ExplicitId
                : opt.IdPrefix + i.ToString().PadLeft(width, '0'));
        return ids;
    }

    /// <summary>Creates and starts the units. Throws <see cref="InvalidOperationException"/>
    /// if any id is already running and <paramref name="replace"/> is false.</summary>
    public async Task<IReadOnlyList<string>> ConnectAsync(CliOptions opt, bool replace)
    {
        if (replace) await DisconnectAsync(null);

        var ids = PlanIds(opt);
        var created = new List<ChargePointNode>();
        lock (_lock)
        {
            var clash = ids.Where(id => _nodes.Any(n => n.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList();
            if (clash.Count > 0)
                throw new InvalidOperationException($"already running: {string.Join(", ", clash)}");

            foreach (var id in ids)
            {
                var node = new ChargePointNode(id, opt, _consoleLog);
                node.Info += m => _hub.Publish(id, "log", m);
                node.Wire += (outbound, text) => _hub.Publish(id, outbound ? "wire-out" : "wire-in", text);
                _nodes.Add(node);
                created.Add(node);
            }
        }

        _hub.Publish("", "log", $"starting {created.Count} charge point(s) → {opt.Url}");
        foreach (var n in created)
        {
            n.Start();
            await Task.Delay(100);   // stagger the socket opens
        }
        return ids;
    }

    /// <summary>Stops and removes the named units, or all of them when <paramref name="ids"/> is null.</summary>
    public async Task<int> DisconnectAsync(IEnumerable<string>? ids)
    {
        List<ChargePointNode> victims;
        lock (_lock)
        {
            var set = ids?.ToHashSet(StringComparer.OrdinalIgnoreCase);
            victims = _nodes.Where(n => set is null || set.Contains(n.Id)).ToList();
            foreach (var v in victims) _nodes.Remove(v);
        }
        foreach (var v in victims)
            await v.DisposeAsync();
        if (victims.Count > 0)
            _hub.Publish("", "log", $"stopped {victims.Count} charge point(s)");
        return victims.Count;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync(null);
}
