using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockClient.Core.Quotes;

/// <summary>
/// The A-share trading calendar, straight from 深交所's official site:
///   https://www.szse.cn/api/report/exchange/onepersistenthour/monthList?month=YYYY-MM
/// Each day carries jyrq (date) + jybz ("1" 交易日 / "0" 非交易日) — weekends,
/// holidays and 调休 all correct, future months included. No auth, and reachable
/// from egresses that 东财's realtime hosts drop. 深/沪 share trading days, so this
/// one calendar covers all A-share (SH/SZ/BJ).
/// </summary>
public sealed class SzseCalendarClient
{
    private const string Host = "https://www.szse.cn";
    private readonly HttpClient _http;

    public SzseCalendarClient(HttpClient http) => _http = http;

    /// <summary>Trading days across the months spanning [first, last] inclusive, or
    /// null if not one month could be fetched. Partial months still return what
    /// landed.</summary>
    public async Task<IReadOnlyList<DateOnly>?> GetAsync(
        DateOnly first, DateOnly last, CancellationToken cancellationToken)
    {
        var days = new List<DateOnly>();
        var any = false;

        var month = new DateOnly(first.Year, first.Month, 1);
        var end = new DateOnly(last.Year, last.Month, 1);
        while (month <= end)
        {
            try
            {
                var url = $"{Host}/api/report/exchange/onepersistenthour/monthList"
                          + $"?month={month:yyyy-MM}&random=0.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Referrer = new Uri(Host + "/");

                using var response = await _http.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var rows = JsonSerializer.Deserialize<MonthResponse>(json)?.Data;
                if (rows is not null)
                {
                    any = true;
                    foreach (var row in rows)
                        if (row.Flag == "1" && DateOnly.TryParse(row.Date, out var d))
                            days.Add(d);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // One month failing shouldn't sink the rest.
            }

            month = month.AddMonths(1);
        }

        return any ? days : null;
    }

    private sealed record MonthResponse
    {
        [JsonPropertyName("data")]
        public List<Day>? Data { get; init; }
    }

    private sealed record Day
    {
        [JsonPropertyName("jyrq")]
        public string? Date { get; init; }

        [JsonPropertyName("jybz")]
        public string? Flag { get; init; }
    }
}
