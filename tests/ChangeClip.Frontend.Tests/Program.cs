using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChangeClip.Contracts;
using ChangeClip.Frontend;

namespace ChangeClip.Frontend.Tests
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Progress("Starting frontend preview harness.");
            var app = new App();
            Progress("Initializing App.xaml resources.");
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Progress("App.xaml initialized.");
            try
            {
                var ready = new ExchangeSnapshot(ExchangeState.Ready, "Ubacite 10 ili 20 KM");
                var readySession = new StubExchangeSession(ready);
                Progress("Creating first offscreen MainWindow.");
                var readyWindow = CreateWindow(readySession, new StubSimulatorControls());
                Progress("First MainWindow created and laid out.");
                try
                {
                    AssertButtons(readyWindow, false, "Ready");
                    var readyViewModel = (ExchangeViewModel)readyWindow.DataContext;
                    Assert(readyViewModel.ReceivedAmount == "0,00 KM", "initial amount uses fixed Bosnian KM formatting");
                    Assert(readyViewModel.SimulatorHint.Contains("Ubacite 10 ili 20 KM"),
                        "simulator explains how to enable exchange");
                }
                finally { DisposeWindow(readyWindow); }

                var session = new StubExchangeSession(ready);
                var simulator = new StubSimulatorControls { HopperCoins = 100 };
                simulator.OnInsert = delegate(int amountMinor)
                {
                    var total = session.Snapshot.ReceivedMinor + amountMinor;
                    session.Publish(new ExchangeSnapshot(ExchangeState.AwaitingPayout,
                        "Primljeno. Pritisnite Razmijeni za isplatu.", total, 0, 0, true, false));
                };
                Progress("Creating simulator wallet preview window.");
                var preview = CreateWindow(session, simulator);
                var previewRoot = GetRoot(preview);
                FindExpander(previewRoot).IsExpanded = true;
                Layout(preview);
                var insert10 = FindButton(previewRoot, "Ubaci 10 KM");
                insert10.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, insert10));
                PublishFromBackgroundAndDrain(session, session.Snapshot, preview);
                Assert(((ExchangeViewModel)preview.DataContext).ReceivedAmount == "10,00 KM",
                    "wallet adds 10 KM after first note");
                var insert20 = FindButton(previewRoot, "Ubaci 20 KM");
                insert20.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, insert20));
                PublishFromBackgroundAndDrain(session, session.Snapshot, preview);
                Assert(simulator.InsertCalls == 2 && simulator.LastInsertedMinor == 2000,
                    "simulator inserts 10 KM and 20 KM notes");

                var viewModel = (ExchangeViewModel)preview.DataContext;
                AssertButtons(preview, true, "AwaitingPayout");
                Assert(viewModel.ReceivedAmount == "30,00 KM", "wallet total is 30,00 KM after 10+20 input");
                Assert(viewModel.Status == "Primljeno. Pritisnite Razmijeni za isplatu.", "awaiting-payout status");
                Assert(viewModel.SimulatorHint.Contains("Možete ubaciti još"), "wallet allows additional notes");
                Assert(FindExpander(previewRoot).Visibility == Visibility.Visible, "simulator visible with controls");
                Progress("30 KM wallet and AwaitingPayout bindings verified.");

                var output = args.Length > 0 ? Path.GetFullPath(args[0]) :
                    Path.Combine(FindProjectRoot(), "artifacts", "ui", "changeclip-preview.png");
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                RenderPreview(preview, output);
                Progress("Offscreen PNG render complete.");
                Console.WriteLine("PASS: AwaitingPayout 30,00 KM preview written to " + output);

                var exchangeButton = FindButton(previewRoot, "Razmijeni");
                exchangeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, exchangeButton));
                Assert(session.ExchangeCalls == 1, "Razmijeni click calls ExchangeAsync once");
                DrainDispatcher();
                Assert(viewModel.ReceivedAmount == "0,00 KM" &&
                    viewModel.Status.IndexOf("Uspješno", StringComparison.OrdinalIgnoreCase) >= 0,
                    "successful full payout resets displayed wallet to zero");
                Assert(viewModel.CanInsertSimulatorBanknote, "simulator input is enabled again in Ready after payout");
                DisposeWindow(preview);

                var closingSession = new StubExchangeSession(new ExchangeSnapshot(ExchangeState.AwaitingPayout,
                    "Primljeno. Pritisnite Razmijeni za isplatu.", 3000, 0, 0, true, false));
                var closingWindow = CreateWindow(closingSession, null);
                closingWindow.Close();
                Assert(closingSession.ReturnCalls == 0, "closing a saved wallet never requests ReturnAsync");

                var debt = new ExchangeSnapshot(ExchangeState.Debt,
                    "Dužni smo vam još 5 KM", 2000, 1500, 500, false, false, true);
                var debtSession = new StubExchangeSession(debt);
                var debtWindow = CreateWindow(debtSession, new StubSimulatorControls());
                try
                {
                    AssertButtons(debtWindow, false, "Debt");
                    var debtViewModel = (ExchangeViewModel)debtWindow.DataContext;
                    Assert(debtViewModel.Status.StartsWith("Dužni smo vam još ", StringComparison.Ordinal) &&
                        debtViewModel.Status.EndsWith(" KM", StringComparison.Ordinal), "debt status uses KM formatting");
                    Assert(debtViewModel.PaidAmount.EndsWith(" KM", StringComparison.Ordinal), "paid amount uses KM formatting");
                }
                finally { DisposeWindow(debtWindow); }

                var uncertain = new ExchangeSnapshot(ExchangeState.ReconciliationRequired,
                    "Nepoznata isplata — potrebna provjera", 1000, 0, 1000, false, false, false);
                var uncertainSession = new StubExchangeSession(uncertain);
                var uncertainWindow = CreateWindow(uncertainSession, null);
                try
                {
                    var uncertainViewModel = (ExchangeViewModel)uncertainWindow.DataContext;
                    Assert(uncertainViewModel.OwedAmount == "Provjera",
                        "unknown payout does not present an exact mathematical remainder");
                }
                finally { DisposeWindow(uncertainWindow); }

                var noSimulatorSession = new StubExchangeSession(ready);
                var noSimulatorWindow = CreateWindow(noSimulatorSession, null);
                try
                {
                    Assert(FindExpander(GetRoot(noSimulatorWindow)).Visibility == Visibility.Collapsed,
                        "simulator panel hidden without simulator controls");
                }
                finally { DisposeWindow(noSimulatorWindow); }

                Console.WriteLine("PASS: Ready/AwaitingPayout/Debt buttons and simulator visibility");
                Console.WriteLine("PASS: wallet accumulation, one ExchangeAsync call, and zero reset");
                Console.WriteLine("PASS: closing a saved wallet does not call ReturnAsync");
                Console.WriteLine("PASS: unknown payout amount is presented for reconciliation");
                Progress("All frontend scenarios complete.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: " + ex);
                return 1;
            }
            finally
            {
                Progress("Shutting down WPF Application.");
                app.Shutdown();
                Progress("WPF Application shutdown complete.");
            }
        }

        private static MainWindow CreateWindow(IExchangeSession session, ISimulatorControls simulator)
        {
            var window = new MainWindow(session, simulator);
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            Layout(window);
            return window;
        }

        private static void DisposeWindow(MainWindow window)
        {
            var disposable = window.DataContext as IDisposable;
            if (disposable != null) disposable.Dispose();
        }

        private static void AssertButtons(MainWindow window, bool canExchange, string state)
        {
            var root = GetRoot(window);
            Assert(FindButton(root, "Razmijeni").IsEnabled == canExchange, state + " Razmijeni enabled state");
            Assert(FindButtonOrNull(root, "Vrati") == null, state + " has no public Vrati action");
        }

        private static void PublishFromBackgroundAndDrain(StubExchangeSession session,
            ExchangeSnapshot snapshot, MainWindow window)
        {
            using (var published = new ManualResetEvent(false))
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    session.Publish(snapshot);
                    published.Set();
                });
                Assert(published.WaitOne(TimeSpan.FromSeconds(5)), "background Changed event completes");
            }

            DrainDispatcher();
            Layout(window);
        }

        private static void DrainDispatcher()
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            var drained = false;
            var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            timeout.Tick += delegate
            {
                timeout.Stop();
                frame.Continue = false;
            };
            dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate
            {
                drained = true;
                timeout.Stop();
                frame.Continue = false;
            }));
            timeout.Start();
            Dispatcher.PushFrame(frame);
            timeout.Stop();
            if (!drained) throw new TimeoutException("Dispatcher did not drain the Changed update within five seconds.");
        }

        private static void RenderPreview(MainWindow window, string output)
        {
            var root = GetRoot(window);
            FindExpander(root).IsExpanded = true;
            Layout(window);
            var statusPanel = FindNamedBorder(root, "StatusPanel");
            Assert(statusPanel.ActualHeight + 0.5 >= statusPanel.DesiredSize.Height,
                "expanded preview status panel has enough height for its content");
            const int width = 760;
            const int height = 720;
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(output)) encoder.Save(stream);
        }

        private static void Layout(MainWindow window)
        {
            var root = GetRoot(window);
            root.DataContext = window.DataContext;
            root.ApplyTemplate();
            root.Measure(new Size(760, 720));
            root.Arrange(new Rect(0, 0, 760, 720));
            root.UpdateLayout();
        }

        private static FrameworkElement GetRoot(MainWindow window)
        {
            var root = window.Content as FrameworkElement;
            if (root == null) throw new InvalidOperationException("MainWindow XAML content is missing.");
            return root;
        }

        private static Button FindButton(DependencyObject parent, string content)
        {
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var button = child as Button;
                if (button != null && String.Equals(Convert.ToString(button.Content), content, StringComparison.Ordinal))
                    return button;
                var nested = FindButtonOrNull(child, content);
                if (nested != null) return nested;
            }
            throw new InvalidOperationException("Button not found: " + content);
        }

        private static Button FindButtonOrNull(DependencyObject parent, string content)
        {
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var button = child as Button;
                if (button != null && String.Equals(Convert.ToString(button.Content), content, StringComparison.Ordinal))
                    return button;
                var nested = FindButtonOrNull(child, content);
                if (nested != null) return nested;
            }
            return null;
        }

        private static Expander FindExpander(DependencyObject parent)
        {
            var expander = FindExpanderOrNull(parent);
            if (expander == null) throw new InvalidOperationException("Simulator Expander not found.");
            return expander;
        }

        private static Expander FindExpanderOrNull(DependencyObject parent)
        {
            var expander = parent as Expander;
            if (expander != null) return expander;
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var found = FindExpanderOrNull(VisualTreeHelper.GetChild(parent, i));
                if (found != null) return found;
            }
            return null;
        }

        private static Border FindNamedBorder(DependencyObject parent, string name)
        {
            var border = parent as Border;
            var frameworkElement = parent as FrameworkElement;
            if (border != null && frameworkElement != null &&
                String.Equals(frameworkElement.Name, name, StringComparison.Ordinal)) return border;
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var found = FindNamedBorderOrNull(VisualTreeHelper.GetChild(parent, i), name);
                if (found != null) return found;
            }
            throw new InvalidOperationException("Named Border not found: " + name);
        }

        private static Border FindNamedBorderOrNull(DependencyObject parent, string name)
        {
            var border = parent as Border;
            var frameworkElement = parent as FrameworkElement;
            if (border != null && frameworkElement != null &&
                String.Equals(frameworkElement.Name, name, StringComparison.Ordinal)) return border;
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var found = FindNamedBorderOrNull(VisualTreeHelper.GetChild(parent, i), name);
                if (found != null) return found;
            }
            return null;
        }

        private static string FindProjectRoot()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "README.md")) &&
                    Directory.Exists(Path.Combine(directory.FullName, "src")) &&
                    Directory.Exists(Path.Combine(directory.FullName, "tests")))
                    return directory.FullName;
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException("Could not find the ChangeClip project root.");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
        }

        private static void Progress(string message)
        {
            Console.WriteLine("[frontend-preview] " + message);
            Console.Out.Flush();
        }
    }

    internal sealed class StubExchangeSession : IExchangeSession, IDisposable
    {
        private readonly object _sync = new object();
        private ExchangeSnapshot _snapshot;

        public StubExchangeSession(ExchangeSnapshot snapshot) { _snapshot = snapshot; }
        public event EventHandler<ExchangeSnapshot> Changed;
        public ExchangeSnapshot Snapshot { get { lock (_sync) return _snapshot; } }
        public Task StartAsync(CancellationToken cancellationToken) { return Task.CompletedTask; }
        public int ExchangeCalls { get; private set; }
        public int ReturnCalls { get; private set; }
        public Task ExchangeAsync(CancellationToken cancellationToken)
        {
            ExchangeCalls++;
            var amount = Snapshot.ReceivedMinor;
            Publish(new ExchangeSnapshot(ExchangeState.Ready,
                "Uspješno. Ubacite 10 ili 20 KM.", 0, amount, 0, true, false));
            return Task.CompletedTask;
        }
        public Task ReturnAsync(CancellationToken cancellationToken) { ReturnCalls++; return Task.CompletedTask; }

        public void Publish(ExchangeSnapshot snapshot)
        {
            lock (_sync)
            {
                _snapshot = snapshot;
                var handler = Changed;
                if (handler != null) handler(this, snapshot);
            }
        }

        public void Dispose() { }
    }

    internal sealed class StubSimulatorControls : ISimulatorControls
    {
        public int HopperCoins { get; set; }
        public int InsertCalls { get; private set; }
        public int LastInsertedMinor { get; private set; }
        public Action<int> OnInsert { get; set; }
        public void InsertBanknote(int amountMinor)
        {
            InsertCalls++;
            LastInsertedMinor = amountMinor;
            if (OnInsert != null) OnInsert(amountMinor);
        }
        public void SetHopperCoins(int coins) { HopperCoins = coins; }
    }
}
