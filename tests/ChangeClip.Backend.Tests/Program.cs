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
    internal static partial class Program
    {
        private static readonly CancellationToken None = CancellationToken.None;
        private static int Main()
        {
            try { Run().GetAwaiter().GetResult(); Console.WriteLine("PASS: backend wallet, readiness, ledger and resolution scenarios"); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static async Task Run()
        {
            await ImmediateDurableCredit(); await SumMultipleNotes(); await FullPayout(1000, 10); await FullPayout(2000, 20);
            await DoubleClick(); await InFlightDuringDisable(); await Batch300(); await PartialSecondBatch();
            await FundedRestart(); await RestartPayoutBlocked(); await FundingPendingRestart(); await LegacyBlocked();
            await EmptyStartDebt(); await FullPayoutAndEmpty(); await UnknownCount(); await CancellationProgress();
            await LateCreditPreserved(); await JournalFailure(); await DuplicateAcrossRestart(); await DisposeFunded();
            await DisconnectedFunded(); await BatchIntentBeforeSideEffect(); await RejectedDoesNotCredit(); await IdleWithoutStacked(); await CreditThenRejected(); await DisposeInFlight(); await DisposePayout(); AtomicJsonJournal(); await ReadinessScenarios(); LedgerScenarios();
        }
        private sealed class Rig : IDisposable
        {
            public readonly Acceptor Bill = new Acceptor(); public readonly Hopper Coins = new Hopper();
            public readonly MemoryStore Store; public readonly ExchangeService Service;
            public Rig(MemoryStore store = null, bool enforceKnownStock = false) { Store = store ?? new MemoryStore(); Service = new ExchangeService(Bill, Coins, Store, 100, enforceKnownStock); }
            public Task Start() { return Service.StartAsync(None); }
            public void Dispose() { Service.Dispose(); }
        }
        private static async Task ImmediateDurableCredit()
        {
            using (var rig = new Rig())
            {
                await rig.Start(); rig.Bill.Emit(BillEventKind.Reading, 1000, "a"); rig.Bill.Emit(BillEventKind.Credit, 1000, "a");
                Check(rig.Service.Snapshot.ReceivedMinor == 1000 && rig.Store.ReadAll().Single().ReceivedMinor == 1000 && rig.Coins.Payouts == 0 && rig.Bill.Enabled, "Credit must be durable without click or hopper payout");
                Check(rig.Store.ReadAll().Single().Stage == "FundingPending", "Unfinished stacking must be pending");
                rig.Bill.Emit(BillEventKind.Stacked, 1000, "a"); rig.Bill.Emit(BillEventKind.Credit, 1000, "a"); rig.Bill.Emit(BillEventKind.Stacked, 1000, "a");
                Check(rig.Store.ReadAll().Single().Stage == "Funded" && rig.Store.ReadAll().Single().Credits.Count == 1 && rig.Service.Snapshot.ReceivedMinor == 1000 && !rig.Service.Snapshot.CanReturn, "Stacked/duplicate changed wallet or exposed return");
                Check(rig.Bill.Accepts == 0 && rig.Bill.Returns == 0, "Backend called escrow API");
            }
        }
        private static async Task SumMultipleNotes()
        {
            using (var rig = new Rig()) { await rig.Start(); rig.Bill.Insert(1000); rig.Bill.Insert(2000); Check(rig.Service.Snapshot.ReceivedMinor == 3000 && rig.Bill.Enabled, "10+20 must collect 30 while enabled"); await rig.Service.ExchangeAsync(None); Check(rig.Coins.Requests.SequenceEqual(new[] { 30 }) && rig.Store.ReadAll().Single().ReceivedMinor == 3000, "Wrong aggregate payout"); CheckReady(rig); }
        }
        private static void CheckReady(Rig rig)
        { Check(rig.Service.Snapshot.State == ExchangeState.Ready && rig.Service.Snapshot.Message.Contains("Uspješno") && rig.Service.Snapshot.ReceivedMinor == 0 && rig.Service.Snapshot.PaidMinor == 0 && rig.Service.Snapshot.OwedMinor == 0 && rig.Bill.Enabled, "Complete payout must reset Ready0 and re-enable"); }
        private static async Task FullPayout(int amount, int coins)
        {
            using (var rig = new Rig()) { await rig.Start(); rig.Bill.Insert(amount); await rig.Service.ExchangeAsync(None); CheckReady(rig); var row = rig.Store.ReadAll().Single(); Check(row.Stage == "Succeeded" && row.ReceivedMinor == amount && row.PaidMinor == amount && rig.Coins.Requests.Single() == coins, "Incorrect full payout accounting"); }
        }
        private static async Task DoubleClick()
        {
            using (var rig = new Rig())
            {
                await rig.Start(); rig.Bill.Insert(1000); rig.Bill.IdleCompletion = new TaskCompletionSource<bool>();
                var first = rig.Service.ExchangeAsync(None); var second = rig.Service.ExchangeAsync(None); await second;
                Check(rig.Coins.Payouts == 0, "Click bypassed idle drain"); rig.Bill.IdleCompletion.SetResult(true); await first; await rig.Service.ExchangeAsync(None);
                Check(rig.Coins.Payouts == 1 && rig.Bill.IdleCalls == 1, "Double click caused another operation");
            }
        }
        private static async Task InFlightDuringDisable()
        {
            using (var rig = new Rig())
            { await rig.Start(); rig.Bill.Insert(1000); rig.Bill.OnDisable = () => rig.Bill.Emit(BillEventKind.Reading, 2000, "in-flight"); rig.Bill.OnIdle = () => { rig.Bill.Emit(BillEventKind.Credit, 2000, "in-flight"); rig.Bill.Emit(BillEventKind.Stacked, 2000, "in-flight"); }; await rig.Service.ExchangeAsync(None); Check(rig.Coins.Requests.Single() == 30 && rig.Store.ReadAll().Single().ReceivedMinor == 3000, "In-flight note omitted from freeze"); }
        }
        private static async Task Batch300()
        {
            using (var rig = new Rig())
            { await rig.Start(); for (int i = 0; i < 15; i++) rig.Bill.Insert(2000); await rig.Service.ExchangeAsync(None); Check(rig.Coins.Requests.SequenceEqual(new[] { 255, 45 }), "300 coins must split 255+45"); var row = rig.Store.ReadAll().Single(); Check(row.PaidMinor == 30000 && row.Batches.Count == 2 && row.Batches[1].BaseConfirmedCoins == 255 && row.Batches.All(x => x.Stage == "Completed"), "Batch accounting lost cumulative base"); CheckReady(rig); }
        }
        private static async Task PartialSecondBatch()
        {
            using (var rig = new Rig())
            {
                rig.Coins.Results.Enqueue(new PayoutResult(255, 255, PayoutOutcome.Completed)); rig.Coins.Results.Enqueue(new PayoutResult(45, 7, PayoutOutcome.Fault));
                await rig.Start(); for (int i = 0; i < 15; i++) rig.Bill.Insert(2000); await rig.Service.ExchangeAsync(None);
                Check(rig.Service.Snapshot.State == ExchangeState.Debt && rig.Service.Snapshot.PaidMinor == 26200 && rig.Service.Snapshot.OwedMinor == 3800 && rig.Service.Snapshot.IsDebtExact && !rig.Bill.Enabled, "Partial second batch debt must be 38 KM");
                Check(rig.Store.ReadAll().Single().PaidMinor == 26200 && rig.Coins.Payouts == 2, "Partial lost durable base");
            }
        }
        private static async Task FundedRestart()
        {
            var store = new MemoryStore(); using (var first = new Rig(store)) { await first.Start(); first.Bill.Insert(1000); }
            using (var second = new Rig(store)) { await second.Start(); Check(second.Service.Snapshot.State == ExchangeState.AwaitingPayout && second.Service.Snapshot.ReceivedMinor == 1000 && second.Service.Snapshot.CanExchange && second.Coins.Payouts == 0 && second.Bill.Enabled, "Funded restart must allow manual click and collection"); second.Bill.Insert(2000); await second.Service.ExchangeAsync(None); Check(second.Coins.Requests.Single() == 30, "Funded restart+20 lost 10 KM"); }
        }
        private static async Task RestartPayoutBlocked()
        {
            var store = new MemoryStore(); store.Save(Funded("payout", 1000)); var row = store.ReadAll().Single(); row.Stage = "PayoutIntent"; row.PaidMinor = 300; row.Batches.Add(new PayoutBatchRecord { Number = 1, RequestedCoins = 10, ConfirmedCoins = 3, Stage = "Intent" }); store.Save(row);
            using (var rig = new Rig(store)) { await rig.Start(); await rig.Service.ExchangeAsync(None); Check(rig.Coins.Payouts == 0 && !rig.Bill.Enabled && rig.Service.Snapshot.State == ExchangeState.ReconciliationRequired && rig.Service.Snapshot.PaidMinor == 300 && !rig.Service.Snapshot.IsDebtExact, "Restart replayed payout intent"); }
        }
        private static async Task FundingPendingRestart()
        {
            var store = new MemoryStore(); var row = Funded("pending", 1000); row.Stage = "FundingPending"; row.HasPendingNote = true; row.Credits[0].Stacked = false; store.Save(row);
            using (var rig = new Rig(store)) { await rig.Start(); Check(rig.Service.Snapshot.State == ExchangeState.ReconciliationRequired && rig.Service.Snapshot.ReceivedMinor == 1000 && !rig.Bill.Enabled, "Pending funding resumed unsafely"); }
        }
        private static async Task LegacyBlocked()
        {
            var store = new MemoryStore(); store.Save(new TransactionRecord { Id = "legacy", Stage = "Credited", ReceivedMinor = 1000, AmountMinor = 1000, CoinValueMinor = 100, IsDebtExact = true });
            using (var rig = new Rig(store)) { await rig.Start(); Check(rig.Service.Snapshot.State == ExchangeState.ReconciliationRequired && rig.Service.Snapshot.ReceivedMinor == 1000 && store.ReadAll().Single().ReceivedMinor == 1000 && rig.Coins.Payouts == 0, "Legacy unresolved money discarded or replayed"); }
        }
        private static async Task EmptyStartDebt()
        {
            using (var rig = new Rig()) { rig.Coins.Status = new HopperStatus(true, true, 0); await rig.Start(); Check(rig.Bill.Enabled, "Connected empty hopper must allow collection"); rig.Bill.Insert(1000); await rig.Service.ExchangeAsync(None); Check(rig.Service.Snapshot.State == ExchangeState.Debt && rig.Service.Snapshot.OwedMinor == 1000 && rig.Service.Snapshot.PaidMinor == 0 && rig.Coins.Payouts == 0 && !rig.Bill.Enabled && rig.Store.ReadAll().Single().Stage == "Debt", "Empty hopper must preserve 10 KM debt"); }
        }
        private static async Task FullPayoutAndEmpty()
        {
            using (var rig = new Rig()) { rig.Coins.Results.Enqueue(new PayoutResult(10, 10, PayoutOutcome.Empty)); rig.Coins.EmptyAfterPayout = true; await rig.Start(); rig.Bill.Insert(1000); await rig.Service.ExchangeAsync(None); CheckReady(rig); Check(rig.Store.ReadAll().Single().Stage == "Succeeded", "Last coin Empty was not successful"); }
        }
        private static async Task UnknownCount()
        {
            using (var rig = new Rig()) { rig.Coins.Results.Enqueue(new PayoutResult(10, 7, PayoutOutcome.Uncertain, false)); await rig.Start(); rig.Bill.Insert(1000); await rig.Service.ExchangeAsync(None); Check(rig.Service.Snapshot.State == ExchangeState.ReconciliationRequired && !rig.Service.Snapshot.IsDebtExact && !rig.Bill.Enabled && rig.Store.ReadAll().Single().PaidMinor == 700, "Unknown count was exact or progress lost"); }
        }
        private static async Task CancellationProgress()
        {
            using (var rig = new Rig()) using (var cancel = new CancellationTokenSource())
            { rig.Coins.CancelPayout = cancel; await rig.Start(); rig.Bill.Insert(1000); await rig.Service.ExchangeAsync(cancel.Token); Check(rig.Service.Snapshot.State == ExchangeState.ReconciliationRequired && rig.Store.ReadAll().Single().PaidMinor == 300 && !rig.Service.Snapshot.IsDebtExact, "Cancellation lost confirmed payout progress"); }
        }
        private static async Task LateCreditPreserved()
        {
            using (var rig = new Rig())
            { await rig.Start(); rig.Bill.Insert(1000); rig.Coins.OnPayoutStatus = () => rig.Bill.Emit(BillEventKind.Credit, 2000, "late"); await rig.Service.ExchangeAsync(None); Check(rig.Service.Snapshot.State == ExchangeState.ReconciliationRequired && rig.Store.ReadAll().Single().ReceivedMinor == 3000 && rig.Coins.Payouts == 0, "Late cash event ignored or wrong frozen payout executed"); }
        }
        private static async Task JournalFailure()
        {
            using (var rig = new Rig())
            { await rig.Start(); rig.Store.FailOnStage = "FundingPending"; rig.Bill.Insert(1000); Check(!rig.Bill.Enabled && rig.Service.Snapshot.State == ExchangeState.ReconciliationRequired && rig.Service.Snapshot.ReceivedMinor == 1000 && rig.Store.ReadAll().Single().ReceivedMinor == 1000, "Credit write error silently lost cash or left input enabled"); }
            using (var rig = new Rig())
            { await rig.Start(); rig.Bill.Insert(1000); rig.Store.FailOnStage = "PayoutIntent"; await rig.Service.ExchangeAsync(None); Check(rig.Coins.Payouts == 0 && rig.Service.Snapshot.State == ExchangeState.ReconciliationRequired, "Intent failure allowed hopper side effect"); }
        }
        private static async Task DuplicateAcrossRestart()
        {
            var store = new MemoryStore(); using (var first = new Rig(store)) { await first.Start(); first.Bill.Insert(1000, "persisted-id"); }
            using (var second = new Rig(store)) { await second.Start(); second.Bill.Emit(BillEventKind.Credit, 1000, "persisted-id"); second.Bill.Emit(BillEventKind.Stacked, 1000, "persisted-id"); Check(second.Service.Snapshot.ReceivedMinor == 1000 && store.ReadAll().Single().Credits.Count == 1, "Restart duplicate credited twice"); }
        }
        private static async Task DisposeFunded()
        {
            var store = new MemoryStore(); var rig = new Rig(store); await rig.Start(); rig.Bill.Insert(2000); rig.Dispose(); var row = store.ReadAll().Single(); Check(row.Stage == "Funded" && row.ReceivedMinor == 2000 && row.PaidMinor == 0 && rig.Bill.Returns == 0, "Dispose returned or damaged funded cash");
        }
        private static async Task DisconnectedFunded()
        {
            var store = new MemoryStore(); store.Save(Funded("offline", 1000)); using (var rig = new Rig(store)) { rig.Coins.Status = new HopperStatus(false, false); await rig.Start(); Check(rig.Service.Snapshot.ReceivedMinor == 1000 && !rig.Bill.Enabled && rig.Coins.Payouts == 0 && store.ReadAll().Single().Stage == "Funded", "Startup connection failure hid funded balance"); }
        }
        private static async Task BatchIntentBeforeSideEffect()
        {
            using (var rig = new Rig())
            { await rig.Start(); for (int i = 0; i < 15; i++) rig.Bill.Insert(2000); rig.Coins.BeforePayout = () => { var row = rig.Store.ReadAll().Single(); Check(row.Stage == "PayoutIntent" && row.Batches.Last().Stage == "Intent" && row.Batches.Last().Number == rig.Coins.Payouts, "Batch command preceded its durable intent"); }; await rig.Service.ExchangeAsync(None); Check(rig.Coins.Payouts == 2, "Batch intent test did not reach second command"); }
        }
        private static async Task RejectedDoesNotCredit()
        {
            using (var rig = new Rig()) { await rig.Start(); rig.Bill.Emit(BillEventKind.Reading, 0, "bad-note"); rig.Bill.Emit(BillEventKind.Rejected, 0, "bad-note"); Check(rig.Service.Snapshot.State == ExchangeState.Ready && rig.Service.Snapshot.ReceivedMinor == 0 && rig.Coins.Payouts == 0, "Rejected note credited or blocked wallet"); }
        }
        private static async Task IdleWithoutStacked()
        {
            using (var rig = new Rig())
            { await rig.Start(); rig.Bill.Emit(BillEventKind.Credit, 1000, "not-stacked"); await rig.Service.ExchangeAsync(None); Check(rig.Service.Snapshot.State == ExchangeState.ReconciliationRequired && rig.Coins.Payouts == 0 && !rig.Store.ReadAll().Single().Credits.Single().Stacked, "Idle invented STACKED evidence"); }
        }
        private static async Task CreditThenRejected()
        {
            using (var rig = new Rig())
            { await rig.Start(); rig.Bill.Emit(BillEventKind.Credit, 1000, "contradiction"); rig.Bill.Emit(BillEventKind.Rejected, 0, "contradiction"); await rig.Service.ExchangeAsync(None); Check(rig.Service.Snapshot.State == ExchangeState.ReconciliationRequired && rig.Store.ReadAll().Single().ReceivedMinor == 1000 && rig.Coins.Payouts == 0 && !rig.Bill.Enabled, "CREDIT→REJECTED was silently funded"); }
        }
        private static async Task DisposeInFlight()
        {
            var store = new MemoryStore(); var rig = new Rig(store); await rig.Start(); rig.Bill.Insert(1000);
            rig.Bill.OnDisable = () => rig.Bill.Emit(BillEventKind.Reading, 2000, "closing-note");
            rig.Bill.OnIdle = () => { rig.Bill.Emit(BillEventKind.Credit, 2000, "closing-note"); rig.Bill.Emit(BillEventKind.Stacked, 2000, "closing-note"); };
            rig.Dispose(); var row = store.ReadAll().Single(); Check(row.Stage == "Funded" && row.ReceivedMinor == 3000 && row.Credits.Count == 2 && row.Credits.All(x => x.Stacked), "Dispose detached before in-flight cash settled");
        }
        private static async Task DisposePayout()
        {
            using (var rig = new Rig())
            { await rig.Start(); rig.Bill.Insert(1000); rig.Coins.BeforePayout = () => rig.Service.Dispose(); await rig.Service.ExchangeAsync(None); Check(rig.Store.ReadAll().Single().Stage == "ReconciliationRequired" && !rig.Store.ReadAll().Single().IsDebtExact && rig.Coins.Payouts == 1, "Dispose lost unresolved payout intent"); }
        }
        private static TransactionRecord Funded(string id, int amount)
        { return new TransactionRecord { Id = id, SchemaVersion = 2, Stage = "Funded", AmountMinor = amount, ReceivedMinor = amount, CoinValueMinor = 100, IsDebtExact = true, Credits = new List<CreditRecord> { new CreditRecord { EventId = id + "-credit", AmountMinor = amount, Currency = "BAM", Stacked = true } }, Batches = new List<PayoutBatchRecord>() }; }
        private static void AtomicJsonJournal()
        {
            var directory = Path.Combine(Path.GetTempPath(), "ChangeClip.Backend.Tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); var path = Path.Combine(directory, "journal.json");
            try
            {
                var store = new JsonTransactionStore(path); var row = Funded("json", 1000); store.Save(row); row.Stage = "Debt"; row.PaidMinor = 700; row.Batches.Add(new PayoutBatchRecord { Number = 1, RequestedCoins = 10, ConfirmedCoins = 7, Stage = "Partial" }); store.Save(row);
                var restored = new JsonTransactionStore(path).ReadAll().Single(); Check(restored.Stage == "Debt" && restored.PaidMinor == 700 && restored.Credits.Single().Stacked && restored.Batches.Single().ConfirmedCoins == 7, "JSON v2 roundtrip failed"); restored.Credits[0].AmountMinor = 0; Check(store.ReadAll().Single().Credits[0].AmountMinor == 1000, "Journal leaked mutable nested records");
                File.WriteAllText(path, "[{\"Id\":\"legacy-json\",\"Stage\":\"PayoutIntent\",\"ReceivedMinor\":1000,\"PaidMinor\":700,\"CoinValueMinor\":100,\"IsDebtExact\":true}]");
                Check(store.ReadAll().Single().SchemaVersion == 0 && store.ReadAll().Single().Credits == null, "Legacy schema not readable");
                using (var session = new ExchangeService(new Acceptor(), new Hopper(), store, 100))
                { session.StartAsync(None).GetAwaiter().GetResult(); Check(session.Snapshot.State == ExchangeState.ReconciliationRequired && session.Snapshot.ReceivedMinor == 1000 && session.Snapshot.PaidMinor == 700, "Legacy JSON amounts lost on migration"); }
                File.WriteAllText(path, "broken"); bool failed = false; try { store.ReadAll(); } catch { failed = true; } Check(failed, "Corrupt journal silently reset");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                // Windows can briefly retain an already-deleted file after atomic replacement.
                // Retry only an empty directory; unexpected files must still fail this test.
                for (int attempt = 0; ; attempt++)
                {
                    try { Directory.Delete(directory); break; }
                    catch (IOException) when (attempt < 5 && !Directory.EnumerateFileSystemEntries(directory).Any())
                    { Thread.Sleep(50); }
                }
            }
        }
        private sealed class MemoryStore : ITransactionStore
        {
            private readonly List<TransactionRecord> rows = new List<TransactionRecord>(); public string FailOnStage;
            public IReadOnlyList<TransactionRecord> ReadAll() { lock (rows) return rows.Select(x => x.Copy()).ToArray(); }
            public void Save(TransactionRecord row) { lock (rows) { if (row.Stage == FailOnStage) throw new IOException("Injected journal failure"); rows.RemoveAll(x => x.Id == row.Id); rows.Add(row.Copy()); } }
        }
        private sealed class Acceptor : IBillAcceptor
        {
            public event EventHandler<BillEventArgs> BillEvent;
            public bool Enabled; public int Accepts, Returns, IdleCalls; public Action OnDisable, OnIdle; public TaskCompletionSource<bool> IdleCompletion;
            public void Emit(BillEventKind kind, int amount, string id) { BillEvent?.Invoke(this, new BillEventArgs(kind, amount, "BAM", eventId: id)); }
            public void Insert(int amount, string id = null) { Check(Enabled, "Stub input disabled"); id = id ?? Guid.NewGuid().ToString("N"); Emit(BillEventKind.Reading, amount, id); Emit(BillEventKind.Credit, amount, id); Emit(BillEventKind.Stacked, amount, id); }
            public Task ConnectAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
            public Task SetEnabledAsync(bool enabled, CancellationToken token) { token.ThrowIfCancellationRequested(); Enabled = enabled; if (!enabled) { var callback = OnDisable; OnDisable = null; callback?.Invoke(); } return Task.CompletedTask; }
            public Task WaitForIdleAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); IdleCalls++; var callback = OnIdle; OnIdle = null; callback?.Invoke(); return IdleCompletion == null ? Task.CompletedTask : IdleCompletion.Task; }
            public Task AcceptEscrowAsync(CancellationToken token) { Accepts++; throw new Exception("Backend must not accept escrow"); }
            public Task ReturnEscrowAsync(CancellationToken token) { Returns++; throw new Exception("Backend must not return stacked notes"); }
            public void Dispose() { }
        }
        private sealed class Hopper : ICoinHopper
        {
            public int Payouts; public readonly List<int> Requests = new List<int>(); public readonly Queue<PayoutResult> Results = new Queue<PayoutResult>();
            public HopperStatus Status = new HopperStatus(true, false, null); public bool EmptyAfterPayout; public Action OnPayoutStatus, BeforePayout; public CancellationTokenSource CancelPayout;
            public Task ConnectAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
            public Task<HopperStatus> GetStatusAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); var callback = OnPayoutStatus; OnPayoutStatus = null; callback?.Invoke(); return Task.FromResult(Status); }
            public Task<PayoutResult> PayoutAsync(int count, IProgress<int> progress, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); Payouts++; Requests.Add(count); BeforePayout?.Invoke();
                if (CancelPayout != null) { progress.Report(3); CancelPayout.Cancel(); token.ThrowIfCancellationRequested(); }
                var result = Results.Count == 0 ? new PayoutResult(count, count, PayoutOutcome.Completed) : Results.Dequeue();
                for (int i = 1; i <= result.ConfirmedCoins; i++) progress.Report(i);
                if (EmptyAfterPayout) Status = new HopperStatus(true, true, 0); return Task.FromResult(result);
            }
            public Task StopAsync(CancellationToken token) { return Task.CompletedTask; }
            public void Dispose() { }
        }
    }
}
