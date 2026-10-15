using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Text;
using ChangeClip.Backend;
using ChangeClip.Contracts;
using ChangeClip.Devices;

namespace ChangeClip.Service
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (ShouldOpenSetupWindow(args))
            {
                var application = new System.Windows.Application
                {
                    ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose
                };
                return application.Run(new SetupWindow(new RuntimePaths()));
            }
            try { InitializeCommandOutput(); return Run(args); }
            catch (Exception error) { Console.Error.WriteLine("GREŠKA: " + error.Message); return 1; }
        }

        public static bool ShouldOpenSetupWindow(string[] args)
        {
            return SetupWindow.IsDefaultLaunch(args);
        }
        [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int standardHandle);
        [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint processId);
        private static void InitializeCommandOutput()
        {
            // A WinExe has no console on double-click. Preserve redirected pipes for CLI tools,
            // or attach to the invoking console when arguments were supplied interactively.
            uint outputType = GetFileType(GetStdHandle(-11));
            if (outputType != 1 && outputType != 3) AttachConsole(uint.MaxValue);
            var encoding = new UTF8Encoding(false);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), encoding) { AutoFlush = true });
        }
        private static int Run(string[] args)
        {
            if (args.Length == 0 || args[0] == "help" || args[0] == "--help") { Help(); return 0; }
            var values = Parse(args.Skip(1).ToArray());
            if (args[0] == "capabilities")
            {
                Only(values);
                string sdkVersion = DeviceFactory.Nv9RuntimeAvailable ? Assembly.Load("ITLlib").GetName().Version.ToString() : "";
                Console.WriteLine("{\"version\":\"" + typeof(Program).Assembly.GetName().Version + "\",\"nv9Runtime\":" + JsonBool(DeviceFactory.Nv9RuntimeAvailable)
                    + ",\"simulatorCompiled\":" + JsonBool(DeviceFactory.SimulatorAvailable) + ",\"itlVersion\":\"" + sdkVersion + "\"}");
                return 0;
            }
            string root; values.TryGetValue("data-root", out root); values.Remove("data-root");
            if (root != null && !Path.IsPathRooted(root)) throw new ArgumentException("--data-root mora biti apsolutna putanja.");
            var paths = new RuntimePaths(root);
            // Also serializes reads that can import legacy records or finish a durable resolution receipt.
            using (ApplicationLease.Acquire())
            {
                if (args[0] == "configure") { Configure(paths, values); return 0; }
                var store = new LedgerStore(paths.Ledger, paths.LegacyJournal);
                var service = new ResolutionService(store);
                if (args[0] == "status" || args[0] == "list")
                {
                    Only(values);
                    Console.WriteLine("Konfiguracija: " + paths.HardwareConfig);
                    if (!File.Exists(paths.HardwareConfig)) Console.WriteLine("Uređaji nisu podešeni.");
                    else
                    {
                        try
                        {
                            var options = DeviceConfiguration.Read(paths.HardwareConfig);
                            DeviceOptionsValidator.Validate(options, requireHardware: true);
                            Console.WriteLine("Hardware: NV9 " + options.Nv9Port + ", hopper " + options.HopperPort + ", kovanica " + options.CoinValueMinor + " feninga.");
                        }
                        catch (Exception error) { Console.WriteLine("Konfiguracija nije spremna: " + error.Message); }
                    }
                    var rows = store.ReadAll().Where(x => !TransactionStages.IsClosed(x.Stage)).ToArray();
                    Console.WriteLine("Otvoreni zapisi: " + rows.Length);
                    foreach (var row in rows)
                        Console.WriteLine(row.Id + " | " + row.Stage + " | primljeno=" + row.ReceivedMinor + " | potvrđenoIsplaćeno=" + row.PaidMinor
                            + " | ostatak=" + (row.ReceivedMinor - row.PaidMinor) + " | tačan=" + row.IsDebtExact + " | fingerprint=" + service.GetFingerprint(row.Id));
                    return 0;
                }
                if (args[0] == "show")
                {
                    Only(values, "id"); string id = Required(values, "id");
                    var row = store.ReadAll().SingleOrDefault(x => x.Id == id) ?? throw new ArgumentException("Transakcija nije pronađena.");
                    Console.WriteLine("Fingerprint: " + service.GetFingerprint(id)); PrintJson(row);
                    foreach (var receipt in service.ReadResolutions().Where(x => x.TransactionId == id)) PrintJson(receipt);
                    return 0;
                }
                if (args[0] == "resolve")
                {
                    Only(values, "id", "fingerprint", "verified-received-minor", "verified-paid-minor", "manual-paid-minor", "reason", "confirm");
                    string id = Required(values, "id"), fingerprint = Required(values, "fingerprint"), reason = Required(values, "reason");
                    int received = NonNegative(values, "verified-received-minor"), paid = NonNegative(values, "verified-paid-minor"), manual = NonNegative(values, "manual-paid-minor");
                    var row = store.ReadAll().SingleOrDefault(x => x.Id == id) ?? throw new ArgumentException("Transakcija nije pronađena.");
                    if (service.GetFingerprint(id) != fingerprint) throw new InvalidOperationException("Fingerprint je zastario; ponovo pregledajte zapis.");
                    if (TransactionStages.IsClosed(row.Stage) && row.Stage != TransactionStages.Resolved) throw new InvalidOperationException("Transakcija je već zatvorena.");
                    if (received < row.ReceivedMinor || received % 1000 != 0 || paid < row.PaidMinor || (long)paid + manual != received)
                        throw new ArgumentException("Provjereni primitak mora pokriti originalni primitak; provjerena i ručna isplata moraju potpuno izmiriti taj iznos. Sve vrijednosti su u feningima.");
                    string operatorName = WindowsIdentity.GetCurrent().Name;
                    Console.WriteLine("Transakcija: " + id + "; operater: " + operatorName + "; provjereno primljeno: " + received + "; uređaj isplatio: " + paid + "; ručno podmireno: " + manual + " feninga.");
                    Console.WriteLine("Razlog: " + reason);
                    if (!values.ContainsKey("confirm")) { Console.WriteLine("Pregled. Nema promjene. Nakon fizičke provjere i ručnog podmirenja ponovite sa --confirm."); return 0; }
                    PrintJson(service.Resolve(id, fingerprint, operatorName, reason, received, paid, manual));
                    Console.WriteLine("Izmirenje je trajno zabilježeno. Originalni dokazi su očuvani. Nije poslana komanda za isplatu.");
                    return 0;
                }
                throw new ArgumentException("Nepoznata komanda. Pokrenite ChangeClip.Service help.");
            }
        }
        private static void Configure(RuntimePaths paths, Dictionary<string, string> values)
        {
            Only(values, "nv9-port", "hopper-port", "coin-value", "hopper-address", "hopper-echo", "nv9-address", "encrypted-ssp");
            var options = File.Exists(paths.HardwareConfig) ? DeviceConfiguration.Read(paths.HardwareConfig) : new DeviceOptions();
            options.Mode = "Hardware"; options.SimulatorCoins = 0;
            string text;
            if (values.TryGetValue("nv9-port", out text)) options.Nv9Port = text.ToUpperInvariant();
            if (values.TryGetValue("hopper-port", out text)) options.HopperPort = text.ToUpperInvariant();
            if (values.TryGetValue("coin-value", out text)) options.CoinValueMinor = NonNegative(values, "coin-value");
            if (values.TryGetValue("hopper-address", out text)) options.HopperAddress = Byte.Parse(text, CultureInfo.InvariantCulture);
            if (values.TryGetValue("nv9-address", out text)) options.Nv9Address = Byte.Parse(text, CultureInfo.InvariantCulture);
            if (values.TryGetValue("hopper-echo", out text)) options.HopperEcho = Boolean.Parse(text);
            if (values.TryGetValue("encrypted-ssp", out text)) options.UseEncryptedSsp = Boolean.Parse(text);
            DeviceConfiguration.WriteHardware(paths.HardwareConfig, options);
            Console.WriteLine("Sačuvana Hardware konfiguracija: " + paths.HardwareConfig);
            Console.WriteLine("NV9 " + options.Nv9Port + "; hopper " + options.HopperPort + "; kovanica " + options.CoinValueMinor + " feninga.");
            Console.WriteLine("Pokrenite ChangeClip. Podešavanje ne programira uređaje i ne pokreće isplatu.");
        }
        private static Dictionary<string, string> Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || args[i].Length < 3) throw new ArgumentException("Opcije moraju početi sa --.");
                string key = args[i].Substring(2), value = "true";
                if (key != "confirm")
                {
                    if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Nedostaje vrijednost za --" + key);
                    value = args[i];
                }
                if (values.ContainsKey(key)) throw new ArgumentException("Opcija je ponovljena: --" + key);
                values.Add(key, value);
            }
            return values;
        }
        private static void Only(Dictionary<string, string> values, params string[] allowed)
        { foreach (string key in values.Keys) if (!allowed.Contains(key)) throw new ArgumentException("Nepoznata opcija --" + key); }
        private static string Required(Dictionary<string, string> values, string name)
        { string value; if (!values.TryGetValue(name, out value) || string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Obavezna opcija --" + name); return value; }
        private static int NonNegative(Dictionary<string, string> values, string name)
        { int number; if (!Int32.TryParse(Required(values, name), NumberStyles.None, CultureInfo.InvariantCulture, out number)) throw new ArgumentException("--" + name + " mora biti cijeli nenegativan broj."); return number; }
        private static string JsonBool(bool value) { return value ? "true" : "false"; }
        private static void PrintJson<T>(T value)
        {
            using (var stream = new MemoryStream())
            { new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value); Console.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray())); }
        }
        private static void Help()
        {
            Console.WriteLine("ChangeClip.Service - podešavanje i servis. Zatvorite ChangeClip prije upotrebe.");
            Console.WriteLine("configure --nv9-port COM3 --hopper-port COM4 --coin-value 100 [--hopper-address 3] [--hopper-echo true] [--encrypted-ssp true]");
            Console.WriteLine("COM3/COM4 i apoen 100 su primjeri. Upišite stvarne portove i apoen u feningima.");
            Console.WriteLine("status | show --id ID | capabilities");
            Console.WriteLine("resolve --id ID --fingerprint HASH --verified-received-minor N --verified-paid-minor N --manual-paid-minor N --reason \"Opis provjere i ručne isplate\" [--confirm]");
            Console.WriteLine("resolve bez --confirm prikazuje pregled. Potvrdite samo nakon provjere fizičkog novca; alat ne isplaćuje kovanice.");
            Console.WriteLine("Opcioni --data-root APSOLUTNA_PUTANJA bira zasebnu evidenciju. U redovnom radu izostavite ga.");
        }
    }
}
