using System;
using System.Threading.Tasks;
using System.Windows;
using ChangeClip.Contracts;

namespace ChangeClip.Frontend
{
    public partial class MainWindow : Window
    {
        private readonly ExchangeViewModel _viewModel;
        private bool _started;
        private bool _disposed;

        public MainWindow(IExchangeSession session, ISimulatorControls simulator = null)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
#if HARDWARE_RELEASE
            if (simulator != null) throw new ArgumentException("Hardware release cannot use simulator controls.", nameof(simulator));
#endif
            InitializeComponent();
            _viewModel = new ExchangeViewModel(session, simulator, Dispatcher);
            DataContext = _viewModel;
#if !HARDWARE_RELEASE
            if (simulator != null) CoinStockBox.Text = simulator.HopperCoins.ToString();
#endif
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (_started) return;
            _started = true;
            await _viewModel.StartAsync();
        }

        private async void Exchange_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.ExchangeAsync();
        }

#if !HARDWARE_RELEASE
        private void Insert10_Click(object sender, RoutedEventArgs e) { _viewModel.InsertBanknote(1000); }
        private void Insert20_Click(object sender, RoutedEventArgs e) { _viewModel.InsertBanknote(2000); }

        private async void ApplyCoins_Click(object sender, RoutedEventArgs e)
        {
            int coins;
            if (!int.TryParse(CoinStockBox.Text, out coins) || coins < 0)
            {
                MessageBox.Show(this, "Unesite cijeli broj kovanica koji nije manji od nule.",
                    "Simulator", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            await _viewModel.SetHopperCoinsAsync(coins);
        }
#endif

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_viewModel.IsPayoutInProgress) return;
            e.Cancel = true;
            if (IsVisible)
                MessageBox.Show(this, "Prihvat novčanice ili isplata je u toku. Sačekajte prije zatvaranja.",
                    "ChangeClip", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            if (_disposed) return;
            _disposed = true;
            _viewModel.Dispose();
        }
    }
}
