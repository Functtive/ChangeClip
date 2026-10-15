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
                AutomaticConnectionScenarios.RunAsync().GetAwaiter().GetResult();
#if HARDWARE_RELEASE
                return RunHardwareScenarios(args);
#else
                var ready = new ExchangeSnapshot(ExchangeState.Ready, "Ubacite 10 ili 20 KM", canAcceptBanknotes: true);
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
                        "Primljeno. Pritisnite Razmijeni za isplatu.", total, 0, 0, true, false, true, null, true));
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

                var pausedWallet = new StubExchangeSession(new ExchangeSnapshot(ExchangeState.AwaitingPayout,
                    "Primljeno", 1000, 0, 0, true, false, true, null, false));
                var pausedViewModel = new ExchangeViewModel(pausedWallet, new StubSimulatorControls(), Dispatcher.CurrentDispatcher);
                Assert(pausedViewModel.SimulatorHint.IndexOf("Možete ubaciti", StringComparison.OrdinalIgnoreCase) < 0,
                    "AwaitingPayout does not invite more notes when readiness is false");
                pausedViewModel.Dispose();

                var replenishSession = new StubExchangeSession(new ExchangeSnapshot(ExchangeState.Fault, "Nema kovanica."));
                var replenishSimulator = new StubSimulatorControls();
                var replenishViewModel = new ExchangeViewModel(replenishSession, replenishSimulator, Dispatcher.CurrentDispatcher);
                try
                {
                    Assert(replenishViewModel.CanAdjustSimulatorStock, "stock replenishment is available from a fault/empty state");
                    replenishViewModel.SetHopperCoinsAsync(75).GetAwaiter().GetResult();
                    Assert(replenishSimulator.HopperCoins == 75 && replenishSession.RefreshCalls == 1,
                        "stock change refreshes device readiness");
                }
                finally { replenishViewModel.Dispose(); }

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
#endif
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

