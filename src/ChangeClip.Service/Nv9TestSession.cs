using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ChangeClip.Contracts;
using ChangeClip.Devices;

namespace ChangeClip.Service
{
    public sealed class Nv9TestSession
    {
        private readonly object state = new object();
        private readonly SemaphoreSlim transition = new SemaphoreSlim(1, 1);
        private readonly Func<DeviceOptions, IBillAcceptor> factory;
        private readonly Dictionary<string, int> accepted = new Dictionary<string, int>(StringComparer.Ordinal);
        private IBillAcceptor acceptor;
        private ApplicationLease lease;
        private CancellationTokenSource starting;
        private bool active, shutdown, stopRequested;
        private int received;
        private string status = "NV9 nije aktivan. Accept stvarno pohranjuje 10/20 BAM novčanice.";
        private string fault;
        public Nv9TestSession(Func<DeviceOptions, IBillAcceptor> factory = null)
        { this.factory = factory ?? (options => new Nv9BillAcceptor(options)); }
        public bool IsActive { get { lock (state) return active; } }
        public int ReceivedMinor { get { lock (state) return received; } }
        public string Status { get { lock (state) return status; } }
        public event EventHandler StateChanged;
        public async Task StartAsync(DeviceOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var settings = options.Copy();
            settings.Mode = "Hardware"; settings.AutoDetectPorts = false;
            // NV9 testing is independent of hopper settings and coin denomination.
            settings.HopperPort = ""; settings.HopperAddress = 3;
            DeviceOptionsValidator.ValidateConnectionSettings(settings);
            if (string.IsNullOrWhiteSpace(settings.Nv9Port)) throw new InvalidDataException("Prvo pronađite ili podesite NV9 COM port.");
            await transition.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (state)
                {
                    if (shutdown) throw new ObjectDisposedException(nameof(Nv9TestSession));
                    if (active) return;
                    active = true; stopRequested = false; fault = null;
                    starting = new CancellationTokenSource(); status = "Povezivanje NV9…";
                }
                Notify();
                try
                {
                    lease = await Task.Run(() => ApplicationLease.Acquire()).ConfigureAwait(false);
                    var created = await Task.Run(() => factory(settings)).ConfigureAwait(false);
                    if (created == null) throw new InvalidOperationException("NV9 factory nije vratio uređaj.");
                    lock (state) acceptor = created;
                    created.BillEvent += OnBill;
                    await created.ConnectAsync(starting.Token).ConfigureAwait(false);
                    ThrowIfStopping();
                    await created.SetEnabledAsync(true, starting.Token).ConfigureAwait(false);
                    ThrowIfStopping();
                    lock (state) status = "Accept aktivan. Ubacite 10 ili 20 BAM; novčanice se stvarno pohranjuju.";
                    Notify();
                }
                catch (Exception error)
                {
                    lock (state)
                    {
                        if (!(error is OperationCanceledException && stopRequested)) fault = fault ?? error.Message;
                        status = fault ?? "Prihvat se zaustavlja…";
                    }
                    Notify();
                    await CloseDeviceAsync().ConfigureAwait(false);
                    throw;
                }
            }
            finally { transition.Release(); }
        }
        private void ThrowIfStopping()
        {
            lock (state)
            {
                if (fault != null) throw new IOException(fault);
                if (stopRequested || shutdown) throw new OperationCanceledException();
            }
        }
        public async Task StopAsync()
        {
            lock (state) { stopRequested = true; starting?.Cancel(); }
            await transition.WaitAsync().ConfigureAwait(false);
            try { await CloseDeviceAsync().ConfigureAwait(false); }
            finally { transition.Release(); }
        }
        public Task ShutdownAsync()
        {
            lock (state) shutdown = true;
            return StopAsync();
        }
        private async Task CloseDeviceAsync()
        {
            IBillAcceptor device;
            lock (state) device = acceptor;
            string closeError = null;
            if (device != null)
            {
                lock (state) status = fault == null ? "Zaustavljanje Accept; čekam završetak novčanice…" : "Greška NV9: " + fault + " Zaustavljam prihvat…";
                Notify();
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
                {
                    try { await device.SetEnabledAsync(false, timeout.Token).ConfigureAwait(false); }
                    catch (Exception error) { closeError = error.Message; }
                    try { await device.WaitForIdleAsync(timeout.Token).ConfigureAwait(false); }
                    catch (Exception error) { closeError = closeError ?? error.Message; }
                }
                try { await Task.Run(() => device.Dispose()).ConfigureAwait(false); }
                catch (Exception error)
                {
                    // Keep the lease and the device reference: a failed close is not evidence of a closed port.
                    lock (state) { fault = fault ?? closeError ?? error.Message; status = "NV9 port nije potvrđeno zatvoren: " + error.Message + ". Ponovite zaustavljanje prije zatvaranja prozora."; }
                    Notify(); throw;
                }
                device.BillEvent -= OnBill;
                lock (state) acceptor = null;
            }
            // No port can remain owned by this session before another app/service obtains the lease.
            var released = lease; lease = null;
            if (released != null) await Task.Run(() => released.Dispose()).ConfigureAwait(false);
            lock (state)
            {
                active = false; starting?.Dispose(); starting = null;
                fault = fault ?? closeError;
                status = fault == null ? "Accept zaustavljen; NV9 port zatvoren." : "Accept zaustavljen. Greška: " + fault + ". Provjerite novčanicu i uređaj; nema automatskog ponavljanja.";
            }
            Notify();
        }
        private void OnBill(object sender, BillEventArgs e)
        {
            bool mustStop = false;
            lock (state)
            {
                if (!ReferenceEquals(sender, acceptor) || e == null) return;
                if (e.Kind == BillEventKind.Credit || e.Kind == BillEventKind.Stacked)
                {
                    if (string.IsNullOrWhiteSpace(e.EventId) || e.Currency != "BAM" || (e.AmountMinor != 1000 && e.AmountMinor != 2000))
                    { fault = "Nepodržan ili neidentifikovan stvarni novčani događaj: " + e.Kind + ", " + e.AmountMinor + " " + e.Currency; mustStop = true; }
                    else if (accepted.TryGetValue(e.EventId, out int previous))
                    { if (previous != e.AmountMinor) { fault = "Isti NV9 identitet ima različite iznose."; mustStop = true; } }
                    else
                    {
                        try { received = checked(received + e.AmountMinor); accepted.Add(e.EventId, e.AmountMinor); }
                        catch (OverflowException) { fault = "Zbir prihvaćenih novčanica prelazi podržani iznos."; mustStop = true; }
                    }
                    status = fault ?? "Stvarno prihvaćeno: " + e.Kind + ".";
                }
                else if (e.Kind == BillEventKind.Fault || e.Kind == BillEventKind.Disconnected)
                { fault = string.IsNullOrWhiteSpace(e.Message) ? "NV9 " + e.Kind : e.Message; status = fault; mustStop = true; }
                else if (e.Kind == BillEventKind.Rejected && e.EventId != null && accepted.ContainsKey(e.EventId))
                { fault = "REJECTED nakon potvrđenog prihvata; iznos je očuvan, potrebna provjera."; status = fault; mustStop = true; }
                else status = e.Kind == BillEventKind.Reading ? "NV9 čita novčanicu…" : e.Kind == BillEventKind.Escrow ? "Novčanica prolazi; čeka se stvarni Credit/Stacked." : "NV9: " + e.Kind;
            }
            Notify();
            if (mustStop) { var stopping = StopAfterFaultAsync(); }
        }
        private async Task StopAfterFaultAsync()
        { try { await StopAsync().ConfigureAwait(false); } catch { /* CloseDeviceAsync preserves the lease and the actual error for explicit retry. */ } }
        private void Notify()
        {
            var handlers = StateChanged;
            if (handlers != null) foreach (EventHandler handler in handlers.GetInvocationList())
                try { handler(this, EventArgs.Empty); } catch { /* Display subscribers cannot interrupt money accounting. */ }
        }
    }
}
