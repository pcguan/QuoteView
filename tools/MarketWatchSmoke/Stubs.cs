using System.Net;
using System.Net.Http;
using StockClient.Core.Groups;

// Isolated harness: never reads/writes user preferences, shows windows or calls upstream APIs.
namespace StockClient.App
{
    public static class AppPrefs
    {
        public static IReadOnlyList<QuoteColumnState> MarketColumns { get; private set; } = Array.Empty<QuoteColumnState>();
        public static event Action? MarketColumnsChanged;
        public static void SaveMarketColumns(List<QuoteColumnState> columns)
        { MarketColumns = columns; MarketColumnsChanged?.Invoke(); }
    }
    public static class Probe
    { public static void Log(string message) => Console.WriteLine(message); }
}
namespace StockClient.App.Services
{
    public static class DirectHttp
    {
        public static PendingHandler Handler { get; set; } = new();
        public static HttpClient Create(TimeSpan timeout) => new(Handler, false) { Timeout = timeout };
    }
    public sealed class PendingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public int Canceled { get; private set; }
        public TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            try { await Ready.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { Canceled++; throw; }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":{"total":1,"diff":[{"f12":"000001","f13":0,"f14":"平安银行","f2":12.34}]}}"""),
            };
        }
    }
}
namespace StockClient.App.ViewModels
{
    public sealed record MarketOption(StockClient.Core.Contracts.Market? Market, string Display)
    { public override string ToString() => Display; }
}
