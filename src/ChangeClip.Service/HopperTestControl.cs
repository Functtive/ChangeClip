using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 8) };
            row.Children.Add(new TextBlock { Text = "Kovanica:", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            CoinCountTextBox = new TextBox { Text = "1", Width = 88, Padding = new Thickness(6), VerticalContentAlignment = VerticalAlignment.Center };
            DispenseButton = new Button { Content = "Dispense", Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(16, 7, 16, 7), Background = new SolidColorBrush(Color.FromRgb(107, 75, 207)), Foreground = Brushes.White };
            row.Children.Add(CoinCountTextBox); row.Children.Add(DispenseButton); panel.Children.Add(row);
            ReadingsTextBox = new TextBox { IsReadOnly = true, AcceptsReturn = true, MinHeight = 110, MaxHeight = 210,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap,
                Background = new SolidColorBrush(Color.FromRgb(16, 22, 43)), Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(107, 75, 207)), Padding = new Thickness(8), Text = "Upišite broj kovanica i izaberite Dispense.\n" };
            panel.Children.Add(ReadingsTextBox); Content = panel;
            Background = new SolidColorBrush(Color.FromRgb(23, 30, 53));
            DispenseButton.Click += async (sender, args) => await DispenseAsync();
        }
        public Button DispenseButton { get; private set; }
        public TextBox CoinCountTextBox { get; private set; }
        public TextBox ReadingsTextBox { get; private set; }
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
                if (closed) return;
                Append("Očitano: " + reading.ConfirmedCoins + " / " + reading.RequestedCoins);
                Append((reading.CountReliable ? "Preostalo: " : "Preostalo prema očitanju: ") + reading.RemainingCoins + ". " + reading.Message);
            };
            if (Dispatcher.CheckAccess()) update(); else Dispatcher.BeginInvoke(update);
        }
        private void Append(string message) { ReadingsTextBox.AppendText(message + Environment.NewLine); ReadingsTextBox.ScrollToEnd(); }
        private void SetActive(bool value)
        {
            IsActive = value; DispenseButton.IsEnabled = !value && !closed; CoinCountTextBox.IsEnabled = !value && !closed;
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
