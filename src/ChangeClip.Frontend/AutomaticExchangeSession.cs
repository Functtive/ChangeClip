using System;
using System.Threading;
using System.Threading.Tasks;
using ChangeClip.Contracts;
using ChangeClip.Devices;

namespace ChangeClip.Frontend
{
    // Connection lifecycle only. Cash accounting and every payout remain inside ExchangeService.
    public sealed class AutomaticExchangeSession : IExchangeSession
    {
        private readonly object sync = new object();
        private readonly DeviceOptions options;
        private readonly Func<DeviceOptions, CancellationToken, Task<DeviceDiscoveryResult>> discover;
        private readonly Func<DeviceOptions, IExchangeSession> sessionFactory;
        private readonly TimeSpan retryDelay;
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        private CancellationTokenSource runCancellation;
        private IExchangeSession inner;
        private Task loop;
        private TaskCompletionSource<bool> failure;
        private ExchangeSnapshot snapshot = new ExchangeSnapshot(ExchangeState.Disconnected, "Čekanje USB uređaja. Priključite NV9 i hopper.");
        private bool started, disposed, cashObserved;
        public AutomaticExchangeSession(DeviceOptions deviceOptions,
            Func<DeviceOptions, CancellationToken, Task<DeviceDiscoveryResult>> discovery,
            Func<DeviceOptions, IExchangeSession> createSession, TimeSpan? retryInterval = null)
        {
            options = (deviceOptions ?? throw new ArgumentNullException(nameof(deviceOptions))).Copy();
            discover = discovery ?? throw new ArgumentNullException(nameof(discovery));
            sessionFactory = createSession ?? throw new ArgumentNullException(nameof(createSession));
            retryDelay = retryInterval ?? TimeSpan.FromSeconds(2.5);
            if (retryDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryInterval));
        }
        public event EventHandler<ExchangeSnapshot> Changed;
        public ExchangeSnapshot Snapshot { get { lock (sync) return snapshot; } }
        public Task StartAsync(CancellationToken cancellationToken)
        {
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException(nameof(AutomaticExchangeSession));
                if (started) return Task.CompletedTask;
                started = true;
                runCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, cancellationToken);
                var runToken = runCancellation.Token;
                // Scheduling the loop is deliberately the only synchronous startup action.
                loop = Task.Run(() => RunAsync(runToken));
            }
            return Task.CompletedTask;
        }
        private void ObserveMoney(ExchangeSnapshot state)
        {
            if (state == null) return;
            if (state.ReceivedMinor > 0 || state.PaidMinor > 0 || state.OwedMinor > 0 || state.UnresolvedClaims.Count > 0
                || state.State == ExchangeState.Accepting || state.State == ExchangeState.PayingOut
                || state.State == ExchangeState.Debt || state.State == ExchangeState.ReconciliationRequired)
                cashObserved = true;
        }
        private static bool ConnectionFailed(ExchangeState state)
        { return state == ExchangeState.Fault || state == ExchangeState.Disconnected || state == ExchangeState.Debt || state == ExchangeState.ReconciliationRequired; }
        private void Publish(ExchangeSnapshot state)
        {
            lock (sync)
            {
                if (disposed) return;
                snapshot = state;
                var handlers = Changed;
                if (handlers != null) foreach (EventHandler<ExchangeSnapshot> handler in handlers.GetInvocationList())
                    try { handler(this, state); } catch { /* An observer cannot change connection/cash ownership. */ }
            }
        }
        private ExchangeSnapshot Disabled(ExchangeState state, string message, ExchangeSnapshot source = null)
        {
            lock (sync)
            {
                source = source ?? snapshot;
                return new ExchangeSnapshot(state, message, source.ReceivedMinor, source.PaidMinor, source.OwedMinor,
                    false, false, source.IsDebtExact, source.TransactionId, false, source.UnresolvedClaims);
            }
        }
        private void InnerChanged(object sender, ExchangeSnapshot state)
        {
            lock (sync)
            {
                if (disposed || !ReferenceEquals(sender, inner)) return;
                ObserveMoney(state);
                Publish(state);
                if (ConnectionFailed(state.State)) failure?.TrySetResult(true);
            }
        }
        private async Task RunAsync(CancellationToken token)
        {
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    lock (sync) { if (disposed || cashObserved) return; }
                    Publish(Disabled(ExchangeState.Initializing, "Traženje NV9 i hopper USB uređaja..."));
                    IExchangeSession candidate = null;
                    try
                    {
                        var detected = await discover(options.Copy(), token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (detected == null || !detected.Complete || string.IsNullOrWhiteSpace(detected.Nv9Port) || string.IsNullOrWhiteSpace(detected.HopperPort)
                            || string.Equals(detected.Nv9Port, detected.HopperPort, StringComparison.OrdinalIgnoreCase))
                        {
                            Publish(Disabled(ExchangeState.Disconnected, detected?.Message ?? "Uređaji nisu pronađeni. Priključite NV9 i hopper."));
                            await Task.Delay(retryDelay, token).ConfigureAwait(false); continue;
                        }
                        var resolved = options.Copy(); resolved.Nv9Port = detected.Nv9Port; resolved.HopperPort = detected.HopperPort;
                        resolved.HopperEcho = detected.HopperEcho; resolved.AutoDetectPorts = false;
                        lock (sync) { if (disposed || cashObserved) return; }
                        candidate = sessionFactory(resolved);
                        if (candidate == null) throw new InvalidOperationException("Nije moguće pripremiti sesiju uređaja.");
                        TaskCompletionSource<bool> failed;
                        lock (sync)
                        {
                            if (disposed) { failed = null; }
                            else
                            {
                                inner = candidate; failure = failed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                                candidate.Changed += InnerChanged; ObserveMoney(candidate.Snapshot); Publish(candidate.Snapshot);
                            }
                        }
                        if (failed == null) { candidate.Dispose(); candidate = null; return; }
                        await candidate.StartAsync(token).ConfigureAwait(false);
                        lock (sync)
                        {
                            if (disposed) return;
                            ObserveMoney(candidate.Snapshot); Publish(candidate.Snapshot);
                            if (ConnectionFailed(candidate.Snapshot.State)) failed.TrySetResult(true);
                        }
                        await AwaitFailureAsync(failed.Task, token).ConfigureAwait(false);
                        bool blocked;
                        lock (sync) { ObserveMoney(candidate.Snapshot); blocked = cashObserved; }
                        if (blocked)
                        {
                            Publish(Disabled(candidate.Snapshot.State,
                                candidate.Snapshot.Message + " Automatsko ponovno povezivanje je zaustavljeno zbog novčanog bilansa. Ponovo pokrenite aplikaciju ili zatražite servisnu provjeru.", candidate.Snapshot));
                            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); return;
                        }
                        DetachAndDispose(candidate);
                        lock (sync) { ObserveMoney(candidate.Snapshot); blocked = cashObserved; }
                        if (blocked)
                        {
                            Publish(Disabled(ExchangeState.ReconciliationRequired,
                                "Novčani bilans je evidentiran tokom prekida veze. Potrebna je ponovna prijava/servisna provjera; nema automatskog povezivanja ni isplate.", candidate.Snapshot));
                            candidate = null; await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); return;
                        }
                        candidate = null;
                        Publish(Disabled(ExchangeState.Disconnected, "Veza s uređajima je prekinuta. Ponovno traženje USB uređaja..."));
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        ExchangeSnapshot last = candidate?.Snapshot;
                        bool blocked;
                        lock (sync) { ObserveMoney(last); blocked = cashObserved; }
                        if (candidate != null)
                        {
                            DetachAndDispose(candidate);
                            lock (sync) { ObserveMoney(candidate.Snapshot); blocked = cashObserved; }
                            last = candidate.Snapshot;
                        }
                        if (blocked)
                        {
                            Publish(Disabled(ExchangeState.ReconciliationRequired,
                                "Povezivanje je zaustavljeno uz evidentiran novčani bilans. Ponovo pokrenite aplikaciju ili zatražite servisnu provjeru. " + error.Message, last));
                            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); return;
                        }
                        Publish(Disabled(ExchangeState.Disconnected, "Uređaji još nisu spremni. " + error.Message));
                    }
                    await Task.Delay(retryDelay, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                IExchangeSession current;
                lock (sync) current = inner;
                ExchangeSnapshot stopped = null;
                if (current != null)
                {
                    DetachAndDispose(current);
                    stopped = current.Snapshot;
                    lock (sync) ObserveMoney(stopped);
                }
                Publish(Disabled(cashObserved ? ExchangeState.Fault : ExchangeState.Disconnected,
                    cashObserved ? "Povezivanje je zaustavljeno. Evidentirani novac ostaje u trajnom bilansu; ponovo pokrenite aplikaciju ili zatražite servis." : "Čekanje uređaja je zaustavljeno.", stopped));
            }
        }
        private static async Task AwaitFailureAsync(Task failed, CancellationToken token)
        {
            using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                try
                {
                    var cancelled = Task.Delay(Timeout.Infinite, waiting.Token);
                    await Task.WhenAny(failed, cancelled).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    await failed.ConfigureAwait(false);
                }
                finally { waiting.Cancel(); }
            }
        }
        private void DetachAndDispose(IExchangeSession candidate)
        {
            bool owns;
            lock (sync)
            {
                owns = ReferenceEquals(inner, candidate);
                if (owns) { candidate.Changed -= InnerChanged; inner = null; failure = null; }
            }
            if (owns) candidate.Dispose();
        }
        public Task ExchangeAsync(CancellationToken cancellationToken)
        {
            IExchangeSession current;
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException(nameof(AutomaticExchangeSession));
                current = snapshot.CanExchange ? inner : null;
            }
            return current != null ? current.ExchangeAsync(cancellationToken) : Task.CompletedTask;
        }
        public Task RefreshReadinessAsync(CancellationToken cancellationToken)
        {
            IExchangeSession current;
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException(nameof(AutomaticExchangeSession));
                current = inner;
            }
            return current != null ? current.RefreshReadinessAsync(cancellationToken) : Task.CompletedTask;
        }
        public Task ReturnAsync(CancellationToken cancellationToken)
        { throw new NotSupportedException("Automatski spremljene novčanice se ne vraćaju iz aplikacije."); }
        public void Dispose()
        {
            IExchangeSession current; Task running;
            lock (sync)
            {
                if (disposed) return;
                disposed = true; current = inner; inner = null; failure = null; running = loop;
                if (current != null) current.Changed -= InnerChanged;
            }
            shutdown.Cancel();
            // The financial engine establishes its own durable shutdown fence before this returns.
            current?.Dispose();
            // A discovery implementation must honor cancellation. A broken probe still cannot attach/start
            // another financial engine or publish after the disposed fence, so closing the UI stays bounded.
            if (running != null && !running.IsCompleted) try { running.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
        }
    }
}
