using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ChangeClip.Contracts;
using ChangeClip.Devices;

namespace ChangeClip.Service
{
    public sealed class HopperTestReading : EventArgs
    {
        public HopperTestReading(int requested, int confirmed, bool reliable, PayoutOutcome outcome, string message, bool isDispensing = false)
        { RequestedCoins = requested; ConfirmedCoins = confirmed; CountReliable = reliable; Outcome = outcome; Message = message; IsDispensing = isDispensing; }
        public int RequestedCoins { get; private set; }
        public int ConfirmedCoins { get; private set; }
        public int RemainingCoins { get { return Math.Max(0, RequestedCoins - ConfirmedCoins); } }
        public bool CountReliable { get; private set; }
        public PayoutOutcome Outcome { get; private set; }
        public string Message { get; private set; }
        public bool IsDispensing { get; private set; }
    }

    public sealed class HopperTestSession
    {
        private readonly object sync = new object();
        private readonly DeviceOptions options;
        private readonly Func<DeviceOptions, ICoinHopper> factory;
        private Task<HopperTestReading> operation;
        private CancellationTokenSource stopping;
        private ICoinHopper hopper;
        private ApplicationLease lease;
        private readonly SemaphoreSlim cleanup = new SemaphoreSlim(1, 1);
        private bool closed, active;
        public HopperTestSession(DeviceOptions deviceOptions, Func<DeviceOptions, ICoinHopper> createHopper = null)
        {
            options = (deviceOptions ?? throw new ArgumentNullException(nameof(deviceOptions))).Copy();
            // An independent hopper test does not require NV9 configuration or a coin denomination.
            options.Nv9Port = ""; options.Nv9Address = 0; options.Nv9FixedKeyHex = "";
            DeviceOptionsValidator.ValidateConnectionSettings(options);
            if (string.IsNullOrWhiteSpace(options.HopperPort)) throw new InvalidDataException("Prvo pronađite ili upišite hopper COM port.");
            factory = createHopper ?? (value => new AzkoyenCoinHopper(value));
        }
        public event EventHandler<HopperTestReading> Changed;
        public bool IsActive { get { lock (sync) return active; } }
        public Task<HopperTestReading> DispenseAsync(int requestedCoins, CancellationToken token = default(CancellationToken))
        {
            TaskCompletionSource<HopperTestReading> completion;
            lock (sync)
            {
                if (closed) throw new ObjectDisposedException(nameof(HopperTestSession));
                if (active) return operation;
                if (requestedCoins < 1 || requestedCoins > 10000) throw new ArgumentOutOfRangeException(nameof(requestedCoins), "Broj kovanica mora biti od 1 do 10000.");
                active = true;
                stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
                completion = new TaskCompletionSource<HopperTestReading>(TaskCreationOptions.RunContinuationsAsynchronously);
                operation = completion.Task;
            }
            _ = Task.Run(() => RunAsync(requestedCoins, completion));
            return completion.Task;
        }
        private void Report(HopperTestReading reading)
        {
            var handlers = Changed;
            if (handlers != null) foreach (EventHandler<HopperTestReading> handler in handlers.GetInvocationList())
                try { handler(this, reading); } catch { /* Observers never control the payout command. */ }
        }
        private async Task RunAsync(int requested, TaskCompletionSource<HopperTestReading> completion)
        {
            int confirmed = 0; bool commandStarted = false;
            ICoinHopper device = null; HopperTestReading final = null;
            CancellationToken token;
            lock (sync) token = stopping.Token;
            try
            {
                token.ThrowIfCancellationRequested();
                var acquired = ApplicationLease.Acquire();
                lock (sync) lease = acquired;
                device = factory(options.Copy());
                if (device == null) throw new InvalidOperationException("Hopper nije pripremljen.");
                lock (sync) hopper = device;
                Report(new HopperTestReading(requested, 0, true, PayoutOutcome.Completed, "Povezivanje hoppera..."));
                await device.ConnectAsync(token).ConfigureAwait(false);
                while (confirmed < requested)
                {
                    token.ThrowIfCancellationRequested();
                    int batch = Math.Min(255, requested - confirmed), baseCount = confirmed, batchConfirmed = 0;
                    commandStarted = true;
                    Report(new HopperTestReading(requested, confirmed, true, PayoutOutcome.Completed, "Isplata u toku.", true));
                    var result = await device.PayoutAsync(batch, new InlineProgress(count =>
                    {
                        if (count < batchConfirmed || count < 0 || count > batch) throw new InvalidDataException("Nepouzdan hopper brojač.");
                        if (count == batchConfirmed) return;
                        batchConfirmed = count; confirmed = baseCount + count;
                        Report(new HopperTestReading(requested, confirmed, true, PayoutOutcome.Completed, "Isplata u toku.", true));
                    }), token).ConfigureAwait(false);
                    if (result == null || result.RequestedCoins != batch || result.ConfirmedCoins < batchConfirmed || result.ConfirmedCoins > batch || result.ConfirmedCoins < 0 || !result.CountReliable || result.Outcome == PayoutOutcome.Uncertain)
                    {
                        final = new HopperTestReading(requested, confirmed, false, PayoutOutcome.Uncertain,
                            "Nepouzdana konačna isplata. Nalog nije ponovljen; ručno provjerite kovanice." + (result == null ? "" : " " + result.Message));
                        break;
                    }
                    confirmed = baseCount + result.ConfirmedCoins;
                    if (confirmed == requested)
                    { final = new HopperTestReading(requested, confirmed, true, PayoutOutcome.Completed, "Završeno. Potvrđene su sve kovanice."); break; }
                    if (result.ConfirmedCoins != batch || result.Outcome != PayoutOutcome.Completed || token.IsCancellationRequested)
                    { final = new HopperTestReading(requested, confirmed, true, result.Outcome, "Isplata zaustavljena. Preostalo: " + (requested - confirmed) + ". " + result.Message); break; }
                    commandStarted = false;
                }
            }
            catch (Exception error)
            {
                final = new HopperTestReading(requested, confirmed, !commandStarted,
                    commandStarted ? PayoutOutcome.Uncertain : PayoutOutcome.Fault,
                    (commandStarted ? "Nema pouzdane završne potvrde; nalog nije ponovljen. " : "Test nije pokrenut/završen. ") + error.Message);
            }
            finally
            {
                // A failed port close retains both transport ownership and the application lease.
                try { await CloseTransportAsync().ConfigureAwait(false); }
                catch (Exception closeError)
                {
                    final = new HopperTestReading(requested, confirmed, final == null || final.CountReliable,
                        PayoutOutcome.Fault, (final?.Message ?? "") + " COM port nije potvrđeno zatvoren: " + closeError.Message + " Ponovite zatvaranje; nova isplata je blokirana.");
                }
                final = final ?? new HopperTestReading(requested, confirmed, true, PayoutOutcome.Completed, "Završeno.");
                Report(final);
                completion.TrySetResult(final);
            }
        }
        public async Task ShutdownAsync()
        {
            Task<HopperTestReading> pending; ICoinHopper device;
            lock (sync)
            {
                closed = true; pending = operation; device = hopper;
                stopping?.Cancel();
            }
            if (device != null) try { await device.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            if (pending != null) await pending.ConfigureAwait(false);
            await CloseTransportAsync().ConfigureAwait(false);
        }
        private async Task CloseTransportAsync()
        {
            await cleanup.WaitAsync().ConfigureAwait(false);
            try
            {
                ICoinHopper device; ApplicationLease owned;
                lock (sync) { device = hopper; owned = lease; }
                if (device != null)
                {
                    try { await device.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                    device.Dispose(); // Deliberately propagates: an unclosed port cannot lose its lease.
                }
                owned?.Dispose();
                lock (sync) { hopper = null; lease = null; active = false; stopping?.Dispose(); stopping = null; }
            }
            finally { cleanup.Release(); }
        }
        private sealed class InlineProgress : IProgress<int>
        {
            private readonly Action<int> action;
            public InlineProgress(Action<int> report) { action = report; }
            public void Report(int value) { action(value); }
        }
    }
}
