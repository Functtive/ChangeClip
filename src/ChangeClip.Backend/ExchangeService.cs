using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChangeClip.Contracts;

namespace ChangeClip.Backend
{
    public sealed class ExchangeService : IExchangeSession
    {
        private readonly IBillAcceptor acceptor;
        private readonly ICoinHopper hopper;
        private readonly ITransactionStore store;
        private readonly int coinValue;
        private readonly object sync = new object();
        private readonly SemaphoreSlim operation = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly HashSet<string> creditIds = new HashSet<string>(StringComparer.Ordinal);
        private ExchangeSnapshot snapshot = new ExchangeSnapshot(ExchangeState.Disconnected, "Uređaji nisu povezani.");
        private TransactionRecord active;
        private readonly Dictionary<string, TransactionRecord> unresolvedRecords = new Dictionary<string, TransactionRecord>(StringComparer.Ordinal);
        private bool inputEnabled;
        private bool readinessRequested;
        private long fundingRevision;
        public bool EnforceKnownStock { get; private set; }
        private bool started, disposed, disposing, collecting, draining, frozen, halted;
        public ExchangeService(IBillAcceptor billAcceptor, ICoinHopper coinHopper, ITransactionStore transactionStore, int coinValueMinor, bool enforceKnownStock = true)
        {
            acceptor = billAcceptor ?? throw new ArgumentNullException(nameof(billAcceptor));
            hopper = coinHopper ?? throw new ArgumentNullException(nameof(coinHopper));
            store = transactionStore ?? throw new ArgumentNullException(nameof(transactionStore));
            DeviceOptionsValidator.ValidateCoinValue(coinValueMinor);
            EnforceKnownStock = enforceKnownStock;
            coinValue = coinValueMinor; acceptor.BillEvent += OnBill;
        }
        public event EventHandler<ExchangeSnapshot> Changed;
        public ExchangeSnapshot Snapshot { get { lock (sync) return snapshot; } }
        private static string Money(int value) { return (value / 100m).ToString("0.00", CultureInfo.GetCultureInfo("bs-Latn-BA")); }
        private static bool Closed(TransactionRecord row) { return TransactionStages.IsClosed(row.Stage); }
        private static bool Connected(HopperStatus status) { return status != null && status.Connected && string.IsNullOrEmpty(status.Fault); }
        private TransactionRecord NewWallet()
        {
            return new TransactionRecord { Id = Guid.NewGuid().ToString("N"), SchemaVersion = 2, CoinValueMinor = coinValue,
                IsDebtExact = true, Credits = new List<CreditRecord>(), Batches = new List<PayoutBatchRecord>() };
        }
        private void EnsureWallet()
        {
            if (active == null) active = NewWallet();
            if (active.Credits == null) active.Credits = new List<CreditRecord>();
            if (active.Batches == null) active.Batches = new List<PayoutBatchRecord>();
        }
        private void Publish(ExchangeState state, string message, bool canExchange = false)
        {
            if (disposed) return;
            snapshot = new ExchangeSnapshot(state, message, active?.ReceivedMinor ?? 0, active?.PaidMinor ?? 0,
                active == null ? 0 : Math.Max(0, active.ReceivedMinor - active.PaidMinor), canExchange, false,
                active == null ? unresolvedRecords.Count == 0 : active.IsDebtExact, active?.Id, inputEnabled, Claims());
            var handlers = Changed;
            if (handlers != null) foreach (EventHandler<ExchangeSnapshot> handler in handlers.GetInvocationList())
                try { handler(this, snapshot); } catch { /* Observer failures cannot interrupt accounting. */ }
        }
        private IReadOnlyList<UnresolvedClaim> Claims()
        {
            var records = unresolvedRecords.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            if (active != null) { if (Closed(active)) records.Remove(active.Id); else records[active.Id] = active; }
            return records.Values.OrderBy(x => x.Id, StringComparer.Ordinal).Select(x => new UnresolvedClaim(x.Id, x.Stage, x.ReceivedMinor, x.PaidMinor, x.IsDebtExact)).ToArray();
        }
        private void Persist(string stage, string message = null)
        { if (disposed) throw new ObjectDisposedException(nameof(ExchangeService)); active.Stage = stage; active.Message = message; store.Save(active); if (Closed(active)) unresolvedRecords.Remove(active.Id); else unresolvedRecords[active.Id] = active.Copy(); }
        private void Reconcile(string message)
        {
            if (disposed) return;
            EnsureWallet(); active.IsDebtExact = false; halted = true; collecting = false;
            try { Persist(TransactionStages.ReconciliationRequired, message); }
            catch (Exception error) { message += " Greška evidencije: " + error.Message; }
            Publish(ExchangeState.ReconciliationRequired, message);
        }
        private bool FullyFunded(TransactionRecord row)
        {
            return row.SchemaVersion == 2 && row.Stage == TransactionStages.Funded && row.CoinValueMinor == coinValue && row.IsDebtExact && !row.HasPendingNote && row.PaidMinor == 0 && row.ReceivedMinor > 0 && row.Credits != null && row.Credits.Count > 0
                && row.Credits.All(x => x.Stacked && x.Currency == "BAM" && (x.AmountMinor == 1000 || x.AmountMinor == 2000) && !string.IsNullOrEmpty(x.EventId))
                && row.Credits.Select(x => x.EventId).Distinct().Count() == row.Credits.Count && row.Credits.Sum(x => (long)x.AmountMinor) == row.ReceivedMinor;
        }
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            lock (sync) { if (disposed || disposing) throw new ObjectDisposedException(nameof(ExchangeService)); }
            if (!operation.Wait(0)) return;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token))
            try
            {
                lock (sync) { if (started) return; Publish(ExchangeState.Initializing, "Povezivanje uređaja..."); }
                var records = store.ReadAll();
                lock (sync)
                {
                    foreach (var row in records)
                    {
                        if (!string.IsNullOrEmpty(row.CreditEventId)) creditIds.Add(row.CreditEventId);
                        if (row.Credits != null) foreach (var credit in row.Credits) if (!string.IsNullOrEmpty(credit.EventId)) creditIds.Add(credit.EventId);
                    }
                    var unresolved = records.Where(x => !Closed(x)).ToArray();
                    foreach (var row in unresolved) unresolvedRecords[row.Id] = row.Copy();
                    active = unresolved.Length == 1 ? unresolved[0].Copy() : null;
                    if (unresolved.Length > 1)
                    { halted = true; collecting = false; Publish(ExchangeState.ReconciliationRequired, "Više nezatvorenih bilansa. Servis mora provjeriti sva potraživanja."); }
                    else if (active != null && !FullyFunded(active) && !(active.SchemaVersion == 2 && active.Stage == TransactionStages.Debt && active.IsDebtExact))
                        Reconcile("Nezatvoren ili stari nalog nakon pokretanja; nema automatske isplate.");
                }
                await hopper.ConnectAsync(linked.Token).ConfigureAwait(false);
                await acceptor.ConnectAsync(linked.Token).ConfigureAwait(false);
                await SetInputAsync(false, linked.Token).ConfigureAwait(false);
                lock (sync)
                {
                    started = true;
                    if (halted) return;
                    if (active != null && active.Stage == TransactionStages.Debt) { halted = true; Publish(ExchangeState.Debt, DebtMessage()); return; }
                }
                await EnableCollectionAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception exception) { await FailAsync(exception).ConfigureAwait(false); }
            finally { ReleaseOperation(); }
        }
        private async Task SetInputAsync(bool enabled, CancellationToken token)
        {
            lock (sync) { if (disposed || (disposing && enabled)) throw new ObjectDisposedException(nameof(ExchangeService)); }
            await acceptor.SetEnabledAsync(enabled, token).ConfigureAwait(false);
            lock (sync)
            {
                if (disposed) return;
                inputEnabled = enabled;
                // This is the confirmed enable ACK; planned state never masquerades as hardware readiness.
                Publish(snapshot.State, snapshot.Message, snapshot.CanExchange && !halted && !draining && !frozen);
            }
        }
        private async Task EnableCollectionAsync(CancellationToken token, string readyMessage = null)
        {
            while (true)
            {
                var status = await hopper.GetStatusAsync(token).ConfigureAwait(false);
                if (!Connected(status)) throw new InvalidOperationException("Hopper nije povezan/spreman: " + (status?.Fault ?? "nepoznat status"));
                bool desired; long revision;
                lock (sync)
                {
                    if (halted || disposed || disposing || frozen || draining) return;
                    collecting = true; revision = fundingRevision;
                    var received = active?.ReceivedMinor ?? 0;
                    desired = !EnforceKnownStock || (!status.Empty && (!status.AvailableCoins.HasValue || (status.AvailableCoins.Value >= 0 && (long)status.AvailableCoins.Value * coinValue >= (long)received + 2000)));
                }
                await SetInputAsync(desired, token).ConfigureAwait(false);
                lock (sync)
                {
                    if (halted || disposed || disposing || frozen || draining) return;
                    if (revision != fundingRevision) continue; // A cash event raced the ACK: recompute against the current wallet.
                    var received = active?.ReceivedMinor ?? 0;
                    var pending = active != null && active.HasPendingNote;
                    var message = !desired ? "Prihvat je zaustavljen: poznata zaliha ne pokriva narednu novčanicu od 20 KM. Dopunite hopper i osvježite spremnost."
                        : !status.AvailableCoins.HasValue ? "Zaliha kovanica nije poznata; eventualna nepotpuna isplata trajno se evidentira kao dug. Ubacite 10 ili 20 KM."
                        : "Ubacite 10 ili 20 KM.";
                    if (received > 0) message = "Primljeno " + Money(received) + " KM. " + message + " Možete izabrati Razmijeni.";
                    if (readyMessage != null) message = readyMessage + " " + message;
                    Publish(pending ? ExchangeState.Accepting : received > 0 ? ExchangeState.AwaitingPayout : ExchangeState.Ready, message, received > 0);
                    return;
                }
            }
        }
        public async Task RefreshReadinessAsync(CancellationToken cancellationToken)
        {
            lock (sync)
            {
                if (disposed || disposing || !started || halted || frozen || draining) return;
                readinessRequested = true;
                if (active != null && active.HasPendingNote) return;
            }
            if (!operation.Wait(0)) return;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token))
            try
            {
                while (true)
                {
                    lock (sync)
                    {
                        if (halted || frozen || draining || disposed || disposing || (active != null && active.HasPendingNote)) return;
                        readinessRequested = false;
                    }
                    await EnableCollectionAsync(linked.Token).ConfigureAwait(false);
                    lock (sync) { if (!readinessRequested || halted || disposed || disposing || frozen || draining || (active != null && active.HasPendingNote)) return; }
                }
            }
            catch (Exception error) { await FailAsync(error).ConfigureAwait(false); }
            finally { ReleaseOperation(); }
        }
        private void ReleaseOperation()
        {
            operation.Release(); bool refresh;
            lock (sync) refresh = readinessRequested && started && !halted && !disposed && !disposing && !frozen && !draining && (active == null || !active.HasPendingNote);
            if (refresh) QueueReadiness();
        }
        private void QueueReadiness()
        {
            // Invoked after leaving the cash-event lock. A busy owner processes the pending request when it releases its gate.
            lock (sync) { if (disposed || disposing || halted || frozen || draining) return; readinessRequested = true; }
            ObserveReadiness(RefreshReadinessAsync(lifetime.Token));
        }
        private static async void ObserveReadiness(Task task)
        { try { await task.ConfigureAwait(false); } catch { /* RefreshReadinessAsync publishes and disables on failure. */ } }
        private void OnBill(object sender, BillEventArgs args)
        {
            bool refreshReadiness = false;
            lock (sync)
            {
                if (disposed) return;
                fundingRevision++;
                try
                {
                    if (args.Kind == BillEventKind.Credit)
                    {
                        if (!string.IsNullOrEmpty(args.EventId) && creditIds.Contains(args.EventId)) return;
                        EnsureWallet();
                        var valid = !string.IsNullOrEmpty(args.EventId) && args.Currency == "BAM" && (args.AmountMinor == 1000 || args.AmountMinor == 2000);
                        active.Credits.Add(new CreditRecord { EventId = args.EventId, AmountMinor = args.AmountMinor, Currency = args.Currency });
                        if (args.Currency == "BAM" && args.AmountMinor > 0) active.ReceivedMinor = checked(active.ReceivedMinor + args.AmountMinor);
                        active.AmountMinor = active.ReceivedMinor; active.HasPendingNote = true; active.PendingEventId = args.EventId;
                        if (!string.IsNullOrEmpty(args.EventId)) creditIds.Add(args.EventId);
                        if (!valid || !collecting || frozen || halted)
                        {
                            Reconcile("Uplata je evidentirana izvan sigurnog toka. Potrebna je ručna provjera.");
                            lifetime.Cancel(); DisableInBackground(); return;
                        }
                        Persist(TransactionStages.FundingPending);
                        Publish(draining ? ExchangeState.Accepting : ExchangeState.AwaitingPayout,
                            "Primljeno " + Money(active.ReceivedMinor) + " KM.", !draining && active.ReceivedMinor > 0);
                    }
                    else if (args.Kind == BillEventKind.Reading || args.Kind == BillEventKind.Escrow)
                    {
                        // Repeated escrow notifications for a completed GUID do not open a new note.
                        if (!string.IsNullOrEmpty(args.EventId) && creditIds.Contains(args.EventId)) return;
                        EnsureWallet(); active.HasPendingNote = true; active.PendingEventId = args.EventId;
                        if (!collecting || frozen || halted)
                        { Reconcile("Novčanica je ušla tokom blokiranog prihvata."); lifetime.Cancel(); DisableInBackground(); return; }
                        Persist(TransactionStages.FundingPending);
                        Publish(ExchangeState.Accepting, "Prihvat novčanice...", !draining && active.ReceivedMinor > 0);
                    }
                    else if (args.Kind == BillEventKind.Stacked)
                    {
                        var credit = active?.Credits?.FirstOrDefault(x => !string.IsNullOrEmpty(args.EventId) && x.EventId == args.EventId);
                        if (credit == null)
                        {
                            if (!string.IsNullOrEmpty(args.EventId) && creditIds.Contains(args.EventId)) return;
                            Reconcile("STACKED bez poznate CREDIT potvrde. Potrebna je ručna provjera."); lifetime.Cancel(); DisableInBackground(); return;
                        }
                        if (credit.Stacked) return;
                        credit.Stacked = true;
                        if (active.PendingEventId == args.EventId) { active.HasPendingNote = false; active.PendingEventId = null; }
                        if (halted || frozen) { Persist(TransactionStages.ReconciliationRequired, active.Message); return; }
                        Persist(active.HasPendingNote ? TransactionStages.FundingPending : TransactionStages.Funded);
                        Publish(draining ? ExchangeState.Accepting : ExchangeState.AwaitingPayout,
                            "Primljeno " + Money(active.ReceivedMinor) + " KM. Izaberite Razmijeni ili ubacite još.", !draining && active.ReceivedMinor > 0);
                        refreshReadiness = !draining;
                    }
                    else if (args.Kind == BillEventKind.Rejected)
                    {
                        if (halted || frozen || active == null) return;
                        if (active.Credits.Any(x => !x.Stacked && x.EventId == active.PendingEventId))
                        { Reconcile("REJECTED nakon CREDIT potvrde. Potrebna je ručna provjera."); lifetime.Cancel(); DisableInBackground(); return; }
                        active.HasPendingNote = false; active.PendingEventId = null;
                        if (active.ReceivedMinor == 0) { Persist(TransactionStages.Returned); active = null; Publish(ExchangeState.Ready, "Novčanica nije prihvaćena."); }
                        else { Persist(TransactionStages.Funded); Publish(draining ? ExchangeState.Accepting : ExchangeState.AwaitingPayout, "Primljeno " + Money(active.ReceivedMinor) + " KM.", !draining); }
                    }
                    else if (args.Kind == BillEventKind.Fault || args.Kind == BillEventKind.Disconnected)
                    {
                        if (active != null && (active.HasPendingNote || frozen || draining || active.Stage == TransactionStages.PayoutIntent)) Reconcile(args.Message ?? "Prekid tokom aktivnog naloga.");
                        else if (active?.Stage != TransactionStages.Debt) { halted = true; collecting = false; Publish(ExchangeState.Fault, args.Message ?? "Prekid acceptora."); }
                        lifetime.Cancel(); DisableInBackground();
                    }
                }
                catch (Exception error)
                { Reconcile("Greška trajne evidencije uplate: " + error.Message); lifetime.Cancel(); DisableInBackground(); }
            }
            if (refreshReadiness) QueueReadiness();
        }
        private async void DisableInBackground()
        { try { await SetInputAsync(false, CancellationToken.None).ConfigureAwait(false); } catch { } }
        // Kept only for IExchangeSession compatibility. Automatically stacked cash cannot be returned.
        public Task ReturnAsync(CancellationToken cancellationToken)
        { throw new NotSupportedException("Automatski spremljene novčanice se ne vraćaju iz aplikacije."); }
        public async Task ExchangeAsync(CancellationToken cancellationToken)
        {
            lock (sync) { if (disposed || disposing) throw new ObjectDisposedException(nameof(ExchangeService)); }
            if (!operation.Wait(0)) return;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token))
            try
            {
                lock (sync)
                {
                    if (!snapshot.CanExchange || active == null || active.ReceivedMinor <= 0 || halted || frozen) return;
                    draining = true; Publish(ExchangeState.Accepting, "Zaustavljanje prihvata i završetak novčanice...");
                }
                await SetInputAsync(false, linked.Token).ConfigureAwait(false);
                await acceptor.WaitForIdleAsync(linked.Token).ConfigureAwait(false);
                int totalCoins;
                lock (sync)
                {
                    if (halted || disposed) throw new InvalidOperationException("Bilans zahtijeva provjeru.");
                    // Idle is only a fence; final note evidence must already come from real events.
                    if (active.HasPendingNote || active.Credits.Any(x => !x.Stacked))
                        throw new InvalidOperationException("Idle bez konačne STACKED potvrde za sve uplate.");
                    Persist(TransactionStages.Funded);
                    collecting = false; draining = false; frozen = true;
                    totalCoins = active.ReceivedMinor / coinValue;
                    if (active.ReceivedMinor % coinValue != 0) throw new InvalidOperationException("Bilans ne odgovara apoenu kovanice.");
                    Publish(ExchangeState.PayingOut, "Isplata " + Money(active.ReceivedMinor) + " KM...");
                }
                string successMessage = null;
                while (true)
                {
                    var status = await hopper.GetStatusAsync(linked.Token).ConfigureAwait(false);
                    lock (sync)
                    {
                        if (halted || disposed) throw new InvalidOperationException("Isplata je prekinuta.");
                        if (!Connected(status) || status.Empty)
                        { Persist(TransactionStages.Debt, DebtMessage()); halted = true; frozen = false; Publish(ExchangeState.Debt, active.Message); return; }
                    }
                    PayoutBatchRecord batch;
                    lock (sync)
                    {
                        var baseCoins = active.PaidMinor / coinValue;
                        batch = new PayoutBatchRecord { Number = active.Batches.Count + 1, BaseConfirmedCoins = baseCoins,
                            RequestedCoins = Math.Min(255, totalCoins - baseCoins), Stage = "Intent" };
                        active.Batches.Add(batch); Persist(TransactionStages.PayoutIntent);
                    }
                    var result = await hopper.PayoutAsync(batch.RequestedCoins, new InlineProgress(count => RecordProgress(batch, count)), linked.Token).ConfigureAwait(false);
                    bool complete;
                    lock (sync)
                    {
                        if (halted || disposed || active.Stage != TransactionStages.PayoutIntent) throw new InvalidOperationException("Isplata je prekinuta; potrebna je provjera.");
                        if (result == null || result.RequestedCoins != batch.RequestedCoins || result.ConfirmedCoins < batch.ConfirmedCoins || result.ConfirmedCoins < 0 || result.ConfirmedCoins > batch.RequestedCoins || !result.CountReliable || result.Outcome == PayoutOutcome.Uncertain)
                        {
                            batch.Stage = "Uncertain"; Reconcile("Broj isplaćenih kovanica nije pouzdano potvrđen. Nema automatskog ponavljanja."); return;
                        }
                        batch.ConfirmedCoins = result.ConfirmedCoins;
                        active.PaidMinor = checked((batch.BaseConfirmedCoins + batch.ConfirmedCoins) * coinValue);
                        batch.Stage = batch.ConfirmedCoins == batch.RequestedCoins ? "Completed" : "Partial";
                        complete = active.PaidMinor == active.ReceivedMinor;
                        if (complete)
                        {
                            Persist(TransactionStages.Succeeded, "Uspješno. Isplaćeno " + Money(active.PaidMinor) + " KM.");
                            successMessage = "Uspješno. Isplaćeno " + Money(active.PaidMinor) + " KM.";
                            active = null; frozen = false; Publish(ExchangeState.Ready, successMessage);
                        }
                        else if (batch.Stage == "Partial" || result.Outcome != PayoutOutcome.Completed)
                        { Persist(TransactionStages.Debt, DebtMessage()); halted = true; frozen = false; Publish(ExchangeState.Debt, active.Message); return; }
                        else Persist(TransactionStages.PayoutIntent); // A completed batch is durable; restart still never replays the remaining batch.
                    }
                    if (complete) { await EnableCollectionAsync(linked.Token, successMessage).ConfigureAwait(false); return; }
                }
            }
            catch (Exception exception) { await FailAsync(exception).ConfigureAwait(false); }
            finally { ReleaseOperation(); }
        }
        private string DebtMessage() { return "Dužni smo vam još " + Money(Math.Max(0, active.ReceivedMinor - active.PaidMinor)) + " KM."; }
        private void RecordProgress(PayoutBatchRecord batch, int count)
        {
            lock (sync)
            {
                if (active == null || halted || disposed || active.Stage != TransactionStages.PayoutIntent || active.Batches.Last() != batch) throw new InvalidOperationException("Neočekivani napredak isplate.");
                if (count < 0 || count > batch.RequestedCoins) throw new InvalidOperationException("Neispravan brojač hoppera.");
                if (count <= batch.ConfirmedCoins) return;
                batch.ConfirmedCoins = count; active.PaidMinor = checked((batch.BaseConfirmedCoins + count) * coinValue);
                Persist(TransactionStages.PayoutIntent); Publish(ExchangeState.PayingOut, "Isplaćeno " + Money(active.PaidMinor) + " KM.");
            }
        }
        private async Task FailAsync(Exception exception)
        {
            lock (sync)
            {
                if (disposed) return;
                halted = true; collecting = false;
                if (active != null && active.Stage != TransactionStages.Debt && (active.HasPendingNote || frozen || draining || active.Stage == TransactionStages.PayoutIntent || active.Stage == TransactionStages.FundingPending))
                    Reconcile("Transakcija je prekinuta: " + exception.Message);
                else if (active?.Stage == TransactionStages.Debt) Publish(ExchangeState.Debt, DebtMessage());
                else if (snapshot.State != ExchangeState.ReconciliationRequired) Publish(ExchangeState.Fault, "Rad je zaustavljen: " + exception.Message);
            }
            try { await SetInputAsync(false, CancellationToken.None).ConfigureAwait(false); } catch { }
            try { await hopper.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }
        public void Dispose()
        {
            bool drainInput;
            lock (sync)
            {
                if (disposed || disposing) return;
                disposing = true;
                drainInput = collecting && !halted && !frozen;
                if (drainInput) draining = true;
                lifetime.Cancel();
                if (active != null && active.Stage != TransactionStages.Debt && (frozen || active.Stage == TransactionStages.PayoutIntent))
                    Reconcile("Aplikacija je zatvorena tokom isplate.");
            }
            // Keep listening until disable+idle has delivered every in-flight cash event.
            using (var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
            {
                try
                {
                    SetInputAsync(false, shutdown.Token).GetAwaiter().GetResult();
                    if (drainInput)
                    {
                        acceptor.WaitForIdleAsync(shutdown.Token).GetAwaiter().GetResult();
                        lock (sync)
                        {
                            if (active != null && !halted)
                            {
                                if (active.HasPendingNote || active.Credits.Any(x => !x.Stacked))
                                    throw new InvalidOperationException("Zatvaranje nema konačnu STACKED potvrdu.");
                                Persist(active.ReceivedMinor > 0 ? TransactionStages.Funded : TransactionStages.Returned);
                            }
                        }
                    }
                }
                catch (Exception exception)
                { lock (sync) { if (active != null && active.Stage != TransactionStages.Debt) Reconcile("Zatvaranje nije potvrdilo završetak prihvata: " + exception.Message); } }
                try { hopper.StopAsync(shutdown.Token).GetAwaiter().GetResult(); } catch { }
            }
            lock (sync)
            {
                if (active != null && active.HasPendingNote && active.Stage != TransactionStages.Debt && !halted)
                    Reconcile("Aplikacija je zatvorena tokom nedovršenog prihvata.");
                disposed = true; collecting = false; draining = false; acceptor.BillEvent -= OnBill;
            }
            acceptor.Dispose(); hopper.Dispose();
        }
        private sealed class InlineProgress : IProgress<int>
        {
            private readonly Action<int> report;
            public InlineProgress(Action<int> action) { report = action; }
            public void Report(int value) { report(value); }
        }
    }
}
