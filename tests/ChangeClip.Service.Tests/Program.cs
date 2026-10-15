using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ChangeClip.Backend;
using ChangeClip.Contracts;

internal static class Program
{
    private static int checks;
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ChangeClip-service-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new RuntimePaths(root);
            Call(false, "configure", "--data-root", root, "--nv9-port", "COM3", "--hopper-port", "COM3", "--coin-value", "100");
            Assert(!File.Exists(paths.HardwareConfig), "invalid configuration does not create file");
            Call(false, "configure", "--data-root", root, "--nv9-port", "COM3", "--hopper-port", "COM4", "--coin-value", "3");
            Call(true, "configure", "--data-root", root, "--nv9-port", "COM3", "--hopper-port", "COM4", "--coin-value", "100");
            var options = DeviceConfiguration.Read(paths.HardwareConfig);
            Assert(options.Mode == "Hardware" && options.CoinValueMinor == 100 && options.Nv9Port == "COM3" && options.HopperPort == "COM4", "configuration is hardware only with explicit ports and denomination");
            var config = File.ReadAllBytes(paths.HardwareConfig);
            using (ApplicationLease.Acquire())
                Call(false, "configure", "--data-root", root, "--coin-value", "200");
            Assert(File.ReadAllBytes(paths.HardwareConfig).SequenceEqual(config), "live application lease prevents config mutation");
            Call(false, "configure", "--data-root", root, "--nv9-porrt", "COM5");
            var store = new LedgerStore(paths.Ledger, paths.LegacyJournal);
            var service = new ResolutionService(store);
            store.Save(new TransactionRecord { Id = "cash-debt", SchemaVersion = 2, Stage = TransactionStages.Debt, ReceivedMinor = 1000, AmountMinor = 1000, PaidMinor = 700, CoinValueMinor = 100, IsDebtExact = true });
            string fingerprint = service.GetFingerprint("cash-debt");
            string[] resolution = { "resolve", "--data-root", root, "--id", "cash-debt", "--fingerprint", fingerprint, "--verified-received-minor", "1000", "--verified-paid-minor", "700", "--manual-paid-minor", "300", "--reason", "Potvrda prebrojanog novca i rucne isplate" };
            Call(true, resolution);
            Assert(service.ReadResolutions().Count == 0 && store.ReadAll().Single().Stage == TransactionStages.Debt, "preview does not settle debt");
            var stale = (string[])resolution.Clone(); stale[6] = "stale";
            Call(false, stale.Concat(new[] { "--confirm" }).ToArray());
            Assert(service.ReadResolutions().Count == 0, "stale fingerprint cannot settle debt");
            Call(true, resolution.Concat(new[] { "--confirm" }).ToArray());
            Assert(store.ReadAll().Single().Stage == TransactionStages.Resolved && store.ReadAll().Single().PaidMinor == 700, "settlement preserves original device payment");
            var receipt = service.ReadResolutions().Single();
            Assert(receipt.ManualPaidMinor == 300 && receipt.VerifiedPaidMinor == 700 && receipt.Original.Stage == TransactionStages.Debt && !String.IsNullOrWhiteSpace(receipt.OperatorName), "settlement has original evidence and operator audit");
            Call(true, resolution.Concat(new[] { "--confirm" }).ToArray());
            Assert(service.ReadResolutions().Count == 1, "repeated confirmed command is idempotent");
            store.Save(new TransactionRecord { Id = "pending-unknown", Stage = TransactionStages.ReconciliationRequired, SchemaVersion = 2, CoinValueMinor = 100, IsDebtExact = false, HasPendingNote = true });
            string pending = service.GetFingerprint("pending-unknown");
            Call(true, "resolve", "--data-root", root, "--id", "pending-unknown", "--fingerprint", pending, "--verified-received-minor", "0", "--verified-paid-minor", "0", "--manual-paid-minor", "0", "--reason", "Fizicki potvrdjeno da novcanica nije primljena", "--confirm");
            Assert(store.ReadAll().All(x => x.Stage == TransactionStages.Resolved), "explicit verified zero can reconcile abandoned pending note");
            string status = Call(true, "status", "--data-root", root);
            Assert(status.Contains("Otvoreni zapisi: 0"), "status excludes resolved claims");
            Console.WriteLine("PASS: " + checks + " service configuration, settlement and process-lock checks; no hardware.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static string Call(bool success, params string[] args)
    {
        string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ChangeClip.Service.exe");
        var info = new ProcessStartInfo(executable, String.Join(" ", args.Select(x => "\"" + x.Replace("\"", "\\\"") + "\"")))
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using (var process = Process.Start(info))
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000)) { process.Kill(); throw new TimeoutException("Service command did not terminate."); }
            string text = output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
            Assert((process.ExitCode == 0) == success, "CLI " + args[0] + " exit: " + text);
            return text;
        }
    }
    private static void Assert(bool value, string message)
    { if (!value) throw new InvalidOperationException("FAIL: " + message); checks++; }
}
