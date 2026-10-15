using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChangeClip.Backend;
using ChangeClip.Contracts;

namespace ChangeClip.Backend.Tests
{
    internal static partial class Program
    {
        private static async Task ReadinessScenarios()
        {
            using (var rig = new Rig(enforceKnownStock: true))
            {
                rig.Coins.Status = new HopperStatus(true, false, 19); await rig.Start();
                Check(!rig.Bill.Enabled && !rig.Service.Snapshot.CanAcceptBanknotes && rig.Service.EnforceKnownStock, "Known 19 KM cannot cover next 20 KM note");
                rig.Coins.Status = new HopperStatus(true, false, 30); await rig.Service.RefreshReadinessAsync(None);
                Check(rig.Bill.Enabled && rig.Service.Snapshot.CanAcceptBanknotes, "Refill/refresh did not enable confirmed input");
                rig.Bill.Insert(1000); Check(rig.Bill.Enabled, "30 KM stock covers wallet10 + next20");
                rig.Bill.Insert(1000); Check(!rig.Bill.Enabled && !rig.Service.Snapshot.CanAcceptBanknotes && rig.Service.Snapshot.CanExchange && rig.Service.Snapshot.ReceivedMinor == 2000, "Stacked did not stop input at known shortage");
                await rig.Service.ExchangeAsync(None); Check(rig.Coins.Requests.Single() == 20 && rig.Service.Snapshot.State == ExchangeState.Ready, "Existing funded wallet could not pay during intake shortage");
            }
            using (var rig = new Rig(enforceKnownStock: true))
            {
                rig.Coins.Status = new HopperStatus(true, true, null); await rig.Start();
                Check(!rig.Bill.Enabled && !rig.Service.Snapshot.CanAcceptBanknotes, "Known Empty must stop input even with unknown inventory count");
                rig.Coins.Status = new HopperStatus(true, false, null); await rig.Service.RefreshReadinessAsync(None);
                Check(rig.Bill.Enabled && rig.Service.Snapshot.CanAcceptBanknotes && rig.Service.Snapshot.Message.Contains("nije poznata"), "Unknown stock should explicitly allow accounting-backed intake");
                rig.Bill.Insert(1000); rig.Coins.Status = new HopperStatus(true, true, 0); await rig.Service.ExchangeAsync(None);
                Check(rig.Service.Snapshot.State == ExchangeState.Debt, "Empty after actual funded cash did not preserve debt");
                rig.Coins.Status = new HopperStatus(true, false, 500); await rig.Service.RefreshReadinessAsync(None);
                Check(rig.Service.Snapshot.State == ExchangeState.Debt && !rig.Bill.Enabled && rig.Coins.Payouts == 0, "Refresh reopened debt or replayed payout");
            }
            using (var rig = new Rig(enforceKnownStock: true))
            {
                await rig.Start(); rig.Bill.Emit(BillEventKind.Credit, 1000, "pending-refresh"); await rig.Service.ExchangeAsync(None);
                rig.Coins.Status = new HopperStatus(true, false, 100); await rig.Service.RefreshReadinessAsync(None);
                Check(rig.Service.Snapshot.State == ExchangeState.ReconciliationRequired && !rig.Bill.Enabled && rig.Coins.Payouts == 0, "Refresh reopened ambiguous cash");
            }
            var store = new MemoryStore(); var first = Funded("claim-a", 1000); first.Stage = TransactionStages.Debt; first.PaidMinor = 700;
            var second = Funded("claim-b", 2000); second.Stage = TransactionStages.ReconciliationRequired; second.IsDebtExact = false; second.PaidMinor = 400;
            store.Save(first); store.Save(second);
            using (var rig = new Rig(store, true))
            {
                await rig.Start(); await rig.Service.RefreshReadinessAsync(None); await rig.Service.ExchangeAsync(None);
                Check(rig.Service.Snapshot.UnresolvedClaims.Count == 2 && rig.Service.Snapshot.TransactionId == null && rig.Service.Snapshot.ReceivedMinor == 0 && !rig.Bill.Enabled, "Multiple claims were collapsed into the last wallet");
                Check(rig.Service.Snapshot.UnresolvedClaims.Single(x => x.TransactionId == first.Id).PaidMinor == 700 && rig.Service.Snapshot.UnresolvedClaims.Single(x => x.TransactionId == second.Id).ReceivedMinor == 2000, "Claim evidence omitted");
            }
            var after = store.ReadAll(); Check(after.Count == 2 && after.Single(x => x.Id == first.Id).Stage == TransactionStages.Debt && after.Single(x => x.Id == second.Id).Stage == TransactionStages.ReconciliationRequired, "Multiple startup changed old claims or created empty third record");
            await EnableAckIsTruthful();
            await ReadinessAckRace();
            using (var rig = new Rig(enforceKnownStock: true))
            {
                rig.Coins.Status = new HopperStatus(true, false, 30); rig.Coins.EmptyAfterPayout = true; await rig.Start(); rig.Bill.Insert(1000); await rig.Service.ExchangeAsync(None);
                Check(rig.Service.Snapshot.State == ExchangeState.Ready && !rig.Service.Snapshot.CanAcceptBanknotes && rig.Service.Snapshot.Message.Contains("Uspješno") && !rig.Service.Snapshot.Message.Contains("Ubacite"), "Full payout shortage invited a new deposit before readiness ACK");
            }
        }
        private static async Task EnableAckIsTruthful()
        {
            var bill = new DelayedEnableAcceptor(); var coins = new Hopper();
            using (var service = new ExchangeService(bill, coins, new MemoryStore(), 100))
            {
                var start = service.StartAsync(None);
                Check(!start.IsCompleted && !service.Snapshot.CanAcceptBanknotes, "Input readiness became true before enable ACK");
                bill.EnableAck.SetResult(true); await start;
                Check(service.Snapshot.CanAcceptBanknotes, "Input readiness did not track confirmed enable ACK");
            }
        }
        private sealed class DelayedEnableAcceptor : IBillAcceptor
        {
            public event EventHandler<BillEventArgs> BillEvent { add { } remove { } }
            public readonly TaskCompletionSource<bool> EnableAck = new TaskCompletionSource<bool>();
            public Task ConnectAsync(CancellationToken token) { return Task.CompletedTask; }
            public Task SetEnabledAsync(bool enabled, CancellationToken token) { return enabled ? EnableAck.Task : Task.CompletedTask; }
            public Task WaitForIdleAsync(CancellationToken token) { return Task.CompletedTask; }
            public Task AcceptEscrowAsync(CancellationToken token) { throw new Exception("Forbidden escrow call"); }
            public Task ReturnEscrowAsync(CancellationToken token) { throw new Exception("Forbidden escrow call"); }
            public void Dispose() { }
        }
        private static void LedgerScenarios()
        {
            InDirectory(LedgerHistoryAndCopies);
            InDirectory(LegacyMigration);
            InDirectory(ResolutionAuditAndIdempotency);
            InDirectory(ResolutionStaleAndIncomplete);
            InDirectory(ReceiptCrashRecovery);
            InDirectory(ReceiptDoesNotOverwriteNewer);
            InDirectory(ExplicitVerifiedReceipt);
            InDirectory(DisposeLatePayoutDoesNotWrite);
            InDirectory(ResolvedNeedsReceipt);
            StartupReadAndDisposeAreSerialized();
        }
        private static void StartupReadAndDisposeAreSerialized()
        {
            using (var store = new BlockingStartupStore())
            using (var disposeEntered = new ManualResetEventSlim(false))
            {
                var session = new ExchangeService(new Acceptor(), new Hopper(), store, 100);
                var start = Task.Run(() => session.StartAsync(None));
                Task closing = null;
                try
                {
                    Check(store.ReadEntered.Wait(TimeSpan.FromSeconds(4)), "Startup did not enter controlled journal read");
                    closing = Task.Run(() =>
                    {
                        disposeEntered.Set();
                        session.Dispose();
                        Interlocked.Exchange(ref store.DisposeReturned, 1);
                    });
                    Check(disposeEntered.Wait(TimeSpan.FromSeconds(4)), "Concurrent Dispose did not start");
                    Check(!closing.Wait(TimeSpan.FromMilliseconds(75)), "Dispose returned while startup ReadAll still owned a pending write");
                    Check(Volatile.Read(ref store.Writes) == 0, "Controlled ReadAll wrote before its release event");
                }
                finally
                {
                    // Release even after an assertion failure so neither worker can escape the fixture.
                    store.AllowRead.Set();
                    start.GetAwaiter().GetResult();
                    if (closing != null) closing.GetAwaiter().GetResult();
                    else session.Dispose();
                }
                Check(Volatile.Read(ref store.Writes) == 1 && Volatile.Read(ref store.WritesAfterDispose) == 0,
                    "Startup migration/write occurred after Dispose returned");
                Thread.Sleep(25);
                Check(Volatile.Read(ref store.Writes) == 1 && Volatile.Read(ref store.WritesAfterDispose) == 0,
                    "A late startup continuation wrote after shutdown completed");
            }
        }
        private sealed class BlockingStartupStore : ITransactionStore, IDisposable
        {
            public readonly ManualResetEventSlim ReadEntered = new ManualResetEventSlim(false);
            public readonly ManualResetEventSlim AllowRead = new ManualResetEventSlim(false);
            public int DisposeReturned, Writes, WritesAfterDispose;
            public IReadOnlyList<TransactionRecord> ReadAll()
            {
                ReadEntered.Set();
                if (!AllowRead.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Controlled startup journal read was not released");
                RecordWrite(); // Models migration/receipt recovery performed inside ReadAll.
                return new TransactionRecord[0];
            }
            public void Save(TransactionRecord transaction) { RecordWrite(); }
            private void RecordWrite()
            {
                Interlocked.Increment(ref Writes);
                if (Volatile.Read(ref DisposeReturned) != 0) Interlocked.Increment(ref WritesAfterDispose);
            }
            public void Dispose() { ReadEntered.Dispose(); AllowRead.Dispose(); }
        }
        private static void LedgerHistoryAndCopies(string root)
        {
            var store = new LedgerStore(root);
            for (int i = 0; i < 80; i++) { var historic = Funded("history-" + i, 1000); historic.Stage = TransactionStages.Succeeded; historic.PaidMinor = 1000; store.Save(historic); }
            var current = Funded("current", 1000); store.Save(current);
            var directory = Path.Combine(root, "transactions"); var before = Directory.GetFiles(directory).ToDictionary(x => x, File.ReadAllBytes);
            var times = before.Keys.ToDictionary(x => x, File.GetLastWriteTimeUtc);
            current.Message = "one active write"; store.Save(current);
            var changed = before.Keys.Where(x => !before[x].SequenceEqual(File.ReadAllBytes(x))).ToArray();
            Check(changed.Length == 1 && before.Keys.Where(x => x != changed[0]).All(x => times[x] == File.GetLastWriteTimeUtc(x)), "Save rewrote unrelated ledger history");
            var unrelated = before.Keys.First(x => x != changed[0]); var unrelatedBytes = File.ReadAllBytes(unrelated);
            File.WriteAllText(unrelated, "damaged history"); current.Message = "active save does not scan history"; store.Save(current);
            bool readFailed = false; try { store.ReadAll(); } catch { readFailed = true; } Check(readFailed, "ReadAll ignored corrupt historic record"); File.WriteAllBytes(unrelated, unrelatedBytes);
            var read = store.ReadAll().Single(x => x.Id == current.Id); read.Credits[0].AmountMinor = 0;
            Check(store.ReadAll().Single(x => x.Id == current.Id).Credits[0].AmountMinor == 1000, "Ledger ReadAll leaked mutable nested values");
            Check(!Directory.GetFiles(directory, "*.tmp").Any(), "Atomic save left an active temporary file");
        }
        private static void LegacyMigration(string root)
        {
            var legacy = Path.Combine(root, "journal.json"); var source = new JsonTransactionStore(legacy); source.Save(Funded("legacy-a", 1000)); source.Save(Funded("legacy-b", 2000));
            var original = File.ReadAllBytes(legacy); var store = new LedgerStore(Path.Combine(root, "ledger"), legacy); Check(store.ReadAll().Count == 2, "Legacy records not migrated");
            var files = Directory.GetFiles(Path.Combine(root, "ledger", "transactions")).ToDictionary(x => x, File.ReadAllBytes); store.ReadAll();
            Check(files.All(x => x.Value.SequenceEqual(File.ReadAllBytes(x.Key))) && original.SequenceEqual(File.ReadAllBytes(legacy)), "Idempotent migration rewrote original history");
            var row = store.ReadAll().Single(x => x.Id == "legacy-a"); row.Stage = TransactionStages.Debt; row.PaidMinor = 700; store.Save(row);
            Check(new LedgerStore(Path.Combine(root, "ledger"), legacy).ReadAll().Single(x => x.Id == row.Id).PaidMinor == 700 && original.SequenceEqual(File.ReadAllBytes(legacy)), "Legacy merge reverted current ledger or changed original");
            source.Save(Funded("legacy-c", 1000)); Check(store.ReadAll().Count == 3, "Later additional legacy record not merged");
            var altered = Funded("legacy-a", 2000); source.Save(altered); bool conflict = false; try { store.ReadAll(); } catch (InvalidDataException) { conflict = true; }
            Check(conflict, "Changed legacy original silently overwrote migrated ledger");
        }
        private static void ResolutionAuditAndIdempotency(string root)
        {
            var store = new LedgerStore(root); var claim = Funded("claim", 1000); claim.Stage = TransactionStages.Debt; claim.PaidMinor = 700; store.Save(claim);
            var service = new ResolutionService(store); var fingerprint = service.GetFingerprint(claim.Id);
            var receipt = service.Resolve(claim.Id, fingerprint, "DOMAIN\\operator", "Verified customer settlement", 700, 300);
            var resolved = store.ReadAll().Single();
            Check(resolved.Stage == TransactionStages.Resolved && resolved.PaidMinor == 700 && resolved.ReceivedMinor == 1000 && resolved.ResolutionId == receipt.Id, "Resolution destroyed original cash evidence");
            Check(receipt.Original.Stage == TransactionStages.Debt && receipt.Original.PaidMinor == 700 && receipt.VerifiedReceivedMinor == 1000 && receipt.ManualPaidMinor == 300 && receipt.OriginalFingerprint == fingerprint, "Receipt missing immutable original/verification evidence");
            receipt.Original.PaidMinor = 0; Check(service.ReadResolutions().Single().Original.PaidMinor == 700, "Resolution Original leaked a mutable reference");
            var repeated = service.Resolve(claim.Id, fingerprint, "DOMAIN\\operator", "Verified customer settlement", 700, 300); Check(repeated.Id == receipt.Id && service.ReadResolutions().Count == 1, "Idempotent resolve duplicated receipt");
            var bill = new Acceptor(); var hopper = new Hopper(); using (var session = new ExchangeService(bill, hopper, new LedgerStore(root), 100))
            { session.StartAsync(None).GetAwaiter().GetResult(); Check(session.Snapshot.UnresolvedClaims.Count == 0 && session.Snapshot.State == ExchangeState.Ready && hopper.Payouts == 0, "Resolved restart blocked or issued hopper command"); }
        }
        private static void ResolutionStaleAndIncomplete(string root)
        {
            var store = new LedgerStore(root); var claim = Funded("uncertain", 1000); claim.Stage = TransactionStages.ReconciliationRequired; claim.IsDebtExact = false; claim.PaidMinor = 300; store.Save(claim);
            var service = new ResolutionService(store); var stale = service.GetFingerprint(claim.Id); claim.Message = "new physical verification pending"; store.Save(claim);
            ExpectFailure(() => service.Resolve(claim.Id, stale, "operator", "reason", 300, 700), "Stale fingerprint was accepted");
            var fingerprint = service.GetFingerprint(claim.Id);
            ExpectFailure(() => service.Resolve(claim.Id, fingerprint, "operator", "reason", 300, 600), "Partial manual settlement closed full claim");
            ExpectFailure(() => service.Resolve(claim.Id, fingerprint, "operator", "reason", 200, 800), "Resolution reduced confirmed lower bound");
            ExpectFailure(() => service.Resolve(claim.Id, fingerprint, "", "reason", 700, 300), "Anonymous resolution accepted");
            var receipt = service.Resolve(claim.Id, fingerprint, "operator", "Physical counters verified", 700, 300);
            Check(receipt.Original.PaidMinor == 300 && receipt.VerifiedPaidMinor == 700 && store.ReadAll().Single().PaidMinor == 300, "Uncertain verified amount overwrote original lower bound");
            var funded = Funded("manual-funded", 2000); store.Save(funded); var fundedFingerprint = service.GetFingerprint(funded.Id);
            service.Resolve(funded.Id, fundedFingerprint, "operator", "Customer reimbursed offline", 0, 2000); Check(store.ReadAll().Single(x => x.Id == funded.Id).Stage == TransactionStages.Resolved, "Funded wallet could not be settled offline");
        }
        private static void ReceiptCrashRecovery(string root)
        {
            var store = new LedgerStore(root); var row = Funded("crash", 1000); row.Stage = TransactionStages.Debt; row.PaidMinor = 700; store.Save(row);
            var path = Directory.GetFiles(Path.Combine(root, "transactions")).Single(); var prefix = File.ReadAllBytes(path); var service = new ResolutionService(store); var fingerprint = service.GetFingerprint(row.Id);
            var receipt = service.Resolve(row.Id, fingerprint, "operator", "Receipt-first test", 700, 300);
            File.WriteAllBytes(path, prefix); // Durable prefix at crash: receipt exists, closing write did not happen.
            var recovered = new LedgerStore(root); Check(recovered.ReadAll().Single().Stage == TransactionStages.Resolved && recovered.ReadAll().Single().PaidMinor == 700 && recovered.ReadResolutions().Single().Id == receipt.Id, "Receipt-first recovery failed");
        }
        private static void ReceiptDoesNotOverwriteNewer(string root)
        {
            var store = new LedgerStore(root); var row = Funded("newer", 1000); row.Stage = TransactionStages.Debt; row.PaidMinor = 700; store.Save(row);
            var path = Directory.GetFiles(Path.Combine(root, "transactions")).Single(); var prefix = File.ReadAllText(path); var service = new ResolutionService(store); var fingerprint = service.GetFingerprint(row.Id);
            service.Resolve(row.Id, fingerprint, "operator", "Conflict test", 700, 300);
            var newer = prefix.Replace("\"ReceivedMinor\":1000", "\"ReceivedMinor\":2000"); Check(newer != prefix, "Conflict fixture did not modify original"); File.WriteAllText(path, newer);
            var before = File.ReadAllBytes(path); ExpectFailure(() => new LedgerStore(root).ReadAll(), "Receipt overwrote newer transaction"); Check(before.SequenceEqual(File.ReadAllBytes(path)), "Recovery changed conflicting record");
        }
        private static void ExplicitVerifiedReceipt(string root)
        {
            var store = new LedgerStore(root); var row = new TransactionRecord { Id = "no-credit", SchemaVersion = 2, Stage = TransactionStages.ReconciliationRequired, CoinValueMinor = 100, IsDebtExact = false, HasPendingNote = true, Credits = new List<CreditRecord>(), Batches = new List<PayoutBatchRecord>() }; store.Save(row);
            var service = new ResolutionService(store); var fingerprint = service.GetFingerprint(row.Id);
            ExpectFailure(() => service.Resolve(row.Id, fingerprint, "operator", "Inspected empty path", 0, 0), "Implicit zero cleared ambiguous pending cash");
            var receipt = service.Resolve(row.Id, fingerprint, "operator", "Inspected empty path", 0, 0, 0);
            Check(receipt.VerifiedReceivedMinor == 0 && store.ReadAll().Single().Stage == TransactionStages.Resolved, "Explicit verified zero could not close inspected no-credit claim");
            row.Id = "missing-credit"; store.Save(row); fingerprint = service.GetFingerprint(row.Id);
            ExpectFailure(() => service.Resolve(row.Id, fingerprint, "operator", "reason", 500, 0, 500), "Verified amount outside 10/20 note sums accepted");
            var actual = service.Resolve(row.Id, fingerprint, "operator", "Physically found 10 KM; customer reimbursed", 1000, 0, 1000);
            Check(actual.Original.ReceivedMinor == 0 && actual.VerifiedReceivedMinor == 1000 && store.ReadAll().Single(x => x.Id == row.Id).ReceivedMinor == 0, "Verified intake replaced original evidence");
        }
        private static async Task ReadinessAckRace()
        {
            var bill = new ReadinessRaceAcceptor(); var hopper = new Hopper { Status = new HopperStatus(true, false, 30) };
            using (var service = new ExchangeService(bill, hopper, new MemoryStore(), 100))
            {
                await service.StartAsync(None); bill.BlockNextEnable = true;
                var refresh = service.RefreshReadinessAsync(None); Check(!refresh.IsCompleted, "Race fixture did not pause enable ACK");
                bill.Emit(BillEventKind.Credit, 2000, "ack-race"); bill.Emit(BillEventKind.Stacked, 2000, "ack-race");
                bill.EnableAck.SetResult(true); await refresh;
                Check(!bill.Enabled && !service.Snapshot.CanAcceptBanknotes && service.Snapshot.CanExchange && service.Snapshot.ReceivedMinor == 2000, "Enable ACK race left known-shortage input enabled");
            }
        }
        private sealed class ReadinessRaceAcceptor : IBillAcceptor
        {
            public event EventHandler<BillEventArgs> BillEvent;
            public bool Enabled, BlockNextEnable; public readonly TaskCompletionSource<bool> EnableAck = new TaskCompletionSource<bool>();
            public void Emit(BillEventKind kind, int amount, string id) { BillEvent?.Invoke(this, new BillEventArgs(kind, amount, "BAM", eventId: id)); }
            public Task ConnectAsync(CancellationToken token) { return Task.CompletedTask; }
            public async Task SetEnabledAsync(bool enabled, CancellationToken token)
            { if (enabled && BlockNextEnable) { BlockNextEnable = false; await EnableAck.Task; } Enabled = enabled; }
            public Task WaitForIdleAsync(CancellationToken token) { return Task.CompletedTask; }
            public Task AcceptEscrowAsync(CancellationToken token) { throw new Exception("Forbidden escrow call"); }
            public Task ReturnEscrowAsync(CancellationToken token) { throw new Exception("Forbidden escrow call"); }
            public void Dispose() { }
        }
        private static void DisposeLatePayoutDoesNotWrite(string root)
        {
            var store = new LedgerStore(root); var bill = new Acceptor(); var hopper = new DelayedPayoutHopper();
            var service = new ExchangeService(bill, hopper, store, 100); service.StartAsync(None).GetAwaiter().GetResult(); bill.Insert(1000);
            var exchange = service.ExchangeAsync(None); Check(!exchange.IsCompleted && hopper.Calls == 1, "Forced-shutdown fixture did not pause active payout");
            service.Dispose(); var path = Directory.GetFiles(Path.Combine(root, "transactions"), "*.json").Single(); var durableAtReturn = File.ReadAllBytes(path);
            ExpectFailure(() => hopper.Progress.Report(7), "Late progress after Dispose was not rejected");
            hopper.Completion.SetResult(new PayoutResult(10, 7, PayoutOutcome.Completed)); exchange.GetAwaiter().GetResult();
            Check(durableAtReturn.SequenceEqual(File.ReadAllBytes(path)) && store.ReadAll().Single().Stage == TransactionStages.ReconciliationRequired && store.ReadAll().Single().PaidMinor == 0, "Late payout continuation modified journal after Dispose returned");
        }
        private sealed class DelayedPayoutHopper : ICoinHopper
        {
            public int Calls; public IProgress<int> Progress; public readonly TaskCompletionSource<PayoutResult> Completion = new TaskCompletionSource<PayoutResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task ConnectAsync(CancellationToken token) { return Task.CompletedTask; }
            public Task<HopperStatus> GetStatusAsync(CancellationToken token) { return Task.FromResult(new HopperStatus(true, false)); }
            public Task<PayoutResult> PayoutAsync(int coins, IProgress<int> progress, CancellationToken token) { Calls++; Progress = progress; return Completion.Task; }
            public Task StopAsync(CancellationToken token) { return Task.CompletedTask; }
            public void Dispose() { }
        }
        private static void ResolvedNeedsReceipt(string root)
        {
            var store = new LedgerStore(root); var row = Funded("forged", 1000); store.Save(row);
            row.Stage = TransactionStages.Resolved; row.ResolutionId = "missing"; ExpectFailure(() => store.Save(row), "Direct Save bypassed audited resolution");
            var path = Directory.GetFiles(Path.Combine(root, "transactions")).Single(); var bytes = File.ReadAllText(path);
            var forged = bytes.Replace("\"Stage\":\"Funded\"", "\"Stage\":\"Resolved\"").Replace("\"ResolutionId\":null", "\"ResolutionId\":\"missing\"");
            Check(forged != bytes, "Forged receipt fixture did not modify ledger"); File.WriteAllText(path, forged);
            ExpectFailure(() => store.ReadAll(), "Resolved record without receipt silently hid unresolved claim");
        }
        private static void ExpectFailure(Action action, string message)
        { bool failed = false; try { action(); } catch { failed = true; } Check(failed, message); }
        private static void InDirectory(Action<string> test)
        {
            var path = Path.Combine(Path.GetTempPath(), "ChangeClip.Ledger.Tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); Exception failure = null;
            try { test(path); } catch (Exception error) { failure = error; }
            try
            {
                var absolute = Path.GetFullPath(path);
                if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(absolute).StartsWith("ChangeClip.Ledger.Tests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path");
                for (int attempt = 0; ; attempt++)
                {
                    try { Directory.Delete(absolute, true); break; }
                    catch (IOException) when (attempt < 5) { Thread.Sleep(50); }
                }
            }
            catch (Exception cleanup) { if (failure != null) throw new AggregateException(failure, cleanup); throw; }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}

