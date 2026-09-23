using System.Globalization;

namespace MockOcpp.Model;

/// <summary>
/// The CSMS owns the clock. BootNotification/Heartbeat responses carry
/// <c>currentTime</c>; rather than touch the OS clock we record the offset and
/// apply it whenever a timestamp goes on the wire.
/// </summary>
public sealed class OcppClock
{
    private TimeSpan _offset = TimeSpan.Zero;

    public bool Synced { get; private set; }

    public void Sync(DateTimeOffset csmsTime)
    {
        _offset = csmsTime - DateTimeOffset.UtcNow;
        Synced = true;
    }

    public DateTimeOffset Now => DateTimeOffset.UtcNow + _offset;

    public string NowIso => ToIso(Now);

    public static string ToIso(DateTimeOffset t)
        => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out DateTimeOffset value)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
}
