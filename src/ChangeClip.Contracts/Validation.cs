using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace ChangeClip.Contracts
{
    public static class DeviceOptionsValidator
    {
        public static void ValidateCoinValue(int value)
        {
            if (value <= 0 || 1000 % value != 0 || 2000 % value != 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Upišite stvarni apoen kovanice u feningima; mora tačno dijeliti 10 i 20 KM.");
        }
        public static void Validate(DeviceOptions options, bool requireHardware = false, bool requirePorts = true)
        {
            if (options == null) throw new InvalidDataException("Konfiguracija nije ispravna.");
            bool hardware = string.Equals(options.Mode, "Hardware", StringComparison.OrdinalIgnoreCase);
            bool simulator = string.Equals(options.Mode, "Simulator", StringComparison.OrdinalIgnoreCase);
            if ((!hardware && !simulator) || (requireHardware && !hardware))
                throw new InvalidDataException("Korisničko izdanje zahtijeva Hardware način rada.");
            ValidateCoinValue(options.CoinValueMinor);
            if (options.SimulatorCoins < 0) throw new InvalidDataException("Zaliha ne može biti negativna.");
            if (!hardware) return;
            ValidateConnectionSettings(options, requirePorts);
        }
        public static void ValidateConnectionSettings(DeviceOptions options, bool requirePorts = false)
        {
            if (options == null) throw new InvalidDataException("Konfiguracija nije ispravna.");
            if (options.HopperAddress < 2) throw new InvalidDataException("ccTalk adresa hoppera mora biti 2 do 255.");
            if (options.Nv9Address > 127) throw new InvalidDataException("NV9 SSP adresa mora biti 0 do 127.");
            if (!string.IsNullOrWhiteSpace(options.Nv9FixedKeyHex) && !Regex.IsMatch(options.Nv9FixedKeyHex, "\\A[0-9a-fA-F]{16}\\z"))
                throw new InvalidDataException("NV9 ključ mora sadržavati tačno 16 heksadecimalnih znakova bez 0x prefiksa.");
            foreach (string port in new[] { options.Nv9Port, options.HopperPort })
                if (((requirePorts && !options.AutoDetectPorts) || !string.IsNullOrWhiteSpace(port)) && !Regex.IsMatch(port ?? "", "\\ACOM[1-9][0-9]*\\z", RegexOptions.IgnoreCase))
                    throw new InvalidDataException("Upišite stvarne NV9 i hopper COM portove, npr. COM3 i COM4.");
            if (!string.IsNullOrWhiteSpace(options.Nv9Port) && string.Equals(options.Nv9Port, options.HopperPort, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("NV9 i hopper moraju koristiti različite COM portove.");
        }
    }

    public static class TransactionStages
    {
        public const string FundingPending = "FundingPending", Funded = "Funded", PayoutIntent = "PayoutIntent";
        public const string Succeeded = "Succeeded", Returned = "Returned", Debt = "Debt";
        public const string ReconciliationRequired = "ReconciliationRequired", Resolved = "Resolved";
        public static bool IsClosed(string stage) { return stage == Succeeded || stage == Returned || stage == Resolved; }
    }

    // A per-user exclusive file handle spans Windows/RDP sessions and is released by the OS on process death.
    // Service commands use this same lock even when explicitly examining an alternate data root.
    public sealed class ApplicationLease : IDisposable
    {
        private FileStream handle;
        private ApplicationLease() { }
        public static ApplicationLease Acquire()
        {
            string root = new RuntimePaths().Root;
            Directory.CreateDirectory(root);
            try
            {
                return new ApplicationLease { handle = new FileStream(Path.Combine(root, "application.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None) };
            }
            catch (IOException error)
            { throw new InvalidOperationException("ChangeClip radi ili evidencija nije dostupna. Zatvorite aplikaciju prije konfiguracije ili servisne promjene.", error); }
        }
        public void Dispose() { handle?.Dispose(); handle = null; }
    }
}
