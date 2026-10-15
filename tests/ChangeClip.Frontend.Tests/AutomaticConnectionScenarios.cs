using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ChangeClip.Contracts;
using ChangeClip.Devices;
using ChangeClip.Frontend;

namespace ChangeClip.Frontend.Tests
{
    internal static class AutomaticConnectionScenarios
    {
        private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(15);
        private static DeviceOptions Options()
        { return new DeviceOptions { Mode = "Hardware", CoinValueMinor = 100, AutoDetectPorts = true, Nv9Port = "", HopperPort = "" }; }
        private static DeviceDiscoveryResult Pair()
        { return new DeviceDiscoveryResult("COM7", "COM8", false, true, "Pronađena oba uređaja."); }
        private static ExchangeSnapshot Ready()
        { return new ExchangeSnapshot(ExchangeState.Ready, "Ubacite 10 ili 20 KM.", canAcceptBanknotes: true); }
        private static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Automatic connection: " + message); }
        private static async Task Until(Func<bool> predicate, string message)
        {
            var deadline = Stopwatch.StartNew();
            while (!predicate())
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(4)) throw new TimeoutException("Automatic connection: " + message);
                await Task.Delay(5).ConfigureAwait(false);
            }
        }
        public static async Task RunAsync()
        {
            await NoDevicesReturnsImmediately().ConfigureAwait(false);
            await PlugLaterAndCopyOptions().ConfigureAwait(false);
            await IncompleteAndAmbiguousNeverCreate().ConfigureAwait(false);
            await RetryBeforeCash().ConfigureAwait(false);
            await FundedAndDebtNeverRetry().ConfigureAwait(false);
            await CompletedCashStillLatchesRetry().ConfigureAwait(false);
            await CancellationStopsWaiting().ConfigureAwait(false);
            await LateDiscoveryAfterDispose().ConfigureAwait(false);
            await DisposeDetachesEventsAndStopsInner().ConfigureAwait(false);
            Console.WriteLine("PASS: automatic USB connection lifecycle scenarios");
        }
        private static async Task NoDevicesReturnsImmediately()
        {
            var calls = 0; var engines = 0;
            using (var session = new AutomaticExchangeSession(Options(), (o, t) =>
                { Interlocked.Increment(ref calls); return Task.FromResult(new DeviceDiscoveryResult(null, null, true, false, "Čekanje uređaja.")); },
                o => { Interlocked.Increment(ref engines); return new FakeSession(Ready()); }, Retry))
            {
                var start = session.StartAsync(CancellationToken.None);
                Check(start.IsCompleted, "StartAsync waited for devices");
                await Until(() => Volatile.Read(ref calls) >= 2, "discovery did not retry").ConfigureAwait(false);
                Check(Volatile.Read(ref engines) == 0 && !session.Snapshot.CanExchange && !session.Snapshot.CanAcceptBanknotes, "waiting allowed cash input or created engine");
            }
        }
        private static async Task PlugLaterAndCopyOptions()
        {
            var options = Options(); var plugged = 0; var engines = 0; DeviceOptions received = null;
            using (var session = new AutomaticExchangeSession(options, (o, t) => Task.FromResult(Volatile.Read(ref plugged) == 0
                ? new DeviceDiscoveryResult(null, null, true, false, "Priključite oba uređaja.") : Pair()), o =>
                { received = o.Copy(); Interlocked.Increment(ref engines); return new FakeSession(Ready()); }, Retry))
            {
                await session.StartAsync(CancellationToken.None).ConfigureAwait(false);
                Interlocked.Exchange(ref plugged, 1);
                await Until(() => session.Snapshot.State == ExchangeState.Ready, "plugging both devices did not connect").ConfigureAwait(false);
                Check(Volatile.Read(ref engines) == 1 && received.Nv9Port == "COM7" && received.HopperPort == "COM8" && !received.HopperEcho && !received.AutoDetectPorts, "resolved factory options incorrect");
                Check(options.Nv9Port == "" && options.HopperPort == "" && options.AutoDetectPorts, "original configuration was modified");
            }
        }
        private static async Task IncompleteAndAmbiguousNeverCreate()
        {
            var phase = 0; var engines = 0;
            using (var session = new AutomaticExchangeSession(Options(), (o, t) =>
            {
                var current = Volatile.Read(ref phase);
                return Task.FromResult(current == 0 ? new DeviceDiscoveryResult("COM7", null, true, false, "Nedostaje hopper.")
                    : current == 1 ? new DeviceDiscoveryResult(null, "COM8", true, false, "Nedostaje NV9.")
                    : current == 2 ? new DeviceDiscoveryResult("COM7", "COM8", true, false, "Dvosmislen rezultat.")
                    : current == 3 ? new DeviceDiscoveryResult("COM7", "com7", true, true, "Isti port.") : Pair());
            }, o => { Interlocked.Increment(ref engines); return new FakeSession(Ready()); }, Retry))
            {
                await session.StartAsync(CancellationToken.None).ConfigureAwait(false);
                foreach (var expected in new[] { "Nedostaje hopper.", "Nedostaje NV9.", "Dvosmislen rezultat.", "Isti port." })
                {
                    await Until(() => session.Snapshot.Message == expected, "missing/ambiguous result not shown: " + expected).ConfigureAwait(false);
                    Check(Volatile.Read(ref engines) == 0 && !session.Snapshot.CanExchange && !session.Snapshot.CanAcceptBanknotes, "incomplete/ambiguous pair created engine");
                    Interlocked.Increment(ref phase);
                }
                await Until(() => session.Snapshot.State == ExchangeState.Ready, "valid distinct pair did not connect").ConfigureAwait(false);
                Check(Volatile.Read(ref engines) == 1, "more than one engine created for resolved pair");
            }
        }
        private static async Task RetryBeforeCash()
        {
            var engines = 0; FakeSession first = null;
            using (var session = new AutomaticExchangeSession(Options(), (o, t) => Task.FromResult(Pair()), o =>
            {
                var count = Interlocked.Increment(ref engines);
                var candidate = new FakeSession(Ready());
                if (count == 1) { first = candidate; candidate.OnStart = () => candidate.Publish(new ExchangeSnapshot(ExchangeState.Fault, "USB nije spreman.")); }
                return candidate;
            }, Retry))
            {
                await session.StartAsync(CancellationToken.None).ConfigureAwait(false);
                await Until(() => Volatile.Read(ref engines) == 2 && session.Snapshot.State == ExchangeState.Ready, "pre-cash connection failure was not retried").ConfigureAwait(false);
                Check(first.DisposeCalls == 1 && first.ExchangeCalls == 0, "pre-cash failed engine not disposed or payout invoked");
            }
        }
        private static async Task FundedAndDebtNeverRetry()
        {
            foreach (var stage in new[] { ExchangeState.AwaitingPayout, ExchangeState.Debt, ExchangeState.ReconciliationRequired })
            {
                var engines = 0; FakeSession candidate = null;
                var claims = new[] { new UnresolvedClaim("stored-claim", stage == ExchangeState.AwaitingPayout ? TransactionStages.Funded : stage.ToString(), 1000, 0, stage != ExchangeState.ReconciliationRequired) };
                using (var session = new AutomaticExchangeSession(Options(), (o, t) => Task.FromResult(Pair()), o =>
                {
                    Interlocked.Increment(ref engines);
                    candidate = new FakeSession(new ExchangeSnapshot(stage, "Postojeći bilans.", 1000, 0, 1000,
                        stage == ExchangeState.AwaitingPayout, false, stage != ExchangeState.ReconciliationRequired, "stored-claim", false, claims));
                    candidate.OnStart = () => candidate.Publish(new ExchangeSnapshot(ExchangeState.Fault, "Veza prekinuta.", 1000, 0, 1000, unresolvedClaims: claims));
                    return candidate;
                }, Retry))
                {
                    await session.StartAsync(CancellationToken.None).ConfigureAwait(false);
                    await Until(() => session.Snapshot.Message.Contains("Automatsko ponovno"), "cash fault did not latch retry").ConfigureAwait(false);
                    await session.ExchangeAsync(CancellationToken.None).ConfigureAwait(false);
                    await Task.Delay(65).ConfigureAwait(false);
                    Check(Volatile.Read(ref engines) == 1 && candidate.ExchangeCalls == 0 && session.Snapshot.ReceivedMinor == 1000 && session.Snapshot.UnresolvedClaims.Count == 1, "cash/unresolved balance was replayed or hidden");
                }
                Check(candidate.DisposeCalls == 1 && candidate.ExchangeCalls == 0, "cash shutdown replayed or double-disposed engine");
            }
        }
        private static async Task CompletedCashStillLatchesRetry()
        {
            var engines = 0; FakeSession candidate = null;
            using (var session = new AutomaticExchangeSession(Options(), (o, t) => Task.FromResult(Pair()), o =>
                { Interlocked.Increment(ref engines); return candidate = new FakeSession(Ready()); }, Retry))
            {
                await session.StartAsync(CancellationToken.None).ConfigureAwait(false);
                await Until(() => session.Snapshot.State == ExchangeState.Ready, "initial connection failed").ConfigureAwait(false);
                candidate.Publish(new ExchangeSnapshot(ExchangeState.AwaitingPayout, "Primljeno.", 1000, canExchange: true));
                await session.ExchangeAsync(CancellationToken.None).ConfigureAwait(false);
                Check(candidate.ExchangeCalls == 1 && session.Snapshot.State == ExchangeState.Ready, "manual payout was not delegated exactly once");
                candidate.Publish(new ExchangeSnapshot(ExchangeState.Fault, "USB prekinut poslije isplate."));
                await Until(() => session.Snapshot.Message.Contains("Automatsko ponovno"), "ever-cash latch was cleared by successful payout").ConfigureAwait(false);
                await Task.Delay(60).ConfigureAwait(false);
                Check(Volatile.Read(ref engines) == 1 && candidate.ExchangeCalls == 1, "successful cash history permitted automatic reconnect/replay");
            }
        }
        private static async Task CancellationStopsWaiting()
        {
            var calls = 0;
            using (var cancel = new CancellationTokenSource())
            using (var session = new AutomaticExchangeSession(Options(), (o, t) =>
                { Interlocked.Increment(ref calls); return Task.FromResult(new DeviceDiscoveryResult(null, null, true, false, "Čekanje.")); }, o => new FakeSession(Ready()), Retry))
            {
                await session.StartAsync(cancel.Token).ConfigureAwait(false);
                await Until(() => Volatile.Read(ref calls) > 0, "discovery not started").ConfigureAwait(false);
                cancel.Cancel();
                await Until(() => session.Snapshot.Message.Contains("zaustavljeno"), "start cancellation did not stop waiting").ConfigureAwait(false);
                var stopped = Volatile.Read(ref calls); await Task.Delay(55).ConfigureAwait(false);
                Check(Volatile.Read(ref calls) == stopped && !session.Snapshot.CanAcceptBanknotes, "discovery continued after cancellation");
            }
        }
        private static async Task LateDiscoveryAfterDispose()
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var result = new TaskCompletionSource<DeviceDiscoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var engines = 0; var events = 0;
            var session = new AutomaticExchangeSession(Options(), (o, t) => { entered.TrySetResult(true); return result.Task; },
                o => { Interlocked.Increment(ref engines); return new FakeSession(Ready()); }, Retry);
            session.Changed += (s, e) => Interlocked.Increment(ref events);
            await session.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await Until(() => entered.Task.IsCompleted, "blocked discovery not entered").ConfigureAwait(false);
            var closing = Stopwatch.StartNew(); session.Dispose();
            Check(closing.Elapsed < TimeSpan.FromSeconds(2), "uncooperative discovery blocked closing");
            var stoppedEvents = Volatile.Read(ref events); result.SetResult(Pair()); await Task.Delay(60).ConfigureAwait(false);
            Check(Volatile.Read(ref engines) == 0 && Volatile.Read(ref events) == stoppedEvents, "late discovery created engine or published after Dispose");
        }
        private static async Task DisposeDetachesEventsAndStopsInner()
        {
            FakeSession candidate = null; var events = 0;
            var session = new AutomaticExchangeSession(Options(), (o, t) => Task.FromResult(Pair()), o => candidate = new FakeSession(Ready()), Retry);
            session.Changed += (s, e) => Interlocked.Increment(ref events);
            await session.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await Until(() => session.Snapshot.State == ExchangeState.Ready, "engine not attached").ConfigureAwait(false);
            session.Dispose(); session.Dispose(); var count = Volatile.Read(ref events);
            candidate.Publish(new ExchangeSnapshot(ExchangeState.Debt, "Late callback.", 1000, owedMinor: 1000));
            await Task.Delay(35).ConfigureAwait(false);
            Check(candidate.DisposeCalls == 1 && candidate.ExchangeCalls == 0 && Volatile.Read(ref events) == count, "disposed inner published or was paid/disposed twice");
        }
        private sealed class FakeSession : IExchangeSession
        {
            private readonly object sync = new object();
            private ExchangeSnapshot snapshot;
            public FakeSession(ExchangeSnapshot initial) { snapshot = initial; }
            public event EventHandler<ExchangeSnapshot> Changed;
            public ExchangeSnapshot Snapshot { get { lock (sync) return snapshot; } }
            public Action OnStart;
            public int ExchangeCalls, DisposeCalls;
            public Task StartAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); OnStart?.Invoke(); return Task.CompletedTask; }
            public Task RefreshReadinessAsync(CancellationToken token) { return Task.CompletedTask; }
            public Task ExchangeAsync(CancellationToken token) { Interlocked.Increment(ref ExchangeCalls); Publish(Ready()); return Task.CompletedTask; }
            public Task ReturnAsync(CancellationToken token) { throw new InvalidOperationException("Unexpected return call."); }
            public void Publish(ExchangeSnapshot value) { lock (sync) snapshot = value; Changed?.Invoke(this, value); }
            public void Dispose() { Interlocked.Increment(ref DisposeCalls); }
        }
    }
}