#if HARDWARE_RELEASE
        private static int RunHardwareScenarios(string[] args)
        {
            AssertHardwareMarkupHasNoSimulatorControls();
            var rejectedSimulator = false;
            try { new MainWindow(new StubExchangeSession(new ExchangeSnapshot(ExchangeState.Disconnected, "")),
                new StubSimulatorControls()); }
            catch (ArgumentException) { rejectedSimulator = true; }
            Assert(rejectedSimulator, "hardware constructor rejects simulator controls");
            var disconnected = new StubExchangeSession(new ExchangeSnapshot(ExchangeState.Disconnected, ""));
            var window = CreateWindow(disconnected, null);
            try
            {
                Progress("Checking hardware connection states.");
                Assert(StatusText(window) == "Uređaji nisu povezani.", "Disconnected status is customer-facing");
                disconnected.Publish(new ExchangeSnapshot(ExchangeState.Initializing, ""));
                DrainDispatcher();
                Assert(StatusText(window) == "Povezivanje uređaja...", "Initializing status is customer-facing");

                disconnected.Publish(new ExchangeSnapshot(ExchangeState.Ready, "", canAcceptBanknotes: true));
                DrainDispatcher();
                Assert(StatusText(window) == "Ubacite 10 ili 20 KM",
                    "Ready status allows insertion only when snapshot says so: " + StatusText(window));
                Assert(FindButton(GetRoot(window), "Razmijeni").IsEnabled == false,
                    "exchange button remains disabled until a note is credited");

                disconnected.Publish(new ExchangeSnapshot(ExchangeState.AwaitingPayout,
                    "Primljeno. Pritisnite Razmijeni za isplatu.", 1000, 0, 0, true, false, true, null, true));
                DrainDispatcher();
                Assert(FindButton(GetRoot(window), "Razmijeni").IsEnabled, "one exchange button enables after credit");
                disconnected.ExchangeAction = delegate
                {
                    disconnected.Publish(new ExchangeSnapshot(ExchangeState.PayingOut, "Isplata u toku", 1000));
                };
                FindButton(GetRoot(window), "Razmijeni").RaiseEvent(new RoutedEventArgs(Button.ClickEvent,
                    FindButton(GetRoot(window), "Razmijeni")));
                DrainDispatcher();
                Assert(disconnected.ExchangeCalls == 1, "one click calls ExchangeAsync once");
                Assert(disconnected.PayingOutObserved, "payout transition is published");
                Assert(StatusText(window) == "Isplata u toku", "PayingOut status is customer-facing");
                disconnected.Publish(new ExchangeSnapshot(ExchangeState.Ready,
                    "Uspješno. Ubacite 10 ili 20 KM.", 0, 1000, 0, false, false, true, null, true));
                DrainDispatcher();
                Assert(((ExchangeViewModel)window.DataContext).ReceivedAmount == "0,00 KM",
                    "successful payout resets received amount to zero");
                Assert(StatusText(window).StartsWith("Uspješno", StringComparison.Ordinal),
                    "successful payout returns to ready status");

                disconnected.Publish(new ExchangeSnapshot(ExchangeState.Ready,
                    "Uspješno. Ubacite 10 ili 20 KM.", 0, 1000, 0, false, false, true, null, false));
                DrainDispatcher();
                Assert(StatusText(window).StartsWith("Uspješno", StringComparison.Ordinal) &&
                    StatusText(window).IndexOf("Ubacite", StringComparison.OrdinalIgnoreCase) < 0,
                    "successful payout does not prompt insertion when readiness is false");

                disconnected.Publish(new ExchangeSnapshot(ExchangeState.Ready, "Zaliha kovanica je prazna.",
                    canAcceptBanknotes: false));
                DrainDispatcher();
                Assert(StatusText(window) == "Zaliha kovanica je prazna.", "stock-paused prompt is shown");
                disconnected.Publish(new ExchangeSnapshot(ExchangeState.Fault, "Nepoznat status uređaja."));
                DrainDispatcher();
                Assert(StatusText(window) == "Nepoznat status uređaja.", "unknown device status is shown");

                var claims = new[]
                {
                    new UnresolvedClaim("one", "Debt", 1000, 500, true),
                    new UnresolvedClaim("two", "ReconciliationRequired", 2000, 0, false)
                };
                disconnected.Publish(new ExchangeSnapshot(ExchangeState.ReconciliationRequired,
                    "Provjera", 2000, 0, 2000, false, false, false, null, false, claims));
                DrainDispatcher();
                Assert(StatusText(window).Contains("2") && StatusText(window).Contains("servisna provjera"),
                    "multiple unresolved exchanges show a count and service-required status: " + StatusText(window));
                var vm = (ExchangeViewModel)window.DataContext;
                Assert(vm.ReceivedAmount == "Provjera" && vm.PaidAmount == "Provjera" && vm.OwedAmount == "Provjera",
                    "multiple unresolved exchanges do not present one claim as a combined total");
            }
            finally { DisposeWindow(window); }

            Progress("Checking close protection and minimum-size fault layout.");
            var fundedSession = new StubExchangeSession(new ExchangeSnapshot(ExchangeState.AwaitingPayout,
                "Primljeno", 2000, 0, 0, true));
            var funded = CreateWindow(fundedSession, null);
            try
            {
                var closing = InvokeClosing(funded);
                Assert(!closing.Cancel, "closing a funded customer exchange is allowed");
                Assert(fundedSession.ReturnCalls == 0,
                    "closing a funded exchange never calls ReturnAsync");
            }
            finally { DisposeWindow(funded); }

            var busy = CreateWindow(new StubExchangeSession(new ExchangeSnapshot(ExchangeState.PayingOut, "Isplata")), null);
            try
            {
                busy.Visibility = Visibility.Hidden;
                Assert(InvokeClosing(busy).Cancel, "closing is blocked while payout is busy");
            }
            finally { DisposeWindow(busy); }

            var longFault = new StubExchangeSession(new ExchangeSnapshot(ExchangeState.Fault,
                "Konfiguracija uređaja nije potpuna. Provjerite serijske portove, protokol, adresu uređaja " +
                "i lokaciju konfiguracione datoteke prije ponovnog pokretanja ChangeClip."));
            var compact = CreateWindow(longFault, null);
            try
            {
                var compactRoot = GetRoot(compact);
                compactRoot.Measure(new Size(600, 560));
                compactRoot.Arrange(new Rect(0, 0, 600, 560));
                compactRoot.UpdateLayout();
                var compactStatus = FindNamedBorder(compactRoot, "StatusPanel");
                var button = FindButton(compactRoot, "Razmijeni");
                var statusBounds = compactStatus.TransformToAncestor(compactRoot).TransformBounds(
                    new Rect(0, 0, compactStatus.ActualWidth, compactStatus.ActualHeight));
                var buttonBounds = button.TransformToAncestor(compactRoot).TransformBounds(
                    new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                Assert(StatusText(compact).Contains("konfiguracione datoteke"), "long configuration fault is readable");
                Assert(compactStatus.ActualHeight + 0.5 >= compactStatus.DesiredSize.Height,
                    "status content fits at the minimum window size");
                Assert(statusBounds.Top >= 0 && statusBounds.Bottom <= 560 &&
                    buttonBounds.Top >= 0 && buttonBounds.Bottom <= 560,
                    "status and exchange action stay within the minimum window bounds");
            }
            finally { DisposeWindow(compact); }

            var output = args.Length > 0 ? Path.GetFullPath(args[0]) :
                Path.Combine(FindProjectRoot(), "artifacts", "ui", "changeclip-hardware-preview.png");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            Progress("Rendering hardware preview offscreen.");
            RenderHardwarePreview(CreateWindow(new StubExchangeSession(new ExchangeSnapshot(ExchangeState.Ready,
                "Ubacite 10 ili 20 KM", canAcceptBanknotes: true)), null), output);
            Console.WriteLine("PASS: hardware window states, payout, unresolved claims, close guard, and clean markup");
            Console.WriteLine("PASS: hardware preview written to " + output);
            return 0;
        }

        private static string StatusText(MainWindow window)
        {
            var status = FindNamedTextBlock(GetRoot(window), "StatusText");
            return status.Text;
        }

        private static TextBlock FindNamedTextBlock(DependencyObject parent, string name)
        {
            var text = parent as TextBlock;
            if (text != null && String.Equals(text.Name, name, StringComparison.Ordinal)) return text;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var found = FindNamedTextBlockOrNull(VisualTreeHelper.GetChild(parent, i), name);
                if (found != null) return found;
            }
            throw new InvalidOperationException("TextBlock not found: " + name);
        }

        private static TextBlock FindNamedTextBlockOrNull(DependencyObject parent, string name)
        {
            var text = parent as TextBlock;
            if (text != null && String.Equals(text.Name, name, StringComparison.Ordinal)) return text;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var found = FindNamedTextBlockOrNull(VisualTreeHelper.GetChild(parent, i), name);
                if (found != null) return found;
            }
            return null;
        }

        private static void AssertHardwareMarkupHasNoSimulatorControls()
        {
            var path = Path.Combine(FindProjectRoot(), "src", "ChangeClip.Frontend", "MainWindow.Hardware.xaml");
            var markup = File.ReadAllText(path);
            foreach (var forbidden in new[] { "SIMULATOR", "Ubaci", "Vrati", "CoinStockBox", "Expander" })
                Assert(markup.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) < 0,
                    "hardware markup does not contain " + forbidden);
        }

        private static System.ComponentModel.CancelEventArgs InvokeClosing(MainWindow window)
        {
            var args = new System.ComponentModel.CancelEventArgs();
            var handler = typeof(MainWindow).GetMethod("Window_Closing",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            handler.Invoke(window, new object[] { window, args });
            return args;
        }

        private static void RenderHardwarePreview(MainWindow window, string output)
        {
            var root = GetRoot(window);
            root.DataContext = window.DataContext;
            root.ApplyTemplate();
            root.Measure(new Size(760, 660));
            root.Arrange(new Rect(0, 0, 760, 660));
            root.UpdateLayout();
            var statusPanel = FindNamedBorder(root, "StatusPanel");
            Assert(statusPanel.ActualHeight + 0.5 >= statusPanel.DesiredSize.Height,
                "hardware status panel has enough height for its content");
            var bitmap = new RenderTargetBitmap(760, 660, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(output)) encoder.Save(stream);
            DisposeWindow(window);
        }
#endif

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
        public bool PayingOutObserved { get; private set; }
        public Action ExchangeAction { get; set; }
        public int RefreshCalls { get; private set; }
        public Task RefreshReadinessAsync(CancellationToken cancellationToken)
        {
            RefreshCalls++;
            return Task.CompletedTask;
        }
        public Task ExchangeAsync(CancellationToken cancellationToken)
        {
            ExchangeCalls++;
            if (ExchangeAction != null)
            {
                ExchangeAction();
                return Task.CompletedTask;
            }
            var amount = Snapshot.ReceivedMinor;
            Publish(new ExchangeSnapshot(ExchangeState.Ready,
                "Uspješno. Ubacite 10 ili 20 KM.", 0, amount, 0, true, false, true, null, true));
            return Task.CompletedTask;
        }
        public Task ReturnAsync(CancellationToken cancellationToken) { ReturnCalls++; return Task.CompletedTask; }

        public void Publish(ExchangeSnapshot snapshot)
        {
            lock (_sync)
            {
                _snapshot = snapshot;
                if (snapshot.State == ExchangeState.PayingOut) PayingOutObserved = true;
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
