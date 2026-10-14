using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ChangeClip.Contracts;

namespace ChangeClip.Frontend
{
    public sealed class ExchangeViewModel : INotifyPropertyChanged, IDisposable
    {
        private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("bs-Latn-BA");
        private readonly IExchangeSession _session;
        private readonly ISimulatorControls _simulator;
        private readonly Dispatcher _dispatcher;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private ExchangeSnapshot _snapshot;
        private bool _isBusy;
        private string _error = "";
        private bool _disposed;

        public ExchangeViewModel(IExchangeSession session, ISimulatorControls simulator, Dispatcher dispatcher)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _simulator = simulator;
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _snapshot = session.Snapshot ?? new ExchangeSnapshot(ExchangeState.Disconnected, "Povezivanje...");
            _session.Changed += Session_Changed;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public string ReceivedAmount { get { return Format(_snapshot.ReceivedMinor); } }
        public string PaidAmount { get { return Format(_snapshot.PaidMinor); } }
        public string OwedAmount { get { return _snapshot.IsDebtExact ? Format(_snapshot.OwedMinor) : "Provjera"; } }
        public string Error { get { return _error; } }
        public string SimulatorHint
        {
            get
            {
                return _simulator != null && _snapshot.State == ExchangeState.Ready
                    ? "Simulator: ubacite 10 ili 20 KM prije klika na Razmijeni."
                    : "";
            }
        }
        public Visibility SimulatorHintVisibility
        {
            get { return String.IsNullOrEmpty(SimulatorHint) ? Visibility.Collapsed : Visibility.Visible; }
        }
        public Visibility ErrorVisibility
        {
            get { return String.IsNullOrEmpty(_error) ? Visibility.Collapsed : Visibility.Visible; }
        }
        public bool CanExchange { get { return !_isBusy && _snapshot.CanExchange; } }
        public bool CanReturn { get { return !_isBusy && _snapshot.CanReturn; } }
        public bool IsPayoutInProgress
        {
            get
            {
                var current = _session.Snapshot ?? _snapshot;
                return current.State == ExchangeState.Accepting || current.State == ExchangeState.PayingOut;
            }
        }
        public bool IsAwaitingChoice
        {
            get { return (_session.Snapshot ?? _snapshot).State == ExchangeState.AwaitingChoice; }
        }
        public bool IsConfirmedReturned
        {
            get { return (_session.Snapshot ?? _snapshot).State == ExchangeState.Returned; }
        }
        public Visibility SimulatorVisibility { get { return _simulator == null ? Visibility.Collapsed : Visibility.Visible; } }

        public string Status
        {
            get
            {
                if (_snapshot.State == ExchangeState.Ready || _snapshot.State == ExchangeState.Initializing ||
                    _snapshot.State == ExchangeState.Disconnected)
                    return "Ubacite 10 ili 20 KM";
                if (_snapshot.State == ExchangeState.AwaitingChoice || _snapshot.State == ExchangeState.Accepting)
                    return "Želite li razmijeniti novac?";
                if (_snapshot.State == ExchangeState.PayingOut) return "Isplata u toku";
                if (_snapshot.State == ExchangeState.Succeeded) return "Uspješno";
                if (_snapshot.State == ExchangeState.Debt)
                    return _snapshot.IsDebtExact
                        ? "Dužni smo vam još " + Format(_snapshot.OwedMinor)
                        : "Nepoznata isplata — potrebna provjera";
                if (_snapshot.State == ExchangeState.ReconciliationRequired || !_snapshot.IsDebtExact)
                    return "Nepoznata isplata — potrebna provjera";
                if (_snapshot.State == ExchangeState.Returned) return "Novčanica je vraćena.";
                return String.IsNullOrWhiteSpace(_snapshot.Message) ? "Uređaj nije spreman." : _snapshot.Message;
            }
        }

        public async Task StartAsync()
        {
            await RunAsync(delegate { return _session.StartAsync(_cancellation.Token); });
        }

        public async Task ExchangeAsync()
        {
            if (!CanExchange) return;
            await RunAsync(delegate { return _session.ExchangeAsync(_cancellation.Token); });
        }

        public async Task ReturnAsync()
        {
            if (!CanReturn) return;
            await RunAsync(delegate { return _session.ReturnAsync(_cancellation.Token); });
        }

        public void InsertBanknote(int amountMinor)
        {
            if (_simulator == null) return;
            try { _simulator.InsertBanknote(amountMinor); }
            catch (Exception ex) { SetError(ex.Message); }
        }

        public void SetHopperCoins(int coins)
        {
            if (_simulator == null) return;
            try { _simulator.SetHopperCoins(coins); }
            catch (Exception ex) { SetError(ex.Message); }
        }

        private async Task RunAsync(Func<Task> operation)
        {
            if (_isBusy) return;
            _isBusy = true;
            _error = "";
            NotifyAll();
            try { await operation(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { SetError(ex.Message); }
            finally
            {
                _isBusy = false;
                NotifyAll();
            }
        }

        private void Session_Changed(object sender, ExchangeSnapshot snapshot)
        {
            if (snapshot == null) return;
            _dispatcher.BeginInvoke(DispatcherPriority.DataBind,
                new Action(delegate { ApplySnapshot(snapshot); }));
        }

        private void ApplySnapshot(ExchangeSnapshot snapshot)
        {
            if (_disposed) return;
            _snapshot = snapshot;
            NotifyAll();
        }

        private void SetError(string message)
        {
            _error = String.IsNullOrWhiteSpace(message) ? "Došlo je do greške." : message;
            OnPropertyChanged("Error");
            OnPropertyChanged("ErrorVisibility");
            OnPropertyChanged("SimulatorHint");
            OnPropertyChanged("SimulatorHintVisibility");
            OnPropertyChanged("ErrorVisibility");
        }

        private static string Format(int minor)
        {
            return (minor / 100m).ToString("N2", DisplayCulture) + " KM";
        }

        private void NotifyAll()
        {
            OnPropertyChanged("ReceivedAmount");
            OnPropertyChanged("PaidAmount");
            OnPropertyChanged("OwedAmount");
            OnPropertyChanged("Status");
            OnPropertyChanged("CanExchange");
            OnPropertyChanged("CanReturn");
            OnPropertyChanged("IsPayoutInProgress");
            OnPropertyChanged("IsAwaitingChoice");
            OnPropertyChanged("IsConfirmedReturned");
            OnPropertyChanged("Error");
        }

        private void OnPropertyChanged(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cancellation.Cancel();
            _session.Changed -= Session_Changed;
            _session.Dispose();
            _cancellation.Dispose();
        }
    }
}
