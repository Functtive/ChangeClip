using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ChangeClip.Contracts;

namespace ChangeClip.Service
{
    public sealed class HopperTestControl : UserControl
    {
        private readonly Func<DeviceOptions> getOptions;
        private readonly Func<Task> beforeDispense;
        private readonly Func<DeviceOptions, ICoinHopper> factory;
        private HopperTestSession session;
        private Task running = Task.CompletedTask;
        private bool closed;
        public HopperTestControl(Func<DeviceOptions> getOptions, Func<Task> beforeDispense = null, Func<DeviceOptions, ICoinHopper> factory = null)
        {
            this.getOptions = getOptions ?? throw new ArgumentNullException(nameof(getOptions));
            this.beforeDispense = beforeDispense; this.factory = factory;
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock { Text = "Test hoppera", Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeights.SemiBold });
            var rows = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(94) });
            rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
            rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rows.Children.Add(new TextBlock { Text = "Kovanica", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
            CoinCountTextBox = CountBox(false);
            CoinCountTextBox.Text = "1";
            Grid.SetColumn(CoinCountTextBox, 1); rows.Children.Add(CoinCountTextBox);
            DispenseButton = new Button { Content = "Dispense", Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(10, 7, 10, 7), Background = new SolidColorBrush(Color.FromRgb(107, 75, 207)), Foreground = Brushes.White };
            Grid.SetColumn(DispenseButton, 2); rows.Children.Add(DispenseButton);
            var activity = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
            DispensingIndicator = new Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 0, 7, 0) };
            DispensingLabel = new TextBlock { Text = "Dispensing", VerticalAlignment = VerticalAlignment.Center };
            activity.Children.Add(DispensingIndicator); activity.Children.Add(DispensingLabel);
            Grid.SetRow(activity, 1); rows.Children.Add(activity);
            DispensedCountTextBox = CountBox(true);
            DispensedCountTextBox.Text = "0";
            DispensedCountTextBox.Margin = new Thickness(0, 12, 0, 0);
            Grid.SetRow(DispensedCountTextBox, 1); Grid.SetColumn(DispensedCountTextBox, 1);
            rows.Children.Add(DispensedCountTextBox); panel.Children.Add(rows);
            StatusText = new TextBlock { Text = "Upišite broj kovanica i izaberite Dispense.", Foreground = Brushes.LightSteelBlue,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
            panel.Children.Add(StatusText); Content = panel;
            SetDispensing(false);
            Background = new SolidColorBrush(Color.FromRgb(23, 30, 53));
            DispenseButton.Click += async (sender, args) => await DispenseAsync();
        }
        public Button DispenseButton { get; private set; }
        public TextBox CoinCountTextBox { get; private set; }
        public TextBox DispensedCountTextBox { get; private set; }
        public Ellipse DispensingIndicator { get; private set; }
        public TextBlock DispensingLabel { get; private set; }
        public TextBlock StatusText { get; private set; }
        public bool IsDispensing { get; private set; }
        public bool IsActive { get; private set; }
        public event EventHandler StateChanged;
        public Task DispenseAsync()
        {
            if (!Dispatcher.CheckAccess()) return Dispatcher.InvokeAsync(() => DispenseAsync()).Task.Unwrap();
            if (closed || IsActive) return running;
            int count;
            if (!int.TryParse(CoinCountTextBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 1 || count > 10000)
            { Append("Upišite cijeli broj kovanica od 1 do 10000."); return Task.CompletedTask; }
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            running = completion.Task;
            DispensedCountTextBox.Text = "0";
            SetDispensing(false);
            Append("Priprema isplate…");
            SetActive(true); // Before NV9 stop: the host must block competing device actions immediately.
            ObserveRun(RunAsync(count, completion));
            return running;
        }
        private async Task RunAsync(int count, TaskCompletionSource<bool> completion)
        {
            HopperTestSession current = null;
            try
            {
                if (beforeDispense != null) await beforeDispense().ConfigureAwait(false);
                var options = await OnUI(() => closed ? null : getOptions()).ConfigureAwait(false);
                if (options == null) return;
                current = new HopperTestSession(options, factory);
                var attached = await OnUI(() =>
                {
                    if (closed) return false;
                    session = current; current.Changed += ReadingChanged; return true;
                }).ConfigureAwait(false);
                if (!attached) return;
                await current.DispenseAsync(count).ConfigureAwait(false);
            }
            catch (Exception error) { await OnUI(() => { if (!closed) Append("Test hoppera: " + error.Message); }).ConfigureAwait(false); }
            finally
            {
                bool retained = false;
                try
                {
                    if (current != null) await current.ShutdownAsync().ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    retained = true;
                    await OnUI(() => Append("Port nije zatvoren. Ponovite zatvaranje prozora. " + error.Message)).ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        await OnUI(() =>
                        {
                            if (!retained)
                            {
                                if (current != null) current.Changed -= ReadingChanged;
                                if (ReferenceEquals(session, current)) session = null;
                            }
                            SetActive(retained);
                        }).ConfigureAwait(false);
                    }
                    finally { completion.TrySetResult(true); }
                }
            }
        }
        private static async void ObserveRun(Task task)
        { try { await task.ConfigureAwait(false); } catch { /* A shutting-down Dispatcher cannot strand the already-completed operation task. */ } }
        private Task OnUI(Action action)
        {
            if (Dispatcher.CheckAccess()) { action(); return Task.CompletedTask; }
            return Dispatcher.InvokeAsync(action).Task;
        }
        private Task<T> OnUI<T>(Func<T> action)
        {
            if (Dispatcher.CheckAccess()) return Task.FromResult(action());
            return Dispatcher.InvokeAsync(action).Task;
        }
        private void ReadingChanged(object sender, HopperTestReading reading)
        {
            Action update = () =>
            {
                DispensedCountTextBox.Text = reading.ConfirmedCoins.ToString(CultureInfo.InvariantCulture);
                SetDispensing(reading.IsDispensing);
                Append((reading.CountReliable ? "Preostalo: " : "Preostalo prema očitanju: ") + reading.RemainingCoins + ". " + reading.Message);
            };
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            if (Dispatcher.CheckAccess()) update(); else Dispatcher.BeginInvoke(update);
        }
        private void Append(string message) { StatusText.Text = message; }
        private static TextBox CountBox(bool readOnly)
        {
            return new TextBox { IsReadOnly = readOnly, Width = 88, MinHeight = 38, FontSize = 20,
                Padding = new Thickness(6), TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                Background = readOnly ? new SolidColorBrush(Color.FromRgb(16, 22, 43)) : Brushes.White,
                Foreground = readOnly ? Brushes.White : new SolidColorBrush(Color.FromRgb(16, 22, 43)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(107, 75, 207)) };
        }
        private void SetDispensing(bool value)
        {
            IsDispensing = value;
            var color = new SolidColorBrush(value ? Color.FromRgb(60, 211, 139) : Color.FromRgb(139, 151, 178));
            DispensingIndicator.Fill = color;
            DispensingLabel.Foreground = color;
        }
        private void SetActive(bool value)
        {
            IsActive = value; DispenseButton.IsEnabled = !value && !closed; CoinCountTextBox.IsEnabled = !value && !closed;
            if (!value) SetDispensing(false);
            var handlers = StateChanged;
            if (handlers != null) foreach (EventHandler handler in handlers.GetInvocationList())
                try { handler(this, EventArgs.Empty); } catch { /* Host observers cannot strand an active payout. */ }
        }
        public async Task ShutdownAsync()
        {
            HopperTestSession current = null; Task pending = null;
            await OnUI(() =>
            {
                closed = true; DispenseButton.IsEnabled = false; CoinCountTextBox.IsEnabled = false;
                current = session; pending = running;
            }).ConfigureAwait(false);
            try
            {
                if (current != null) await current.ShutdownAsync().ConfigureAwait(false);
                await pending.ConfigureAwait(false);
                await OnUI(() =>
                {
                    if (current != null && !current.IsActive)
                    { current.Changed -= ReadingChanged; if (ReferenceEquals(session, current)) session = null; }
                    SetActive(session != null && session.IsActive);
                }).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                await OnUI(() => { Append("Zatvaranje hoppera nije završeno: " + error.Message); SetActive(true); }).ConfigureAwait(false);
                throw;
            }
        }
    }
}
