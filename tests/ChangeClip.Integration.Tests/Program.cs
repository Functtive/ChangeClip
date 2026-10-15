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
            await Scenario(root, "10km", 100, 100, new[] { 1000 }, 90, 0);
            await Scenario(root, "20km", 100, 100, new[] { 2000 }, 80, 0);
            await Scenario(root, "multiple", 100, 100, new[] { 1000, 2000 }, 70, 0);
            await Scenario(root, "lastcoin", 10, 100, new[] { 1000 }, 0, 0);
            await Scenario(root, "2kmcoins", 100, 200, new[] { 1000, 2000 }, 85, 0);
            await Scenario(root, "partial", 7, 100, new[] { 1000 }, 0, 300);
            await Scenario(root, "empty", 0, 100, new[] { 1000 }, 0, 1000);
            await Scenario(root, "batch300", 400, 100, Enumerable.Repeat(2000, 15).ToArray(), 100, 0);
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
            await FundedRestart(root);
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task Scenario(string root, string name, int stock, int coinValue, int[] notes,
        int remaining, int debt)
    {
        var options = new DeviceOptions { SimulatorCoins = stock, CoinValueMinor = coinValue };
        var devices = DeviceFactory.Create(options);
        var store = new JsonTransactionStore(Path.Combine(root, name + ".json"));
        using (var session = new ExchangeService(devices.BillAcceptor, devices.Hopper, store, coinValue))
        {
            await session.StartAsync(Token);
            Assert(session.Snapshot.State == ExchangeState.Ready, name + " ready");
            int total = 0;
            foreach (var note in notes)
            {
                devices.Simulator.InsertBanknote(note);
                await devices.BillAcceptor.WaitForIdleAsync(Token);
                total += note;
                Assert(session.Snapshot.State == ExchangeState.AwaitingPayout && session.Snapshot.ReceivedMinor == total
                    && session.Snapshot.CanExchange && !session.Snapshot.CanReturn, name + " accepts and accumulates before click");
                Assert(devices.Simulator.HopperCoins == stock, name + " never pays before click");
                Assert(store.ReadAll().Single().ReceivedMinor == total, name + " confirmed money durable before click");
            }
            await Task.WhenAll(session.ExchangeAsync(Token), session.ExchangeAsync(Token));
            Assert(session.Snapshot.State == (debt == 0 ? ExchangeState.Ready : ExchangeState.Debt)
                && session.Snapshot.OwedMinor == debt, name + " result");
            Assert(devices.Simulator.HopperCoins == remaining, name + " actual simulated coins");
            var records = store.ReadAll();
            Assert(records.Count == 1 && records.Single().ReceivedMinor == total
                && records.Single().PaidMinor == total - debt, name + " durable total credit/payment");
            if (debt == 0)
            {
                Assert(session.Snapshot.ReceivedMinor == 0 && session.Snapshot.PaidMinor == 0, name + " resets display to zero");
                devices.Simulator.InsertBanknote(1000);
                await devices.BillAcceptor.WaitForIdleAsync(Token);
                Assert(session.Snapshot.ReceivedMinor == 1000, name + " re-enables banknotes after full payout");
            }
            else
            {
                bool blocked = false;
                try { devices.Simulator.InsertBanknote(1000); } catch (InvalidOperationException) { blocked = true; }
                Assert(blocked, name + " debt disables banknotes");
            }
        }
    }
    private static async Task FundedRestart(string root)
    {
        var store = new JsonTransactionStore(Path.Combine(root, "funded-restart.json"));
        var firstDevices = DeviceFactory.Create(new DeviceOptions());
        using (var session = new ExchangeService(firstDevices.BillAcceptor, firstDevices.Hopper, store, 100))
        {
            await session.StartAsync(Token);
            firstDevices.Simulator.InsertBanknote(1000);
            await firstDevices.BillAcceptor.WaitForIdleAsync(Token);
            Assert(store.ReadAll().Single().ReceivedMinor == 1000, "funded before close");
        }
        var devices = DeviceFactory.Create(new DeviceOptions { SimulatorCoins = 100 });
        using (var restarted = new ExchangeService(devices.BillAcceptor, devices.Hopper, store, 100))
        {
            await restarted.StartAsync(Token);
            Assert(restarted.Snapshot.ReceivedMinor == 1000 && restarted.Snapshot.CanExchange, "restores funded wallet for manual click");
            Assert(devices.Simulator.HopperCoins == 100, "funded restart does not automatically pay");
            devices.Simulator.InsertBanknote(2000);
            await devices.BillAcceptor.WaitForIdleAsync(Token);
            Assert(restarted.Snapshot.ReceivedMinor == 3000, "keeps accepting after funded restart");
            await restarted.ExchangeAsync(Token);
            Assert(devices.Simulator.HopperCoins == 70 && restarted.Snapshot.State == ExchangeState.Ready,
                "pays restored and new banknotes exactly once");
        }
    }
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("FAIL: " + message); checks++; }
}
