using System.Collections.Concurrent;

namespace StockClient.App.Services;

/// <summary>
/// Ships "!!!"-grade client faults to the server so the web admin can see a
/// machine is sick without someone noticing symptoms first — the proxy
/// black-hole of 2026-09-01 ran for 20 minutes before a human spotted it,
/// while the process knew instantly.
///
/// Strictly best-effort: reporting runs fire-and-forget over the account
/// channel, never throws, and dedupes repeats (the same fault text is sent at
/// most once per 10 minutes) so a crash-loop can't flood the server. When the
/// network itself is what broke, the report simply doesn't arrive — the probe
/// log on disk remains the ground truth.
/// </summary>
public static class ErrorReporter
{
    private static AccountSession? _session;
    private static readonly ConcurrentDictionary<string, DateTimeOffset> _sent = new();
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromMinutes(10);

    public static void Init(AccountSession session) => _session = session;

    public static void Report(string kind, string detail)
    {
        try
        {
            var session = _session;
            if (session is null) return;

            detail = detail.Length > 4000 ? detail[..4000] : detail;

            var key = kind + "|" + detail[..Math.Min(detail.Length, 200)];
            var now = DateTimeOffset.Now;
            if (_sent.TryGetValue(key, out var last) && now - last < DedupeWindow) return;
            _sent[key] = now;

            _ = session.ReportErrorAsync(kind, detail);
        }
        catch
        {
            // A failing reporter must never make anything worse.
        }
    }
}
