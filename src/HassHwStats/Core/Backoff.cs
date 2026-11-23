namespace HassHwStats.Core;

/// <summary>Reconnect schedule: 2 s, 4 s, 8 s, 16 s, then every 30 s until a connection succeeds.</summary>
public sealed class Backoff(TimeSpan first, TimeSpan max)
{
    private readonly TimeSpan _first = first;
    private readonly TimeSpan _max = max;
    private TimeSpan _next = first;
    private DateTime _dueUtc = DateTime.MinValue;

    public int Attempt { get; private set; }

    public bool IsDue(DateTime nowUtc) => nowUtc >= _dueUtc;

    public TimeSpan Remaining(DateTime nowUtc) => _dueUtc > nowUtc ? _dueUtc - nowUtc : TimeSpan.Zero;

    /// <summary>Record a failed attempt and schedule the next one.</summary>
    public void Failed(DateTime nowUtc)
    {
        Attempt++;
        _dueUtc = nowUtc + _next;
        _next = TimeSpan.FromTicks(Math.Min(_next.Ticks * 2, _max.Ticks));
    }

    /// <summary>Connected: start from the shortest delay next time.</summary>
    public void Reset()
    {
        Attempt = 0;
        _next = _first;
        _dueUtc = DateTime.MinValue;
    }

    /// <summary>Start over and make the first attempt after <paramref name="delay"/> (e.g. to let USB re-enumerate after wake).</summary>
    public void RestartAfter(DateTime nowUtc, TimeSpan delay)
    {
        Reset();
        _dueUtc = nowUtc + delay;
    }
}
