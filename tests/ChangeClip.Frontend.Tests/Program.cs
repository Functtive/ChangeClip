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
                    Assert(readyViewModel.SimulatorHint.Contains("ubacite 10 ili 20 KM"),
                        "simulator explains how to enable exchange");
                }
                finally { DisposeWindow(readyWindow); }

                var awaiting = new ExchangeSnapshot(ExchangeState.AwaitingChoice,
                    "Želite li razmijeniti novac?", 1000, 0, 0, true, true);
                var interactionSession = new StubExchangeSession(awaiting);
                var interactionSimulator = new StubSimulatorControls();
                var interactionWindow = CreateWindow(interactionSession, interactionSimulator);
                try
                {
                    var interactionRoot = GetRoot(interactionWindow);
                    FindExpander(interactionRoot).IsExpanded = true;
                    Layout(interactionWindow);
                    var exchangeButton = FindButton(interactionRoot, "Razmijeni");
                    exchangeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, exchangeButton));
                    Assert(interactionSession.ExchangeCalls == 1, "Razmijeni click invokes session once");

                    var insertButton = FindButton(interactionRoot, "Ubaci 10 KM");
                    insertButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, insertButton));
                    Assert(interactionSimulator.InsertCalls == 1 && interactionSimulator.LastInsertedMinor == 1000,
                        "simulator 10 KM click inserts 1000 minor units");
                }
                finally { DisposeWindow(interactionWindow); }

                var session = new StubExchangeSession(ready);
                var simulator = new StubSimulatorControls { HopperCoins = 17 };
                Progress("Creating simulator preview window.");
                var preview = CreateWindow(session, simulator);
                Progress("Preview window created; publishing 20 KM from worker thread.");
                var twenty = new ExchangeSnapshot(ExchangeState.AwaitingChoice,
                    "Želite li razmijeniti novac?", 2000, 0, 0, true, true);
                PublishFromBackgroundAndDrain(session, twenty, preview);
                Assert(((ExchangeViewModel)preview.DataContext).ReceivedAmount == "20,00 KM",
                    "received amount changes to 20,00 KM after 20 KM input");

                Progress("Publishing 10 KM for the final preview.");
                PublishFromBackgroundAndDrain(session, awaiting, preview);
                AssertButtons(preview, true, "AwaitingChoice");

                var viewModel = (ExchangeViewModel)preview.DataContext;
                Assert(viewModel.ReceivedAmount == "10,00 KM", "final preview shows 10,00 KM");
                Assert(viewModel.Status == "Želite li razmijeniti novac?", "awaiting-choice status");
                Assert(FindExpander(GetRoot(preview)).Visibility == Visibility.Visible, "simulator visible with controls");
                Progress("AwaitingChoice snapshot and bindings verified.");

                var output = args.Length > 0 ? Path.GetFullPath(args[0]) :
                    Path.Combine(FindProjectRoot(), "artifacts", "ui", "changeclip-preview.png");
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                RenderPreview(preview, output);
                Progress("Offscreen PNG render complete.");
                Console.WriteLine("PASS: AwaitingChoice preview written to " + output);
                ((IDisposable)preview.DataContext).Dispose();

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

                Console.WriteLine("PASS: Ready/AwaitingChoice/Debt buttons and simulator visibility");
                Console.WriteLine("PASS: button click -> ExchangeAsync and simulator input wiring");
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
            Layout(window);
        }

        private static void RenderPreview(MainWindow window, string output)
        {
            var root = GetRoot(window);
            FindExpander(root).IsExpanded = true;
            Layout(window);
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
        public Task ExchangeAsync(CancellationToken cancellationToken)
        {
            ExchangeCalls++;
            Publish(new ExchangeSnapshot(ExchangeState.Succeeded, "Uspješno", 1000, 1000));
            return Task.CompletedTask;
        }
        public Task ReturnAsync(CancellationToken cancellationToken) { return Task.CompletedTask; }

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
        public void InsertBanknote(int amountMinor) { InsertCalls++; LastInsertedMinor = amountMinor; }
        public void SetHopperCoins(int coins) { HopperCoins = coins; }
    }
}
