using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ChangeClip.Backend;
using ChangeClip.Contracts;
using ChangeClip.Devices;

namespace ChangeClip.Frontend
{
    internal static class Bootstrapper
    {
        private static ApplicationLease lease;
        public static MainWindow CreateWindow()
        {
            try { lease = ApplicationLease.Acquire(); }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "ChangeClip", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            try
            {
                var paths = new RuntimePaths();
                Directory.CreateDirectory(paths.Root);
#if HARDWARE_RELEASE
                string config = paths.HardwareConfig;
                string exampleName = "device.hardware.example.json";
#else
                string config = Path.Combine(paths.Root, "device.local.json");
                string exampleName = "device.example.json";
#endif
                if (!File.Exists(config)) File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", exampleName), config, false);
                var options = DeviceConfiguration.Read(config);
#if HARDWARE_RELEASE
                DeviceOptionsValidator.Validate(options, requireHardware: true);
                if (!DeviceFactory.Nv9RuntimeAvailable) throw new InvalidOperationException("Izdanje nema ugrađen NV9 adapter. Ponovo instalirajte potpuno izdanje.");
#else
                DeviceOptionsValidator.Validate(options);
#endif
                bool simulator = string.Equals(options.Mode, "Simulator", StringComparison.OrdinalIgnoreCase);
                string dataRoot = simulator ? Path.Combine(paths.Root, "simulator", "data") : paths.HardwareData;
                var store = new LedgerStore(Path.Combine(dataRoot, "ledger-v3"), Path.Combine(dataRoot, "transactions.json"));
                var devices = DeviceFactory.Create(options);
                var session = new ExchangeService(devices.BillAcceptor, devices.Hopper, store, options.CoinValueMinor);
                return Attach(new MainWindow(session, devices.Simulator));
            }
            catch (Exception error)
            {
                return Attach(new MainWindow(new ConfigurationFaultSession(
                    "Uređaji nisu spremni. " + error.Message + " Zatvorite aplikaciju i podesite uređaje pomoću ChangeClip.Service configure. Uputstvo je u SERVIS.md.")));
            }
        }
        private static MainWindow Attach(MainWindow window)
        {
            window.Closed += (sender, args) => { lease?.Dispose(); lease = null; };
            return window;
        }
    }
    internal sealed class ConfigurationFaultSession : IExchangeSession
    {
        public ConfigurationFaultSession(string message) { Snapshot = new ExchangeSnapshot(ExchangeState.Fault, message); }
        public ExchangeSnapshot Snapshot { get; private set; }
        public event EventHandler<ExchangeSnapshot> Changed { add { } remove { } }
        public Task StartAsync(CancellationToken token) { return Task.CompletedTask; }
        public Task RefreshReadinessAsync(CancellationToken token) { return Task.CompletedTask; }
        public Task ExchangeAsync(CancellationToken token) { return Task.CompletedTask; }
        public Task ReturnAsync(CancellationToken token) { return Task.CompletedTask; }
        public void Dispose() { }
    }
}
