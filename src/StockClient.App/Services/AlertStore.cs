using System.IO;
using System.Text.Json;

namespace StockClient.App.Services;

/// <summary>One price/percent trigger for a contract.</summary>
public sealed class PriceAlert
{
    public string Code { get; set; } = "";

    /// <summary>"price" or "pct" (涨跌幅 %).</summary>
    public string Metric { get; set; } = "price";

    /// <summary>true = 突破(≥ 阈值), false = 跌破(≤ 阈值).</summary>
    public bool Above { get; set; }

    public double Value { get; set; }

    /// <summary>
    /// Armed alerts can fire; firing disarms them so a hovering price doesn't
    /// spam. Re-armed by <see cref="AlertStore"/> once the metric crosses back
    /// past the threshold (hysteresis) — a fresh crossing is a fresh alert.
    /// </summary>
    public bool Armed { get; set; } = true;

    public string Describe() =>
        (Metric == "pct" ? "涨跌幅 " : "价格 ") + (Above ? "≥ " : "≤ ") +
        (Metric == "pct" ? Value.ToString("0.##") + "%" : Value.ToString("0.###"));
}

/// <summary>
/// Machine-local price alerts — %APPDATA%\StockClient\alerts.json. Deliberately
/// NOT synced to the account: an alert is about what THIS machine should pop up,
/// and syncing would double-notify across machines.
/// </summary>
public sealed class AlertStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly string _path;
    private readonly object _gate = new();
    private List<PriceAlert> _alerts;

    public AlertStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StockClient", "alerts.json");
        _alerts = Load();
    }

    private List<PriceAlert> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<List<PriceAlert>>(File.ReadAllText(_path), Options)
                       ?? new();
        }
        catch { /* corrupt = start empty */ }
        return new();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_alerts, Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* best effort */ }
    }

    public IReadOnlyList<PriceAlert> For(string code)
    {
        lock (_gate)
            return _alerts.Where(a => string.Equals(a.Code, code, StringComparison.OrdinalIgnoreCase))
                          .ToArray();
    }

    public bool Any
    {
        get { lock (_gate) return _alerts.Count > 0; }
    }

    public void Replace(string code, IEnumerable<PriceAlert> alerts)
    {
        lock (_gate)
        {
            _alerts.RemoveAll(a => string.Equals(a.Code, code, StringComparison.OrdinalIgnoreCase));
            _alerts.AddRange(alerts.Select(a => new PriceAlert
            {
                Code = code, Metric = a.Metric, Above = a.Above, Value = a.Value, Armed = true,
            }));
            Save();
        }
    }

    /// <summary>
    /// Evaluates one contract's live metrics and returns the alerts that just
    /// fired. Firing disarms; an alert re-arms when its metric sits on the far
    /// side of the threshold (so it can fire again on the next real crossing).
    /// </summary>
    public IReadOnlyList<PriceAlert> Evaluate(string code, double price, double pct)
    {
        var fired = new List<PriceAlert>();
        lock (_gate)
        {
            var changed = false;
            foreach (var a in _alerts)
            {
                if (!string.Equals(a.Code, code, StringComparison.OrdinalIgnoreCase)) continue;
                var v = a.Metric == "pct" ? pct : price;
                var hit = a.Above ? v >= a.Value : v <= a.Value;

                // Dead zone on the re-arm side so a price jittering right on the
                // threshold (10.00/9.99/10.00…) doesn't fire-and-rearm every
                // tick: it must retreat past the threshold by a small margin
                // before it counts as a fresh crossing.
                var margin = a.Metric == "pct" ? 0.1 : Math.Max(Math.Abs(a.Value) * 0.001, 1e-4);
                var cleared = a.Above ? v < a.Value - margin : v > a.Value + margin;

                if (hit && a.Armed) { a.Armed = false; changed = true; fired.Add(a); }
                else if (cleared && !a.Armed) { a.Armed = true; changed = true; }
            }
            if (changed) Save();
        }
        return fired;
    }
}
