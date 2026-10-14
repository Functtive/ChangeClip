using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChangeClip.Backend;
using ChangeClip.Contracts;

namespace ChangeClip.Backend.Tests
{
    internal static class Program
    {
        private static readonly CancellationToken None = CancellationToken.None;
        private static int Main()
        {
            try { Run().GetAwaiter().GetResult(); Console.WriteLine("PASS: all backend scenarios"); return 0; }
            catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static async Task Run()
        {
            await ReturnWithoutPayout();
            await NormalPayout(1000, 10);
            await NormalPayout(2000, 20);
            await PartialDebt();
            await FullAndEmpty();
            await DuplicateCreditAndClick();
            await RestartBlocked();
            await UnknownCount();
            await AwaitFinalCredit();
            await AwaitRejected();
            await CancellationDuringPayout();
            await FaultDuringEscrowCheck();
            await JournalFailureBlocksSideEffect();
            await UnsupportedBillReturns();
            await DebtSurvivesRestart();
            await NextNoteKeepsSuccessVisible();
            await CreditIsDurableBeforeAcceptReturns();
            await DebtStaysExactOnDisconnect();
            await DisposeDuringPayout();
            AtomicJsonJournal();
        }
        private sealed class Rig : IDisposable
        {
            public readonly Acceptor Bill = new Acceptor();
            public readonly Hopper Coins = new Hopper();
            public readonly MemoryStore Store;
            public readonly ExchangeService Service;
            public Rig(MemoryStore store = null) { Store = store ?? new MemoryStore(); Service = new ExchangeService(Bill, Coins, Store, 100); }
            public async Task Start(int amount = 1000) { await Service.StartAsync(None); Bill.Emit(BillEventKind.Escrow, amount, "escrow"); }
            public void Dispose() { Service.Dispose(); }
        }
        private static async Task ReturnWithoutPayout()
        {
            using (var rig = new Rig())
            { await rig.Start(); await rig.Service.ReturnAsync(None); Check(rig.Bill.Returns == 1 && rig.Coins.Payouts == 0 && rig.Store.ReadAll().Count == 0 && rig.Service.Snapshot.State == ExchangeState.Returned, "Return must not credit or pay"); }
        }
        private static async Task NormalPayout(int amount, int expected)
        {
            using (var rig = new Rig())
            {
                await rig.Start(amount); await rig.Service.ExchangeAsync(None);
                Check(rig.Service.Snapshot.State == ExchangeState.Succeeded && rig.Service.Snapshot.ReceivedMinor == amount && rig.Service.Snapshot.PaidMinor == amount && rig.Service.Snapshot.OwedMinor == 0 && rig.Coins.Requested == expected, "Wrong full payout");
                Check(rig.Store.ReadAll().Single().Stage == "Succeeded", "Success not durable");
            }
        }
        private static async Task PartialDebt()
        {
            using (var rig = new Rig())
            {
                rig.Coins.Result = new PayoutResult(10, 7, PayoutOutcome.Empty);
                await rig.Start(); await rig.Service.ExchangeAsync(None);
                Check(rig.Service.Snapshot.State == ExchangeState.Debt && rig.Service.Snapshot.PaidMinor == 700 && rig.Service.Snapshot.OwedMinor == 300 && rig.Service.Snapshot.IsDebtExact && !rig.Bill.Enabled, "7/10 must preserve exact 3 KM debt");
                rig.Bill.Emit(BillEventKind.Escrow, 1000, "blocked"); await rig.Service.ExchangeAsync(None); Check(rig.Coins.Payouts == 1, "Debt permitted another payout");
            }
        }
        private static async Task FullAndEmpty()
        {
            using (var rig = new Rig())
            {
                rig.Coins.Result = new PayoutResult(10, 10, PayoutOutcome.Empty); rig.Coins.EmptyAfterPayout = true;
                await rig.Start(); await rig.Service.ExchangeAsync(None);
                Check(rig.Service.Snapshot.State == ExchangeState.Succeeded && rig.Service.Snapshot.OwedMinor == 0 && !rig.Bill.Enabled, "Last coin Empty must still succeed");
            }
        }
        private static async Task DuplicateCreditAndClick()
        {
            using (var rig = new Rig())
            {
                rig.Bill.AutoCredit = false; await rig.Start();
                var first = rig.Service.ExchangeAsync(None); var second = rig.Service.ExchangeAsync(None);
                rig.Bill.Emit(BillEventKind.Credit, 1000, "same-credit"); rig.Bill.Emit(BillEventKind.Credit, 1000, "same-credit"); rig.Bill.Emit(BillEventKind.Stacked, 1000, "same-credit");
                await Task.WhenAll(first, second); await rig.Service.ExchangeAsync(None);
                rig.Bill.Emit(BillEventKind.Credit, 1000, "same-credit");
                Check(rig.Coins.Payouts == 1 && rig.Bill.Accepts == 1 && rig.Store.ReadAll().Count == 1, "Duplicate caused extra cash action");
            }
        }
        private static async Task RestartBlocked()
        {
            var store = new MemoryStore(); store.Save(new TransactionRecord { Id = "interrupted", Stage = "PayoutIntent", AmountMinor = 1000, ReceivedMinor = 1000, PaidMinor = 400, CoinValueMinor = 100, IsDebtExact = true });
            using (var rig = new Rig(store))
            { await rig.Service.StartAsync(None); await rig.Service.ExchangeAsync(None); Check(rig.Coins.Payouts == 0 && !rig.Bill.Enabled && rig.Service.Snapshot.State == ExchangeState.ReconciliationRequired && !rig.Service.Snapshot.IsDebtExact, "Restart replayed unresolved payout"); }
        }
        private static async Task UnknownCount()
        {
            using (var rig = new Rig())
            { rig.Coins.Result = new PayoutResult(10, 7, PayoutOutcome.Fault, false); await rig.Start(); await rig.Service.ExchangeAsync(None); Check(rig.Service.Snapshot.State == ExchangeState.ReconciliationRequired && !rig.Service.Snapshot.IsDebtExact && rig.Store.ReadAll().Single().Stage == "ReconciliationRequired", "Unknown count was treated as exact debt"); }
        }
        private static async Task AwaitFinalCredit()
        {
            using (var rig = new Rig())
            {
                rig.Bill.AutoCredit = false; await rig.Start(); rig.Bill.Emit(BillEventKind.Reading, 1000, "reading");
                var task = rig.Service.ExchangeAsync(None); Check(rig.Coins.Payouts == 0 && !task.IsCompleted, "ACK or escrow caused payout");
                rig.Bill.Emit(BillEventKind.Stacked, 1000, "stacked-only"); await task; Check(rig.Coins.Payouts == 1, "Final Stacked not processed");
            }
        }
        private static async Task AwaitRejected()
        {
            using (var rig = new Rig())
            { rig.Bill.AutoReject = false; await rig.Start(); var task = rig.Service.ReturnAsync(None); Check(!task.IsCompleted && rig.Service.Snapshot.State != ExchangeState.Returned, "Return ACK mistaken for confirmed rejection"); rig.Bill.Emit(BillEventKind.Rejected, 0, "rejected"); await task; Check(rig.Service.Snapshot.State == ExchangeState.Returned, "Rejected not finalized"); }
        }
        private static async Task CancellationDuringPayout()
        {
            using (var rig = new Rig()) using (var cancel = new CancellationTokenSource())
            {
                rig.Coins.CancelDuringPayout = cancel; await rig.Start(); await rig.Service.ExchangeAsync(cancel.Token);
                Check(rig.Service.Snapshot.State == ExchangeState.ReconciliationRequired && !rig.Service.Snapshot.IsDebtExact && rig.Store.ReadAll().Single().PaidMinor == 300, "Cancellation lost durable confirmed progress");
            }
        }
        private static async Task FaultDuringEscrowCheck()
        {
            using (var rig = new Rig())
            { await rig.Start(); rig.Coins.Status = new HopperStatus(true, true); await rig.Service.ExchangeAsync(None); Check(rig.Service.Snapshot.CanReturn && !rig.Service.Snapshot.CanExchange && rig.Bill.Accepts == 0, "Unusable hopper accepted a note"); await rig.Service.ReturnAsync(None); }
        }
        private static async Task JournalFailureBlocksSideEffect()
        {
            using (var rig = new Rig())
            { await rig.Start(); rig.Store.FailOnStage = "AcceptIntent"; await rig.Service.ExchangeAsync(None); Check(rig.Bill.Accepts == 0 && rig.Coins.Payouts == 0 && rig.Service.Snapshot.State == ExchangeState.ReconciliationRequired, "Journal failure allowed acceptance"); }
            using (var rig = new Rig())
            { await rig.Start(); rig.Store.FailOnStage = "PayoutIntent"; await rig.Service.ExchangeAsync(None); Check(rig.Bill.Accepts == 1 && rig.Coins.Payouts == 0 && rig.Store.ReadAll().Single().ReceivedMinor == 1000, "Journal failure allowed payout"); }
        }
        private static async Task UnsupportedBillReturns()
        {
            using (var rig = new Rig())
            { await rig.Service.StartAsync(None); rig.Bill.Emit(BillEventKind.Escrow, 5000, "unsupported"); Check(!rig.Service.Snapshot.CanExchange && rig.Service.Snapshot.CanReturn, "Unsupported denomination allowed"); await rig.Service.ReturnAsync(None); Check(rig.Coins.Payouts == 0, "Unsupported note paid"); }
        }
        private static async Task DebtSurvivesRestart()
        {
            var store = new MemoryStore();
            using (var rig = new Rig(store)) { rig.Coins.Result = new PayoutResult(10, 7, PayoutOutcome.Empty); await rig.Start(); await rig.Service.ExchangeAsync(None); }
            using (var rig = new Rig(store)) { await rig.Service.StartAsync(None); Check(rig.Service.Snapshot.State == ExchangeState.Debt && rig.Service.Snapshot.OwedMinor == 300 && rig.Service.Snapshot.IsDebtExact && rig.Coins.Payouts == 0, "Restart erased exact debt"); }
        }
        private static async Task NextNoteKeepsSuccessVisible()
        {
            using (var rig = new Rig())
            { await rig.Start(); await rig.Service.ExchangeAsync(None); Check(rig.Service.Snapshot.State == ExchangeState.Succeeded && rig.Bill.Enabled, "Success did not remain visible"); rig.Bill.Emit(BillEventKind.Escrow, 2000, "second"); await rig.Service.ExchangeAsync(None); Check(rig.Coins.Payouts == 2 && rig.Store.ReadAll().Count == 2 && rig.Service.Snapshot.PaidMinor == 2000, "Second independent note failed"); }
        }
        private static async Task CreditIsDurableBeforeAcceptReturns()
        {
            using (var rig = new Rig())
            {
                rig.Bill.AcceptCompletion = new TaskCompletionSource<bool>();
                await rig.Start(); var exchange = rig.Service.ExchangeAsync(None);
                Check(rig.Store.ReadAll().Single().Stage == "Credited" && rig.Store.ReadAll().Single().ReceivedMinor == 1000 && rig.Coins.Payouts == 0, "Credit was not durable while adapter waits for Stacked");
                rig.Bill.AcceptCompletion.SetResult(true); await exchange;
                Check(rig.Coins.Payouts == 1, "Adapter completion failed to start payout");
            }
        }
        private static async Task DebtStaysExactOnDisconnect()
        {
            using (var rig = new Rig())
            {
                rig.Coins.Result = new PayoutResult(10, 7, PayoutOutcome.Empty); await rig.Start(); await rig.Service.ExchangeAsync(None);
                rig.Bill.Emit(BillEventKind.Disconnected, 0, "disconnect");
                Check(rig.Service.Snapshot.State == ExchangeState.Debt && rig.Service.Snapshot.IsDebtExact && rig.Store.ReadAll().Single().Stage == "Debt", "Disconnect corrupted confirmed debt");
            }
        }
        private static async Task DisposeDuringPayout()
        {
            using (var rig = new Rig())
            {
                rig.Coins.OnPayout = () => rig.Service.Dispose(); await rig.Start(); await rig.Service.ExchangeAsync(None);
                Check(rig.Store.ReadAll().Single().Stage == "ReconciliationRequired" && !rig.Store.ReadAll().Single().IsDebtExact && rig.Coins.Payouts == 1, "Dispose lost unresolved payout");
            }
        }
        private static void AtomicJsonJournal()
        {
            var directory = Path.Combine(Path.GetTempPath(), "ChangeClip.Backend.Tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "journal.json");
            try
            {
                var store = new JsonTransactionStore(path); var record = new TransactionRecord { Id = "json", Stage = "Credited", AmountMinor = 1000, ReceivedMinor = 1000, CoinValueMinor = 100, IsDebtExact = true };
                store.Save(record); record.Stage = "Debt"; record.PaidMinor = 700; store.Save(record);
                var restored = new JsonTransactionStore(path).ReadAll().Single(); Check(restored.Stage == "Debt" && restored.PaidMinor == 700, "JSON update failed");
                restored.PaidMinor = 0; Check(store.ReadAll().Single().PaidMinor == 700, "Read leaked mutable journal record");
                File.WriteAllText(path, "broken"); bool failed = false; try { store.ReadAll(); } catch { failed = true; } Check(failed, "Corrupt journal silently reset");
            }
            finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(directory); }
        }
        private sealed class MemoryStore : ITransactionStore
        {
            private readonly List<TransactionRecord> rows = new List<TransactionRecord>(); public string FailOnStage;
            public IReadOnlyList<TransactionRecord> ReadAll() { lock (rows) return rows.Select(x => x.Copy()).ToArray(); }
            public void Save(TransactionRecord record)
            { lock (rows) { if (record.Stage == FailOnStage) throw new IOException("Injected journal failure"); rows.RemoveAll(x => x.Id == record.Id); rows.Add(record.Copy()); } }
        }
        private sealed class Acceptor : IBillAcceptor
        {
            public event EventHandler<BillEventArgs> BillEvent;
            public TaskCompletionSource<bool> AcceptCompletion;
            public bool Enabled, AutoCredit = true, AutoReject = true; public int Accepts, Returns; private int amount; private int serial;
            public void Emit(BillEventKind kind, int value, string id)
            { if (kind == BillEventKind.Escrow) amount = value; BillEvent?.Invoke(this, new BillEventArgs(kind, value, "BAM", eventId: id)); }
            public Task ConnectAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
            public Task SetEnabledAsync(bool enabled, CancellationToken token) { token.ThrowIfCancellationRequested(); Enabled = enabled; return Task.CompletedTask; }
            public Task AcceptEscrowAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Accepts++; if (AutoCredit) Emit(BillEventKind.Credit, amount, "credit-" + ++serial); return AcceptCompletion == null ? Task.CompletedTask : AcceptCompletion.Task; }
            public Task ReturnEscrowAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Returns++; if (AutoReject) Emit(BillEventKind.Rejected, 0, "reject-" + ++serial); return Task.CompletedTask; }
            public void Dispose() { }
        }
        private sealed class Hopper : ICoinHopper
        {
            public Action OnPayout;
            public int Payouts, Requested; public bool EmptyAfterPayout; public PayoutResult Result; public CancellationTokenSource CancelDuringPayout;
            public HopperStatus Status = new HopperStatus(true, false, null);
            public Task ConnectAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
            public Task<HopperStatus> GetStatusAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Status); }
            public Task<PayoutResult> PayoutAsync(int coins, IProgress<int> progress, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); Payouts++; Requested = coins; OnPayout?.Invoke(); token.ThrowIfCancellationRequested();
                if (CancelDuringPayout != null) { progress.Report(3); CancelDuringPayout.Cancel(); token.ThrowIfCancellationRequested(); }
                var result = Result ?? new PayoutResult(coins, coins, PayoutOutcome.Completed);
                for (var count = 1; count <= result.ConfirmedCoins; count++) progress.Report(count);
                if (EmptyAfterPayout) Status = new HopperStatus(true, true, 0);
                return Task.FromResult(result);
            }
            public Task StopAsync(CancellationToken token) { return Task.CompletedTask; }
            public void Dispose() { }
        }
    }
}
