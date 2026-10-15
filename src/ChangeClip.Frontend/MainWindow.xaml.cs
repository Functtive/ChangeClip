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
            InitializeComponent();
            _viewModel = new ExchangeViewModel(session, simulator, Dispatcher);
            DataContext = _viewModel;
            if (simulator != null) CoinStockBox.Text = simulator.HopperCoins.ToString();
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

        private void Insert10_Click(object sender, RoutedEventArgs e) { _viewModel.InsertBanknote(1000); }
        private void Insert20_Click(object sender, RoutedEventArgs e) { _viewModel.InsertBanknote(2000); }

        private void ApplyCoins_Click(object sender, RoutedEventArgs e)
        {
            int coins;
            if (!int.TryParse(CoinStockBox.Text, out coins) || coins < 0)
            {
                MessageBox.Show(this, "Unesite cijeli broj kovanica koji nije manji od nule.",
                    "Simulator", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _viewModel.SetHopperCoins(coins);
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_viewModel.IsPayoutInProgress) return;
            e.Cancel = true;
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
