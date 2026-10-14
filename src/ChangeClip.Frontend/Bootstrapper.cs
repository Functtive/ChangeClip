using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Windows;
using ChangeClip.Backend;
using ChangeClip.Contracts;
using ChangeClip.Devices;

namespace ChangeClip.Frontend
{
    internal static class Bootstrapper
    {
        private static Mutex _instance;
        private static bool _ownsMutex;

        public static MainWindow CreateWindow()
        {
            try
            {
                _instance = new Mutex(true, "Local\\ChangeClip-Exchange", out _ownsMutex);
                if (!_ownsMutex) throw new InvalidOperationException("ChangeClip je već pokrenut. Koristite postojeći prozor.");
                string userRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChangeClip");
                Directory.CreateDirectory(userRoot);
                string localConfig = Path.Combine(userRoot, "device.local.json");
                if (!File.Exists(localConfig))
                {
                    string example = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", "device.example.json");
                    File.Copy(example, localConfig, false);
                }
                DeviceOptions options;
                using (var input = File.OpenRead(localConfig))
                    options = (DeviceOptions)new DataContractJsonSerializer(typeof(DeviceOptions)).ReadObject(input);
                Validate(options);
                bool simulator = string.Equals(options.Mode, "Simulator", StringComparison.OrdinalIgnoreCase);
                string dataRoot = Path.Combine(userRoot, simulator ? "simulator" : "hardware", "data");
                Directory.CreateDirectory(dataRoot);
                DeviceBundle devices = DeviceFactory.Create(options);
                var session = new ExchangeService(devices.BillAcceptor, devices.Hopper,
                    new JsonTransactionStore(Path.Combine(dataRoot, "transactions.json")), options.CoinValueMinor);
                var window = new MainWindow(session, devices.Simulator);
                window.Closed += (sender, args) => ReleaseInstance();
                return window;
            }
            catch (Exception error)
            {
                ReleaseInstance();
                MessageBox.Show("ChangeClip se ne može pokrenuti. " + error.Message,
                    "ChangeClip", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown(1);
                return null;
            }
        }

        private static void Validate(DeviceOptions options)
        {
            if (options == null) throw new InvalidDataException("Konfiguracija nije ispravna.");
            bool simulator = string.Equals(options.Mode, "Simulator", StringComparison.OrdinalIgnoreCase);
            bool hardware = string.Equals(options.Mode, "Hardware", StringComparison.OrdinalIgnoreCase);
            if (!simulator && !hardware) throw new InvalidDataException("Mode mora biti Simulator ili Hardware.");
            if (options.CoinValueMinor <= 0 || 1000 % options.CoinValueMinor != 0 || 2000 % options.CoinValueMinor != 0)
                throw new InvalidDataException("Apoen kovanice mora moći tačno isplatiti 10 i 20 KM.");
            if (options.SimulatorCoins < 0) throw new InvalidDataException("Zaliha simulatora ne može biti negativna.");
            if (hardware && (string.IsNullOrWhiteSpace(options.Nv9Port) || string.IsNullOrWhiteSpace(options.HopperPort)))
                throw new InvalidDataException("Za uređaje upišite stvarne nv9Port i hopperPort COM portove.");
            if (hardware && string.Equals(options.Nv9Port, options.HopperPort, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("NV9 i hopper moraju koristiti odvojene COM portove.");
        }

        private static void ReleaseInstance()
        {
            if (_instance == null) return;
            if (_ownsMutex) { _instance.ReleaseMutex(); _ownsMutex = false; }
            _instance.Dispose(); _instance = null;
        }
    }
}
