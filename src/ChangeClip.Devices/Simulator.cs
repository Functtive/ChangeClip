#if !HARDWARE_RELEASE
using System;
using System.Threading;
using System.Threading.Tasks;
using ChangeClip.Contracts;

namespace ChangeClip.Devices
{
    public sealed class SimulatedBillAcceptor : IBillAcceptor
    {
        private readonly object gate = new object();
        private bool connected, enabled, disposed, inFlight;
        private TaskCompletionSource<bool> idle;
        public event EventHandler<BillEventArgs> BillEvent;
        public Task ConnectAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); lock (gate) { Check(); connected = true; } return Task.CompletedTask; }
        public Task SetEnabledAsync(bool value, CancellationToken token) { token.ThrowIfCancellationRequested(); lock (gate) { CheckConnected(); enabled = value; } return Task.CompletedTask; }
        public void InsertBanknote(int amountMinor)
        {
            string id; TaskCompletionSource<bool> completion;
            lock (gate)
            {
                CheckConnected();
                if (!enabled) throw new InvalidOperationException("Simulator: prihvat novčanica je isključen.");
                if (inFlight) throw new InvalidOperationException("Simulator: prethodna novčanica još prolazi kroz uređaj.");
                if (amountMinor != 1000 && amountMinor != 2000) throw new ArgumentOutOfRangeException(nameof(amountMinor), "Dozvoljeno je 10 ili 20 BAM.");
                inFlight = true; id = Guid.NewGuid().ToString("N");
                completion = idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            try
            {
                Emit(BillEventKind.Reading, amountMinor, id);
                Emit(BillEventKind.Escrow, amountMinor, id);
                Emit(BillEventKind.Credit, amountMinor, id);
                Emit(BillEventKind.Stacked, amountMinor, id);
                lock (gate) { inFlight = false; completion.TrySetResult(true); }
            }
            catch (Exception ex) { lock (gate) { inFlight = false; completion.TrySetException(ex); } throw; }
        }
        public async Task WaitForIdleAsync(CancellationToken token)
        {
            Task completion;
            lock (gate) { token.ThrowIfCancellationRequested(); CheckConnected(); completion = idle?.Task ?? Task.CompletedTask; }
            if (await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(20), token)).ConfigureAwait(false) != completion)
            { token.ThrowIfCancellationRequested(); throw new TimeoutException("Simulator input nije završio dispatch događaja."); }
            await completion.ConfigureAwait(false);
        }
        // Legacy support API: autoaccept mode has no user-controlled retained escrow.
        public Task AcceptEscrowAsync(CancellationToken token) => WaitForIdleAsync(token);
        public Task ReturnEscrowAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromException(new NotSupportedException("Automatski prihvaćena novčanica ne može se vratiti; simulator nema zadržani escrow.")); }
        private void Emit(BillEventKind kind, int amount, string id) { BillEvent?.Invoke(this, new BillEventArgs(kind, amount, "BAM", "SIMULATOR - automatski prihvat", id)); }
        private void Check() { if (disposed) throw new ObjectDisposedException(nameof(SimulatedBillAcceptor)); }
        private void CheckConnected() { Check(); if (!connected) throw new InvalidOperationException("Simulator nije spojen."); }
        public void Dispose() { lock (gate) { disposed = true; connected = enabled = false; if (inFlight) idle?.TrySetException(new ObjectDisposedException(nameof(SimulatedBillAcceptor))); } }
    }
    public sealed class SimulatedCoinHopper : ICoinHopper
    {
        private readonly object gate = new object();
        private int coins;
        private bool connected, disposed, paying, stop;
        public SimulatedCoinHopper(int initialCoins) { if (initialCoins < 0) throw new ArgumentOutOfRangeException(nameof(initialCoins)); coins = initialCoins; }
        public int Coins { get { lock (gate) return coins; } }
        public void SetCoins(int value) { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); lock (gate) { Check(); if (paying) throw new InvalidOperationException("Ne mijenjaj zalihu tokom isplate."); coins = value; } }
        public Task ConnectAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); lock (gate) { Check(); connected = true; } return Task.CompletedTask; }
        public Task<HopperStatus> GetStatusAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); lock (gate) { Check(); return Task.FromResult(new HopperStatus(connected, coins == 0, coins)); } }
        public async Task<PayoutResult> PayoutAsync(int requested, IProgress<int> progress, CancellationToken token)
        {
            if (requested <= 0 || requested > 255) throw new ArgumentOutOfRangeException(nameof(requested));
            token.ThrowIfCancellationRequested();
            lock (gate) { Check(); if (!connected) throw new InvalidOperationException("Simulator nije spojen."); if (paying) throw new InvalidOperationException("Isplata je već u toku."); paying = true; stop = false; }
            int confirmed = 0;
            try
            {
                while (confirmed < requested)
                {
                    await Task.Delay(60).ConfigureAwait(false);
                    PayoutOutcome? outcome = null;
                    lock (gate)
                    {
                        if (stop || disposed || token.IsCancellationRequested) outcome = PayoutOutcome.Fault;
                        else if (coins == 0) outcome = PayoutOutcome.Empty;
                        else { coins--; confirmed++; }
                    }
                    if (outcome.HasValue) return new PayoutResult(requested, confirmed, outcome.Value, true, "SIMULATOR: isplata prekinuta.");
                    progress?.Report(confirmed);
                }
                return new PayoutResult(requested, confirmed, PayoutOutcome.Completed, true, "SIMULATOR");
            }
            finally { lock (gate) paying = false; }
        }
        public Task StopAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); lock (gate) stop = true; return Task.CompletedTask; }
        private void Check() { if (disposed) throw new ObjectDisposedException(nameof(SimulatedCoinHopper)); }
        public void Dispose() { lock (gate) { disposed = stop = true; connected = false; } }
    }
    internal sealed class SimulatorControls : ISimulatorControls
    {
        private readonly SimulatedBillAcceptor bill; private readonly SimulatedCoinHopper hopper;
        public SimulatorControls(SimulatedBillAcceptor bill, SimulatedCoinHopper hopper) { this.bill = bill; this.hopper = hopper; }
        public int HopperCoins => hopper.Coins;
        public void InsertBanknote(int amountMinor) => bill.InsertBanknote(amountMinor);
        public void SetHopperCoins(int coins) => hopper.SetCoins(coins);
    }
}
#endif
