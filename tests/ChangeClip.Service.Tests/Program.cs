using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChangeClip.Backend;
using ChangeClip.Contracts;
using ChangeClip.Devices;
using ChangeClip.Service;

internal static class Program
{
    private static int checks;
    [STAThread]
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ChangeClip-service-" + Guid.NewGuid().ToString("N"));
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(application.Dispatcher));
        try
        {
            SetupWindowChecks(root);
            Console.WriteLine("PASS: " + Nv9TestChecks.Run(root) + " NV9 Accept, confirmed notes, deduplication and shutdown checks.");
            HopperTestChecks.Run(root);
            DeviceTestWindowChecks.Run(root);
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
            string capabilities = Call(true, "capabilities");
#if HARDWARE_RELEASE
            const string simulatorCapability = "false";
#else
            const string simulatorCapability = "true";
#endif
            Assert(capabilities.Contains("\"nv9Runtime\"") && capabilities.Contains("\"simulatorCompiled\":" + simulatorCapability),
                "WinExe CLI preserves redirected standard output");
            Console.WriteLine("PASS: " + checks + " service configuration, setup window, settlement and CLI output checks; no hardware.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            application.Shutdown();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void SetupWindowChecks(string root)
    {
        Assert(SetupWindow.IsDefaultLaunch(null) && SetupWindow.IsDefaultLaunch(new string[0]),
            "no arguments route to the setup window");
        Assert(!SetupWindow.IsDefaultLaunch(new[] { "capabilities" }) &&
            !SetupWindow.IsDefaultLaunch(new[] { "status" }), "CLI arguments remain on the command route");

        var paths = new RuntimePaths(Path.Combine(root, "setup-window"));
        int scans = 0;
        var window = new SetupWindow(paths, (options, cancellationToken) =>
        {
            scans++;
            Assert(options.AutoDetectPorts, "device scan uses automatic port detection");
            Assert(options.CoinValueMinor == 0, "discovery does not invent a coin denomination before user selection");
            return Task.FromResult(new DeviceDiscoveryResult("", "", true, false, "Uređaji nisu pronađeni."));
        });

        Assert(window.CurrentOptions.AutoDetectPorts, "new setup defaults to automatic device detection");
        Assert(window.CoinValueComboBox.SelectedIndex == -1, "zero denomination has no default selection");
        Assert(window.Nv9StatusText.Text.Contains("Nije pronađen") &&
            window.HopperStatusText.Text.Contains("Nije pronađen"), "missing hardware has visible status lines");
        Assert(window.CoinValueComboBox.Items.Count == 6, "coin denomination list contains six real values");
        Assert(window.Nv9Test != null && window.HopperTest != null,
            "setup window contains real NV9 and hopper test controls");
        var values = window.CoinValueComboBox.Items.Cast<ComboBoxItem>().Select(x => (int)x.Tag).ToArray();
        Assert(values.SequenceEqual(new[] { 5, 10, 20, 50, 100, 200 }), "coin denomination choices are in fenings");

        window.FindDevicesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, window.FindDevicesButton));
        Assert(scans == 1, "Find devices button invokes discovery");
        Assert(window.Nv9StatusText.Text.Contains("Nije pronađen") &&
            window.HopperStatusText.Text.Contains("Nije pronađen"), "no-device scan keeps both status lines clear");

        using (ApplicationLease.Acquire())
        {
            window.CoinValueComboBox.SelectedIndex = 4;
            window.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, window.SaveButton));
            Assert(!File.Exists(paths.HardwareConfig) && window.MessageText.Text.Contains("Zatvorite aplikaciju"),
                "save shows a clear message while ChangeClip owns the application lease");
            window.FindDevicesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, window.FindDevicesButton));
            Assert(scans == 1 && window.MessageText.Text.Contains("Zatvorite aplikaciju"),
                "device scan does not probe ports while ChangeClip owns the application lease");
        }

        window.CoinValueComboBox.SelectedIndex = -1;
        window.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, window.SaveButton));
        Assert(!File.Exists(paths.HardwareConfig) && window.MessageText.Text.Contains("Izaberite apoen"),
            "save requires a selected denomination");
        window.CoinValueComboBox.SelectedIndex = 4;
        window.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, window.SaveButton));
        Assert(File.Exists(paths.HardwareConfig), "save button writes hardware configuration");
        var saved = DeviceConfiguration.Read(paths.HardwareConfig);
        Assert(saved.CoinValueMinor == 100 && saved.AutoDetectPorts &&
            String.IsNullOrWhiteSpace(saved.Nv9Port) && String.IsNullOrWhiteSpace(saved.HopperPort),
            "coin value can be saved before ports are found with auto detection enabled");
        DeviceOptionsValidator.Validate(saved, requireHardware: true);

        var invalid = saved.Copy();
        invalid.CoinValueMinor = 3;
        bool rejected = false;
        try { DeviceOptionsValidator.Validate(invalid, requireHardware: true); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Assert(rejected, "invalid denomination is rejected by hardware validation");

        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(860, 800));
        content.Arrange(new Rect(0, 0, 860, 800));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(860, 800, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var projectRoot = FindProjectRoot();
        var preview = Path.Combine(projectRoot, "artifacts", "ui", "changeclip-setup-preview.png");
        Directory.CreateDirectory(Path.GetDirectoryName(preview));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(preview)) encoder.Save(output);
        Console.WriteLine("PASS: no-argument setup window checks and offscreen preview: " + preview);
    }

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "README.md")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the ChangeClip project root.");
    }
    private static string Call(bool success, params string[] args)
    {
        string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ChangeClip.Service.exe");
        var info = new ProcessStartInfo(executable, String.Join(" ", args.Select(x => "\"" + x.Replace("\"", "\\\"") + "\"")))
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8, CreateNoWindow = true };
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
