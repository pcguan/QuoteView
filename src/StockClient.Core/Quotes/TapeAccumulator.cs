namespace StockClient.Core.Quotes;

/// <summary>
/// Accumulates the live 逐笔 tape across polls. The EastMoney details feed is
/// CDN-fronted, so consecutive 3s polls can hit slightly different edges: one may
/// jump ahead MISSING a print another still carries (…33s → 39s dropping the 36s
/// row), or lag a few seconds behind. Replacing the tape wholesale each poll made
/// those prints flicker in and out, or vanish for good. This merges every
/// snapshot by (time, price) instead — a print reported by ANY edge is kept, and
/// a growing current-second row takes its largest reported volume. Merge only
/// adds/updates; it never removes, so the tape can't jump backward or lose a
/// middle print. A big backward jump (new session, or a genuinely much-delayed
/// source) is treated as a reset rather than merging two sessions together.
///
/// One accumulator per contract (the chart window is single-contract). Pure and
/// unit-tested; the view model just feeds it each snapshot.
/// </summary>
public sealed class TapeAccumulator
{
    /// <summary>Backward jump beyond this many seconds = a reset, not edge jitter.</summary>
    private const int ResetGapSecs = 60;

    private readonly Dictionary<(string Time, double Price), (TradeTick Tick, long Seq)> _acc = new();
    private long _seq;
    private int _newestSecs = -1;

    public IReadOnlyList<TradeTick> Add(IReadOnlyList<TradeTick> snapshot)
    {
        if (snapshot.Count == 0) return Current();

        var snapNewest = SecondsOfDay(snapshot[^1].Time);
        if (_acc.Count > 0 && snapNewest >= 0 && _newestSecs - snapNewest > ResetGapSecs)
            Reset();

        foreach (var t in snapshot)
        {
            var key = (t.Time, t.Price);
            if (!_acc.TryGetValue(key, out var existing))
                _acc[key] = (t, _seq++);
            else if (t.Volume > existing.Tick.Volume)
                _acc[key] = (t, existing.Seq);   // same print, more of the second filled in
        }
        if (snapNewest > _newestSecs) _newestSecs = snapNewest;

        return Current();
    }

    public void Clear() => Reset();

    /// <summary>Chronological (time, then first-seen order for the same second).</summary>
    private IReadOnlyList<TradeTick> Current() =>
        _acc.Values
            .OrderBy(v => v.Tick.Time, StringComparer.Ordinal)
            .ThenBy(v => v.Seq)
            .Select(v => v.Tick)
            .ToArray();

    private void Reset()
    {
        _acc.Clear();
        _seq = 0;
        _newestSecs = -1;
    }

    /// <summary>"HH:MM:SS" → seconds of day, or -1 if malformed.</summary>
    private static int SecondsOfDay(string t) =>
        t.Length >= 8
        && int.TryParse(t.AsSpan(0, 2), out var h)
        && int.TryParse(t.AsSpan(3, 2), out var m)
        && int.TryParse(t.AsSpan(6, 2), out var s)
            ? h * 3600 + m * 60 + s
            : -1;
}
