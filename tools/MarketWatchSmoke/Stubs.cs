using System.Net;
using System.Net.Http;
using StockClient.Core.Groups;

// Isolated harness: never reads/writes user preferences, shows windows or calls upstream APIs.
namespace StockClient.App
{
    public static class AppPrefs
    {
        private static readonly Dictionary<string, List<QuoteColumnState>> Layouts = new();
        public static IReadOnlyList<QuoteColumnState> GetColumns(string key) => Layouts.TryGetValue(key, out var columns) ? columns : Array.Empty<QuoteColumnState>();
        public static event Action<string>? ColumnsChanged;
        public static void SaveColumns(string key, List<QuoteColumnState> columns)
        { Layouts[key] = columns; ColumnsChanged?.Invoke(key); }
    }
    public static class Tones
    { public const string UpHex = "#EF5350", DownHex = "#26A69A"; }
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
        public Func<HttpRequestMessage, string>? Response { get; set; }
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
                Content = new StringContent(Response?.Invoke(request) ?? """{"data":{"total":1,"diff":[{"f12":"000001","f13":0,"f14":"平安银行","f2":12.34}]}}"""),
            };
        }
    }
}
namespace StockClient.App.ViewModels
{
    public sealed record MarketOption(StockClient.Core.Contracts.Market? Market, string Display)
    { public override string ToString() => Display; }
}
