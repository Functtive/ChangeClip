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
        private readonly HashSet<string> credits = new HashSet<string>(StringComparer.Ordinal);
        private ExchangeSnapshot snapshot = new ExchangeSnapshot(ExchangeState.Disconnected, "Uređaji nisu povezani.");
        private TransactionRecord active;
        private TaskCompletionSource<BillEventArgs> billConfirmation;
        private bool returning, started, disposed;
        private int escrowAmount;
        private bool escrowValid;
        public ExchangeService(IBillAcceptor billAcceptor, ICoinHopper coinHopper, ITransactionStore transactionStore, int coinValueMinor)
        {
            acceptor = billAcceptor ?? throw new ArgumentNullException(nameof(billAcceptor));
            hopper = coinHopper ?? throw new ArgumentNullException(nameof(coinHopper));
            store = transactionStore ?? throw new ArgumentNullException(nameof(transactionStore));
            if (coinValueMinor <= 0 || 1000 % coinValueMinor != 0 || 2000 % coinValueMinor != 0)
                throw new ArgumentOutOfRangeException(nameof(coinValueMinor), "Apoen mora tačno dijeliti 10 i 20 KM.");
            coinValue = coinValueMinor;
            acceptor.BillEvent += OnBill;
        }
        public event EventHandler<ExchangeSnapshot> Changed;
        public ExchangeSnapshot Snapshot { get { lock (sync) return snapshot; } }
        private static string Money(int minor) { return (minor / 100m).ToString("0.00", CultureInfo.GetCultureInfo("bs-Latn-BA")); }
        private void Publish(ExchangeState state, string message, bool canExchange = false, bool canReturn = false)
        {
            snapshot = new ExchangeSnapshot(state, message, active == null ? escrowAmount : active.ReceivedMinor,
                active == null ? 0 : active.PaidMinor, active == null ? 0 : Math.Max(0, active.ReceivedMinor - active.PaidMinor),
                canExchange, canReturn, active == null || active.IsDebtExact, active == null ? null : active.Id);
            var handlers = Changed;
            if (handlers != null)
                foreach (EventHandler<ExchangeSnapshot> handler in handlers.GetInvocationList())
                    try { handler(this, snapshot); } catch { /* An observer cannot interrupt cash accounting. */ }
        }
        private void Persist(string stage, string message = null)
        {
            active.Stage = stage; active.Message = message; store.Save(active);
        }
        private static bool Closed(TransactionRecord record) { return record.Stage == "Succeeded" || record.Stage == "Returned"; }
        private void EnsureAlive() { if (disposed) throw new ObjectDisposedException(nameof(ExchangeService)); }
        private static bool Usable(HopperStatus status)
        { return status != null && status.Connected && !status.Empty && string.IsNullOrEmpty(status.Fault); }
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            EnsureAlive();
            if (!operation.Wait(0)) return;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token))
            try
            {
                lock (sync) { if (started) return; Publish(ExchangeState.Initializing, "Povezivanje uređaja..."); }
                var records = store.ReadAll();
                lock (sync)
                {
                    foreach (var record in records) if (!string.IsNullOrEmpty(record.CreditEventId)) credits.Add(record.CreditEventId);
                    active = records.LastOrDefault(x => !Closed(x));
                }
                await hopper.ConnectAsync(linked.Token).ConfigureAwait(false);
                await acceptor.ConnectAsync(linked.Token).ConfigureAwait(false);
                await acceptor.SetEnabledAsync(false, linked.Token).ConfigureAwait(false);
                lock (sync)
                {
                    started = true;
                    if (active != null)
                    {
                        if (active.Stage == "Debt" && active.IsDebtExact)
                            Publish(ExchangeState.Debt, "Dužni smo vam još " + Money(active.ReceivedMinor - active.PaidMinor) + " KM.");
                        else
                        {
                            active.IsDebtExact = false;
                            Persist("ReconciliationRequired", "Nezatvorena transakcija nakon pokretanja; nema automatske isplate.");
                            Publish(ExchangeState.ReconciliationRequired, active.Message);
                        }
                        return;
                    }
                }
                await EnableNextAsync(linked.Token, false).ConfigureAwait(false);
            }
            catch (Exception exception) { await FailAsync(exception).ConfigureAwait(false); }
            finally { operation.Release(); }
        }
        private async Task EnableNextAsync(CancellationToken token, bool preserveResult)
        {
            var status = await hopper.GetStatusAsync(token).ConfigureAwait(false);
            if (!Usable(status))
            {
                await acceptor.SetEnabledAsync(false, token).ConfigureAwait(false);
                lock (sync)
                {
                    // A complete confirmed payout stays successful even if its last coin emptied the hopper.
                    if (!preserveResult) Publish(ExchangeState.Fault, status != null && status.Empty ? "Hopper je prazan. Dopunite kovanice." : "Hopper nije spreman: " + (status == null ? "nepoznat status" : status.Fault));
                }
                return;
            }
            lock (sync) { active = null; escrowAmount = 0; escrowValid = false; }
            await acceptor.SetEnabledAsync(true, token).ConfigureAwait(false);
            lock (sync) { if (!preserveResult && snapshot.State != ExchangeState.AwaitingChoice) Publish(ExchangeState.Ready, "Ubacite 10 ili 20 KM."); }
        }
        private void OnBill(object sender, BillEventArgs args)
        {
            lock (sync)
            {
                if (disposed) return;
                try
                {
                    if ((args.Kind == BillEventKind.Credit || args.Kind == BillEventKind.Stacked) && !string.IsNullOrEmpty(args.EventId) && credits.Contains(args.EventId)) return;
                    if (args.Kind == BillEventKind.Escrow)
                    {
                        if (!started || active != null || returning || billConfirmation != null || (snapshot.State != ExchangeState.Ready && snapshot.State != ExchangeState.Succeeded && snapshot.State != ExchangeState.Returned)) return;
                        escrowAmount = args.AmountMinor;
                        escrowValid = (args.Currency == "BAM" && (escrowAmount == 1000 || escrowAmount == 2000));
                        Publish(ExchangeState.AwaitingChoice, escrowValid ? "Zadržano " + Money(escrowAmount) + " KM. Izaberite radnju." : "Nepodržana novčanica. Izaberite Vrati.", escrowValid, true);
                    }
                    else if (args.Kind == BillEventKind.Rejected)
                    {
                        if (billConfirmation != null) billConfirmation.TrySetResult(args);
                        else if (snapshot.State == ExchangeState.AwaitingChoice) { escrowAmount = 0; escrowValid = false; Publish(ExchangeState.Returned, "Novčanica je vraćena."); }
                    }
                    else if (args.Kind == BillEventKind.Credit || args.Kind == BillEventKind.Stacked)
                    {
                        if (active != null && active.ReceivedMinor > 0) return;
                        if (!returning && active != null && active.Stage == "AcceptIntent" && billConfirmation != null)
                        {
                            active.ReceivedMinor = args.AmountMinor > 0 ? args.AmountMinor : active.AmountMinor;
                            active.CreditEventId = args.EventId;
                            if (!string.IsNullOrEmpty(args.EventId)) credits.Add(args.EventId);
                            Persist("Credited");
                            if (active.ReceivedMinor != active.AmountMinor || (args.Currency != null && args.Currency != "BAM"))
                                throw new InvalidOperationException("Potvrđeni apoen/valuta nije jednak escrow iznosu.");
                            billConfirmation.TrySetResult(args); return;
                        }
                        // Unrequested credit must remain visible and durable; never automatically pay it out.
                        active = new TransactionRecord { Id = Guid.NewGuid().ToString("N"), AmountMinor = args.AmountMinor > 0 ? args.AmountMinor : escrowAmount,
                            ReceivedMinor = args.AmountMinor > 0 ? args.AmountMinor : escrowAmount, CoinValueMinor = coinValue, IsDebtExact = false, CreditEventId = args.EventId };
                        Persist("ReconciliationRequired", "Neočekivana potvrda uplate. Potrebna je ručna provjera.");
                        if (!string.IsNullOrEmpty(args.EventId)) credits.Add(args.EventId);
                        Publish(ExchangeState.ReconciliationRequired, active.Message);
                        billConfirmation?.TrySetException(new InvalidOperationException(active.Message));
                        DisableInBackground();
                    }
                    else if (args.Kind == BillEventKind.Fault || args.Kind == BillEventKind.Disconnected)
                    {
                        if (active != null && !Closed(active) && active.Stage != "Debt")
                        { active.IsDebtExact = false; Persist("ReconciliationRequired", args.Message ?? "Prekid uređaja tokom transakcije."); Publish(ExchangeState.ReconciliationRequired, active.Message); }
                        else if (active == null || active.Stage != "Debt") Publish(ExchangeState.Fault, args.Message ?? "Prekid acceptora.");
                        billConfirmation?.TrySetException(new InvalidOperationException(snapshot.Message));
                        lifetime.Cancel();
                        DisableInBackground();
                    }
                }
                catch (Exception exception)
                {
                    if (active != null) active.IsDebtExact = false;
                    Publish(ExchangeState.ReconciliationRequired, "Greška trajne evidencije: " + exception.Message);
                    billConfirmation?.TrySetException(exception); lifetime.Cancel(); DisableInBackground();
                }
            }
        }
        private async void DisableInBackground()
        { try { await acceptor.SetEnabledAsync(false, CancellationToken.None).ConfigureAwait(false); } catch { } }
        public async Task ReturnAsync(CancellationToken cancellationToken)
        {
            EnsureAlive(); if (!operation.Wait(0)) return;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token))
            try
            {
                Task<BillEventArgs> confirmation;
                lock (sync)
                {
                    if (!snapshot.CanReturn) return;
                    returning = true;
                    billConfirmation = new TaskCompletionSource<BillEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                    confirmation = billConfirmation.Task;
                    Publish(ExchangeState.AwaitingChoice, "Vraćanje novčanice...");
                }
                using (linked.Token.Register(() => billConfirmation?.TrySetCanceled()))
                {
                    await acceptor.ReturnEscrowAsync(linked.Token).ConfigureAwait(false);
                    var result = await confirmation.ConfigureAwait(false);
                    if (result.Kind != BillEventKind.Rejected) throw new InvalidOperationException("Vraćanje nije potvrđeno.");
                }
                lock (sync) { returning = false; billConfirmation = null; escrowAmount = 0; escrowValid = false; Publish(ExchangeState.Returned, "Novčanica je vraćena."); }
                await EnableNextAsync(linked.Token, true).ConfigureAwait(false);
            }
            catch (Exception exception) { await FailAsync(exception).ConfigureAwait(false); }
            finally { lock (sync) { returning = false; billConfirmation = null; } operation.Release(); }
        }
        public async Task ExchangeAsync(CancellationToken cancellationToken)
        {
            EnsureAlive(); if (!operation.Wait(0)) return;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token))
            try
            {
                lock (sync) { if (!snapshot.CanExchange || !escrowValid) return; Publish(ExchangeState.AwaitingChoice, "Provjera hoppera..."); }
                var status = await hopper.GetStatusAsync(linked.Token).ConfigureAwait(false);
                if (!Usable(status))
                { lock (sync) Publish(ExchangeState.AwaitingChoice, "Hopper nije spreman. Vratite novčanicu.", false, true); return; }
                Task<BillEventArgs> confirmation;
                lock (sync)
                {
                    if (snapshot.State != ExchangeState.AwaitingChoice || !escrowValid || active != null) return;
                    active = new TransactionRecord { Id = Guid.NewGuid().ToString("N"), AmountMinor = escrowAmount, CoinValueMinor = coinValue, IsDebtExact = true };
                    Persist("AcceptIntent");
                    billConfirmation = new TaskCompletionSource<BillEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                    confirmation = billConfirmation.Task; Publish(ExchangeState.Accepting, "Čekanje potvrde uplate...");
                }
                BillEventArgs credited;
                using (linked.Token.Register(() => billConfirmation?.TrySetCanceled()))
                {
                    await acceptor.AcceptEscrowAsync(linked.Token).ConfigureAwait(false);
                    credited = await confirmation.ConfigureAwait(false);
                }
                if (credited.Kind == BillEventKind.Rejected)
                {
                    lock (sync) { Persist("Returned"); billConfirmation = null; escrowAmount = 0; Publish(ExchangeState.Returned, "Novčanica je vraćena; nema isplate."); }
                    await EnableNextAsync(linked.Token, true).ConfigureAwait(false); return;
                }
                lock (sync)
                {
                    if (active.Stage != "Credited") throw new InvalidOperationException("Transakcija zahtijeva provjeru.");
                    billConfirmation = null;
                }
                await acceptor.SetEnabledAsync(false, linked.Token).ConfigureAwait(false);
                int coins;
                lock (sync)
                { coins = active.ReceivedMinor / coinValue; Persist("PayoutIntent"); Publish(ExchangeState.PayingOut, "Isplata kovanica..."); }
                var payout = await hopper.PayoutAsync(coins, new InlineProgress(count => RecordProgress(count, coins)), linked.Token).ConfigureAwait(false);
                lock (sync)
                {
                    if (active.Stage != "PayoutIntent") throw new InvalidOperationException("Isplata je prekinuta; potrebna je provjera.");
                    if (payout == null || payout.RequestedCoins != coins || payout.ConfirmedCoins < active.PaidMinor / coinValue || payout.ConfirmedCoins > coins || payout.ConfirmedCoins < 0 || !payout.CountReliable || payout.Outcome == PayoutOutcome.Uncertain)
                    {
                        active.IsDebtExact = false; Persist("ReconciliationRequired", "Broj isplaćenih kovanica nije pouzdano potvrđen. Potrebna je ručna provjera.");
                        Publish(ExchangeState.ReconciliationRequired, active.Message); return;
                    }
                    active.PaidMinor = payout.ConfirmedCoins * coinValue;
                    if (active.PaidMinor == active.ReceivedMinor)
                    { Persist("Succeeded"); Publish(ExchangeState.Succeeded, "Uspješno. Isplaćeno " + Money(active.PaidMinor) + " KM."); }
                    else
                    { Persist("Debt", "Dužni smo vam još " + Money(active.ReceivedMinor - active.PaidMinor) + " KM."); Publish(ExchangeState.Debt, active.Message); return; }
                }
                await EnableNextAsync(linked.Token, true).ConfigureAwait(false);
            }
            catch (Exception exception) { await FailAsync(exception).ConfigureAwait(false); }
            finally { lock (sync) billConfirmation = null; operation.Release(); }
        }
        private void RecordProgress(int count, int requested)
        {
            lock (sync)
            {
                if (active == null || active.Stage != "PayoutIntent") throw new InvalidOperationException("Neočekivani napredak isplate.");
                if (count < 0 || count > requested) throw new InvalidOperationException("Neispravan brojač hoppera.");
                if (count <= active.PaidMinor / coinValue) return;
                active.PaidMinor = checked(count * coinValue); Persist("PayoutIntent"); Publish(ExchangeState.PayingOut, "Isplaćeno " + Money(active.PaidMinor) + " KM.");
            }
        }
        private async Task FailAsync(Exception exception)
        {
            lock (sync)
            {
                if (active != null && !Closed(active) && active.Stage != "Debt")
                {
                    active.IsDebtExact = false;
                    try { Persist("ReconciliationRequired", "Transakcija je prekinuta: " + exception.Message); }
                    catch (Exception journalError) { exception = new InvalidOperationException(exception.Message + " Evidencija: " + journalError.Message); }
                    Publish(ExchangeState.ReconciliationRequired, "Potrebna je ručna provjera. " + exception.Message);
                }
                else if (snapshot.State != ExchangeState.Succeeded && snapshot.State != ExchangeState.Returned && snapshot.State != ExchangeState.Debt)
                    Publish(ExchangeState.Fault, "Rad je zaustavljen: " + exception.Message);
            }
            try { await acceptor.SetEnabledAsync(false, CancellationToken.None).ConfigureAwait(false); } catch { }
            try { await hopper.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }
        public void Dispose()
        {
            bool returnEscrow;
            lock (sync)
            {
                if (disposed) return;
                returnEscrow = active == null && escrowAmount > 0;
                disposed = true;
                lifetime.Cancel(); billConfirmation?.TrySetCanceled();
                if (active != null && !Closed(active) && active.Stage != "Debt")
                {
                    active.IsDebtExact = false;
                    try { Persist("ReconciliationRequired", "Aplikacija je zatvorena tokom transakcije."); Publish(ExchangeState.ReconciliationRequired, active.Message); }
                    catch (Exception exception) { Publish(ExchangeState.ReconciliationRequired, "Greška evidencije pri zatvaranju: " + exception.Message); }
                }
                acceptor.BillEvent -= OnBill;
            }
            if (returnEscrow) try { acceptor.ReturnEscrowAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
            try { acceptor.SetEnabledAsync(false, CancellationToken.None).GetAwaiter().GetResult(); } catch { }
            try { hopper.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
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
