using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChangeClip.Backend;
using ChangeClip.Contracts;
using ChangeClip.Devices;

internal static class Program
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static int checks;
    private static int Main()
    {
        try { Run().GetAwaiter().GetResult(); Console.WriteLine("PASS: " + checks + " integrated simulator/journal checks; no hardware."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChangeClip-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Scenario(root, "return", 25, 100, 1000, true, ExchangeState.Returned, 25, 0);
            await Scenario(root, "10km", 25, 100, 1000, false, ExchangeState.Succeeded, 15, 0);
            await Scenario(root, "20km", 25, 100, 2000, false, ExchangeState.Succeeded, 5, 0);
            await Scenario(root, "lastcoin", 10, 100, 1000, false, ExchangeState.Succeeded, 0, 0);
            await Scenario(root, "2kmcoins", 15, 200, 2000, false, ExchangeState.Succeeded, 5, 0);
            await Scenario(root, "partial", 7, 100, 1000, false, ExchangeState.Debt, 0, 300);
            var options = new DeviceOptions { SimulatorCoins = 100 };
            var devices = DeviceFactory.Create(options);
            using (var restarted = new ExchangeService(devices.BillAcceptor, devices.Hopper,
                new JsonTransactionStore(Path.Combine(root, "partial.json")), 100))
            {
                await restarted.StartAsync(Token);
                Assert(restarted.Snapshot.State == ExchangeState.Debt && restarted.Snapshot.OwedMinor == 300, "restart retains exact debt");
                await restarted.ExchangeAsync(Token);
                Assert(devices.Simulator.HopperCoins == 100, "restart never repeats payout");
                bool blocked = false;
                try { devices.Simulator.InsertBanknote(1000); } catch (InvalidOperationException) { blocked = true; }
                Assert(blocked, "debt blocks new banknote");
            }
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task Scenario(string root, string name, int stock, int coinValue, int banknote,
        bool returnNote, ExchangeState expected, int remaining, int debt)
    {
        var options = new DeviceOptions { SimulatorCoins = stock, CoinValueMinor = coinValue };
        var devices = DeviceFactory.Create(options);
        var store = new JsonTransactionStore(Path.Combine(root, name + ".json"));
        using (var session = new ExchangeService(devices.BillAcceptor, devices.Hopper, store, coinValue))
        {
            await session.StartAsync(Token);
            Assert(session.Snapshot.State == ExchangeState.Ready, name + " ready");
            devices.Simulator.InsertBanknote(banknote);
            Assert(session.Snapshot.State == ExchangeState.AwaitingChoice && session.Snapshot.CanExchange && session.Snapshot.CanReturn, name + " escrow waits");
            if (returnNote) await session.ReturnAsync(Token);
            else await Task.WhenAll(session.ExchangeAsync(Token), session.ExchangeAsync(Token));
            Assert(session.Snapshot.State == expected && session.Snapshot.OwedMinor == debt, name + " result");
            Assert(devices.Simulator.HopperCoins == remaining, name + " actual simulated coins");
            var records = store.ReadAll();
            Assert(returnNote ? records.Count == 0 : records.Count == 1 && records.Single().ReceivedMinor == banknote,
                name + " durable single credit");
        }
    }
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("FAIL: " + message); checks++; }
}
