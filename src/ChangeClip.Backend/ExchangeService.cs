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
        private bool started, disposed, disposing, collecting, draining, frozen, halted;
        public ExchangeService(IBillAcceptor billAcceptor, ICoinHopper coinHopper, ITransactionStore transactionStore, int coinValueMinor)
        {
            acceptor = billAcceptor ?? throw new ArgumentNullException(nameof(billAcceptor));
            hopper = coinHopper ?? throw new ArgumentNullException(nameof(coinHopper));
            store = transactionStore ?? throw new ArgumentNullException(nameof(transactionStore));
            if (coinValueMinor <= 0 || 1000 % coinValueMinor != 0 || 2000 % coinValueMinor != 0) throw new ArgumentOutOfRangeException(nameof(coinValueMinor));
            coinValue = coinValueMinor; acceptor.BillEvent += OnBill;
        }
        public event EventHandler<ExchangeSnapshot> Changed;
        public ExchangeSnapshot Snapshot { get { lock (sync) return snapshot; } }
        private static string Money(int value) { return (value / 100m).ToString("0.00", CultureInfo.GetCultureInfo("bs-Latn-BA")); }
        private static bool Closed(TransactionRecord row) { return row.Stage == "Succeeded" || row.Stage == "Returned"; }
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
            snapshot = new ExchangeSnapshot(state, message, active?.ReceivedMinor ?? 0, active?.PaidMinor ?? 0,
                active == null ? 0 : Math.Max(0, active.ReceivedMinor - active.PaidMinor), canExchange, false,
                active == null || active.IsDebtExact, active?.Id);
            var handlers = Changed;
            if (handlers != null) foreach (EventHandler<ExchangeSnapshot> handler in handlers.GetInvocationList())
                try { handler(this, snapshot); } catch { /* Observer failures cannot interrupt accounting. */ }
        }
        private void Persist(string stage, string message = null)
        { active.Stage = stage; active.Message = message; store.Save(active); }
        private void Reconcile(string message)
        {
            EnsureWallet(); active.IsDebtExact = false; halted = true; collecting = false;
            try { Persist("ReconciliationRequired", message); }
            catch (Exception error) { message += " Greška evidencije: " + error.Message; }
            Publish(ExchangeState.ReconciliationRequired, message);
        }
        private bool FullyFunded(TransactionRecord row)
        {
            return row.SchemaVersion == 2 && row.Stage == "Funded" && row.CoinValueMinor == coinValue && row.IsDebtExact && !row.HasPendingNote && row.PaidMinor == 0 && row.ReceivedMinor > 0 && row.Credits != null && row.Credits.Count > 0
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
                    var unresolved = records.Where(x => !Closed(x)).ToArray(); active = unresolved.LastOrDefault();
                    if (unresolved.Length > 1) Reconcile("Više nezatvorenih bilansa. Potrebna je ručna provjera svih journal zapisa.");
                    else if (active != null && !FullyFunded(active) && !(active.SchemaVersion == 2 && active.Stage == "Debt" && active.IsDebtExact))
                        Reconcile("Nezatvoren ili stari nalog nakon pokretanja; nema automatske isplate.");
                }
                await hopper.ConnectAsync(linked.Token).ConfigureAwait(false);
                await acceptor.ConnectAsync(linked.Token).ConfigureAwait(false);
                await acceptor.SetEnabledAsync(false, linked.Token).ConfigureAwait(false);
                lock (sync)
                {
                    started = true;
                    if (halted) return;
                    if (active != null && active.Stage == "Debt") { halted = true; Publish(ExchangeState.Debt, DebtMessage()); return; }
                }
                await EnableCollectionAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception exception) { await FailAsync(exception).ConfigureAwait(false); }
            finally { operation.Release(); }
        }
        private async Task EnableCollectionAsync(CancellationToken token, string readyMessage = null)
        {
            var status = await hopper.GetStatusAsync(token).ConfigureAwait(false);
            if (!Connected(status)) throw new InvalidOperationException("Hopper nije povezan/spreman: " + (status?.Fault ?? "nepoznat status"));
            lock (sync)
            {
                if (halted || disposed) return;
                collecting = true; frozen = false; draining = false;
                Publish(active == null ? ExchangeState.Ready : ExchangeState.AwaitingPayout,
                    active == null ? (readyMessage ?? "Ubacite 10 ili 20 KM.") : "Primljeno " + Money(active.ReceivedMinor) + " KM. Možete ubaciti još ili izabrati Razmijeni.", active != null && active.ReceivedMinor > 0);
            }
            await acceptor.SetEnabledAsync(true, token).ConfigureAwait(false);
        }
        private void OnBill(object sender, BillEventArgs args)
        {
            lock (sync)
            {
                if (disposed) return;
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
                        Persist("FundingPending");
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
                        Persist("FundingPending");
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
                        if (halted || frozen) { Persist("ReconciliationRequired", active.Message); return; }
                        Persist(active.HasPendingNote ? "FundingPending" : "Funded");
                        Publish(draining ? ExchangeState.Accepting : ExchangeState.AwaitingPayout,
                            "Primljeno " + Money(active.ReceivedMinor) + " KM. Izaberite Razmijeni ili ubacite još.", !draining && active.ReceivedMinor > 0);
                    }
                    else if (args.Kind == BillEventKind.Rejected)
                    {
                        if (halted || frozen || active == null) return;
                        if (active.Credits.Any(x => !x.Stacked && x.EventId == active.PendingEventId))
                        { Reconcile("REJECTED nakon CREDIT potvrde. Potrebna je ručna provjera."); lifetime.Cancel(); DisableInBackground(); return; }
                        active.HasPendingNote = false; active.PendingEventId = null;
                        if (active.ReceivedMinor == 0) { Persist("Returned"); active = null; Publish(ExchangeState.Ready, "Novčanica nije prihvaćena."); }
                        else { Persist("Funded"); Publish(draining ? ExchangeState.Accepting : ExchangeState.AwaitingPayout, "Primljeno " + Money(active.ReceivedMinor) + " KM.", !draining); }
                    }
                    else if (args.Kind == BillEventKind.Fault || args.Kind == BillEventKind.Disconnected)
                    {
                        if (active != null && (active.HasPendingNote || frozen || draining || active.Stage == "PayoutIntent")) Reconcile(args.Message ?? "Prekid tokom aktivnog naloga.");
                        else if (active?.Stage != "Debt") { halted = true; collecting = false; Publish(ExchangeState.Fault, args.Message ?? "Prekid acceptora."); }
                        lifetime.Cancel(); DisableInBackground();
                    }
                }
                catch (Exception error)
                { Reconcile("Greška trajne evidencije uplate: " + error.Message); lifetime.Cancel(); DisableInBackground(); }
            }
        }
        private async void DisableInBackground()
        { try { await acceptor.SetEnabledAsync(false, CancellationToken.None).ConfigureAwait(false); } catch { } }
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
                await acceptor.SetEnabledAsync(false, linked.Token).ConfigureAwait(false);
                await acceptor.WaitForIdleAsync(linked.Token).ConfigureAwait(false);
                int totalCoins;
                lock (sync)
                {
                    if (halted || disposed) throw new InvalidOperationException("Bilans zahtijeva provjeru.");
                    // Idle is only a fence; final note evidence must already come from real events.
                    if (active.HasPendingNote || active.Credits.Any(x => !x.Stacked))
                        throw new InvalidOperationException("Idle bez konačne STACKED potvrde za sve uplate.");
                    Persist("Funded");
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
                        { Persist("Debt", DebtMessage()); halted = true; frozen = false; Publish(ExchangeState.Debt, active.Message); return; }
                    }
                    PayoutBatchRecord batch;
                    lock (sync)
                    {
                        var baseCoins = active.PaidMinor / coinValue;
                        batch = new PayoutBatchRecord { Number = active.Batches.Count + 1, BaseConfirmedCoins = baseCoins,
                            RequestedCoins = Math.Min(255, totalCoins - baseCoins), Stage = "Intent" };
                        active.Batches.Add(batch); Persist("PayoutIntent");
                    }
                    var result = await hopper.PayoutAsync(batch.RequestedCoins, new InlineProgress(count => RecordProgress(batch, count)), linked.Token).ConfigureAwait(false);
                    bool complete;
                    lock (sync)
                    {
                        if (halted || disposed || active.Stage != "PayoutIntent") throw new InvalidOperationException("Isplata je prekinuta; potrebna je provjera.");
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
                            Persist("Succeeded", "Uspješno. Isplaćeno " + Money(active.PaidMinor) + " KM.");
                            successMessage = "Uspješno. Isplaćeno " + Money(active.PaidMinor) + " KM. Ubacite 10 ili 20 KM za novu razmjenu.";
                            active = null; frozen = false; Publish(ExchangeState.Ready, successMessage);
                        }
                        else if (batch.Stage == "Partial" || result.Outcome != PayoutOutcome.Completed)
                        { Persist("Debt", DebtMessage()); halted = true; frozen = false; Publish(ExchangeState.Debt, active.Message); return; }
                        else Persist("PayoutIntent"); // A completed batch is durable; restart still never replays the remaining batch.
                    }
                    if (complete) { await EnableCollectionAsync(linked.Token, successMessage).ConfigureAwait(false); return; }
                }
            }
            catch (Exception exception) { await FailAsync(exception).ConfigureAwait(false); }
            finally { operation.Release(); }
        }
        private string DebtMessage() { return "Dužni smo vam još " + Money(Math.Max(0, active.ReceivedMinor - active.PaidMinor)) + " KM."; }
        private void RecordProgress(PayoutBatchRecord batch, int count)
        {
            lock (sync)
            {
                if (active == null || halted || disposed || active.Stage != "PayoutIntent" || active.Batches.Last() != batch) throw new InvalidOperationException("Neočekivani napredak isplate.");
                if (count < 0 || count > batch.RequestedCoins) throw new InvalidOperationException("Neispravan brojač hoppera.");
                if (count <= batch.ConfirmedCoins) return;
                batch.ConfirmedCoins = count; active.PaidMinor = checked((batch.BaseConfirmedCoins + count) * coinValue);
                Persist("PayoutIntent"); Publish(ExchangeState.PayingOut, "Isplaćeno " + Money(active.PaidMinor) + " KM.");
            }
        }
        private async Task FailAsync(Exception exception)
        {
            lock (sync)
            {
                halted = true; collecting = false;
                if (active != null && active.Stage != "Debt" && (active.HasPendingNote || frozen || draining || active.Stage == "PayoutIntent" || active.Stage == "FundingPending"))
                    Reconcile("Transakcija je prekinuta: " + exception.Message);
                else if (active?.Stage == "Debt") Publish(ExchangeState.Debt, DebtMessage());
                else if (snapshot.State != ExchangeState.ReconciliationRequired) Publish(ExchangeState.Fault, "Rad je zaustavljen: " + exception.Message);
            }
            try { await acceptor.SetEnabledAsync(false, CancellationToken.None).ConfigureAwait(false); } catch { }
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
                if (active != null && active.Stage != "Debt" && (frozen || active.Stage == "PayoutIntent"))
                    Reconcile("Aplikacija je zatvorena tokom isplate.");
            }
            // Keep listening until disable+idle has delivered every in-flight cash event.
            using (var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
            {
                try
                {
                    acceptor.SetEnabledAsync(false, shutdown.Token).GetAwaiter().GetResult();
                    if (drainInput)
                    {
                        acceptor.WaitForIdleAsync(shutdown.Token).GetAwaiter().GetResult();
                        lock (sync)
                        {
                            if (active != null && !halted)
                            {
                                if (active.HasPendingNote || active.Credits.Any(x => !x.Stacked))
                                    throw new InvalidOperationException("Zatvaranje nema konačnu STACKED potvrdu.");
                                Persist(active.ReceivedMinor > 0 ? "Funded" : "Returned");
                            }
                        }
                    }
                }
                catch (Exception exception)
                { lock (sync) { if (active != null && active.Stage != "Debt") Reconcile("Zatvaranje nije potvrdilo završetak prihvata: " + exception.Message); } }
                try { hopper.StopAsync(shutdown.Token).GetAwaiter().GetResult(); } catch { }
            }
            lock (sync)
            {
                if (active != null && active.HasPendingNote && active.Stage != "Debt" && !halted)
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
