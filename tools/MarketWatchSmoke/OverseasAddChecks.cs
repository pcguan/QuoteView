using System.Windows.Threading;
using StockClient.App.Services;
using StockClient.App.ViewModels;
using StockClient.Core.Contracts;
using StockClient.Core.Groups;

internal static class OverseasAddChecks
{
    private sealed class OfflineList : IContractListClient
    {
        public Task<IReadOnlyList<Contract>> FetchAsync(Market market, CancellationToken ct) => throw new Exception("catalog must work offline");
    }
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception(message); }

    public static async Task Run(Dispatcher dispatcher)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qv-add-index-" + Guid.NewGuid());
        try
        {
            var store = new GroupStore(System.IO.Path.Combine(root, "groups.json"));
            store.Save(new GroupConfig { Groups = new() { new Group { Id = "test", Name = "验证分组" } }, ActiveGroupId = "test" });
            var repo = new ContractRepository(new OfflineList(), new ContractCache(System.IO.Path.Combine(root, "contracts")), new MarketClock());
            var handler = DirectHttp.Handler = new PendingHandler
            {
                Response = req => string.Join("", req.RequestUri!.OriginalString.Split("q=")[1].Split(',').Select(api =>
                {
                    var fields = Enumerable.Repeat("", 62).ToArray();
                    fields[1] = "指数"; fields[3] = "12345.67"; fields[4] = "12300"; fields[61] = "ZS";
                    return "v_" + api + "=\"" + string.Join("~", fields) + "\";\n";
                })),
            };
            handler.Ready.SetResult(true);
            await using var vm = new QuotesViewModel(dispatcher, repo, storageRoot: root);
            foreach (var code in new[] { "US.NDX", "US.INX", "US.IXIC" })
            {
                vm.NewCode = code;
                Check(vm.CanAddCode && vm.Suggestions.FirstOrDefault()?.Code == code, "add-box must suggest " + code);
                Check(vm.SearchContracts(code).FirstOrDefault()?.Code == code, "panel editor must suggest " + code);
                vm.AddCode();
                Check(!vm.HasError && vm.Quotes.Any(r => r.Code == code), "add-box failed to add " + code);
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (vm.Quotes.Any(r => r.IsMissing) && DateTime.UtcNow < deadline) await Task.Delay(10);
                Check(vm.Quotes.All(r => !r.IsMissing && r.Now == 12345.67), "added row did not receive a parsed quote: " + code);
            }
            foreach (var query in new[] { "纳斯达克100", "NDX", "标普500", "SPX", "纳斯达克综合指数", "COMP" })
                Check(vm.SearchContracts(query).FirstOrDefault()?.Type == ContractType.Index, "name/alias suggestion missing: " + query);
            vm.NewCode = "NDX";
            vm.AddCode();
            Check(vm.HasError && vm.Quotes.Count == 3, "adding an alias must detect an existing index");
            Check(store.Load().Groups.Single().Codes.SequenceEqual(new[] { "US.NDX", "US.INX", "US.IXIC" }), "added index codes not persisted");
            Console.WriteLine("PASS: real QuotesViewModel suggestions/add/persist/quote update/duplicate check and panel search; isolated storage, no window opened");
        }
        finally { if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true); }
    }
}
