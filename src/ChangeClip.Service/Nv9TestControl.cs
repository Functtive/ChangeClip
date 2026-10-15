using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChangeClip.Contracts;

namespace ChangeClip.Service
{
    public sealed class Nv9TestControl : UserControl
    {
        private readonly Func<DeviceOptions> getOptions;
        private readonly Nv9TestSession session;
        private volatile bool working;
        private string uiError;
        public Nv9TestControl(Func<DeviceOptions> getOptions, Func<DeviceOptions, IBillAcceptor> factory = null)
        {
            this.getOptions = getOptions ?? throw new ArgumentNullException(nameof(getOptions));
            session = new Nv9TestSession(factory);
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = "NV9 · stvarni prihvat", Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.SemiBold });
            TotalText = new TextBlock { Foreground = Brushes.White, FontSize = 28, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 8) };
            panel.Children.Add(TotalText);
            AcceptButton = new Button { Content = "Accept", Background = Brush("#7658E8"), Foreground = Brushes.White, Padding = new Thickness(18, 10, 18, 10), HorizontalAlignment = HorizontalAlignment.Left };
            AcceptButton.Click += Accept_Click; panel.Children.Add(AcceptButton);
            StatusText = new TextBlock { Foreground = Brush("#D6DAEF"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
            panel.Children.Add(StatusText);
            Content = new Border { Background = Brush("#151D38"), CornerRadius = new CornerRadius(12), BorderBrush = Brush("#50406F"), BorderThickness = new Thickness(1), Child = panel };
            session.StateChanged += (sender, args) => DispatchRefresh(); Refresh();
        }
        public Button AcceptButton { get; private set; }
        public TextBlock TotalText { get; private set; }
        public TextBlock StatusText { get; private set; }
        public bool IsActive => working || session.IsActive;
        public event EventHandler StateChanged;
        private static Brush Brush(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);
        private async void Accept_Click(object sender, RoutedEventArgs args)
        { await AcceptAsync(); }
        public async Task AcceptAsync()
        {
            bool ownsTransition = false, stopping = false;
            DeviceOptions options = null;
            try
            {
                await OnUiAsync(() =>
                {
                    if (working) return;
                    working = true; ownsTransition = true; uiError = null;
                    stopping = session.IsActive; Refresh();
                    if (!stopping) options = getOptions();
                }).ConfigureAwait(false);
                if (!ownsTransition) return;
                if (stopping) await session.StopAsync().ConfigureAwait(false);
                else await session.StartAsync(options).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (ownsTransition) await OnUiAsync(() => uiError = error.Message).ConfigureAwait(false);
            }
            finally
            {
                if (ownsTransition) await OnUiAsync(() => { working = false; Refresh(); }).ConfigureAwait(false);
            }
        }
        public async Task StopAsync() { await session.StopAsync().ConfigureAwait(false); DispatchRefresh(); }
        public async Task ShutdownAsync() { await session.ShutdownAsync().ConfigureAwait(false); DispatchRefresh(); }
        private Task OnUiAsync(Action action)
        {
            if (Dispatcher.CheckAccess()) { action(); return Task.CompletedTask; }
            return Dispatcher.InvokeAsync(action).Task;
        }
        private void DispatchRefresh()
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            if (Dispatcher.CheckAccess()) Refresh(); else Dispatcher.BeginInvoke(new Action(Refresh));
        }
        private void Refresh()
        {
            TotalText.Text = (session.ReceivedMinor / 100m).ToString("N2", CultureInfo.GetCultureInfo("bs-Latn-BA")) + " KM";
            StatusText.Text = uiError ?? session.Status;
            AcceptButton.Content = session.IsActive ? "Zaustavi Accept" : "Accept";
            AcceptButton.IsEnabled = !working;
            var handlers = StateChanged;
            if (handlers != null) foreach (EventHandler handler in handlers.GetInvocationList())
                try { handler(this, EventArgs.Empty); } catch { }
        }
    }
}
