using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ChangeClip.Contracts;

namespace ChangeClip.Devices
{
    public sealed class DeviceDiscoveryResult
    {
        public DeviceDiscoveryResult(string nv9Port, string hopperPort, bool hopperEcho, bool complete, string message)
        {
            Nv9Port = nv9Port; HopperPort = hopperPort; HopperEcho = hopperEcho;
            Complete = complete && !string.IsNullOrWhiteSpace(nv9Port) && !string.IsNullOrWhiteSpace(hopperPort) && !string.Equals(nv9Port, hopperPort, StringComparison.OrdinalIgnoreCase);
            Message = message ?? "";
        }
        public string Nv9Port { get; private set; }
        public string HopperPort { get; private set; }
        public bool HopperEcho { get; private set; }
        public bool Complete { get; private set; }
        public string Message { get; private set; }
    }
    public sealed class DevicePortInfo
    {
        public DevicePortInfo(string portName, string pnpDeviceId = null, string friendlyName = null)
        { PortName = portName; PnpDeviceId = pnpDeviceId ?? ""; FriendlyName = friendlyName ?? ""; }
        public string PortName { get; private set; }
        public string PnpDeviceId { get; private set; }
        public string FriendlyName { get; private set; }
        public bool IsUsb => PnpDeviceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) || PnpDeviceId.StartsWith("FTDIBUS\\", StringComparison.OrdinalIgnoreCase);
    }
    public sealed class DeviceProbeResult
    {
        public DeviceProbeResult(bool isMatch, string identity = null) { IsMatch = isMatch; Identity = identity ?? ""; }
        public bool IsMatch { get; private set; }
        public string Identity { get; private set; }
    }
    public interface IDevicePortInventory { Task<IReadOnlyList<DevicePortInfo>> GetPortsAsync(CancellationToken token); }
    public interface IDeviceIdentityProbe
    {
        Task<DeviceProbeResult> ProbeNv9Async(DevicePortInfo port, DeviceOptions options, CancellationToken token);
        Task<DeviceProbeResult> ProbeHopperAsync(DevicePortInfo port, DeviceOptions options, bool echo, CancellationToken token);
    }
    public sealed class DeviceDiscovery
    {
        private readonly IDevicePortInventory inventory;
        private readonly IDeviceIdentityProbe probe;
        public DeviceDiscovery() : this(new WindowsUsbPortInventory(), new ProtocolDeviceIdentityProbe()) { }
        public DeviceDiscovery(IDevicePortInventory inventory, IDeviceIdentityProbe probe)
        { this.inventory = inventory ?? throw new ArgumentNullException(nameof(inventory)); this.probe = probe ?? throw new ArgumentNullException(nameof(probe)); }
        public async Task<DeviceDiscoveryResult> DetectAsync(DeviceOptions options, CancellationToken token)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var settings = options.Copy(); // Never mutate live/configured options while testing port assignments.
            DeviceOptionsValidator.ValidateConnectionSettings(settings);
            token.ThrowIfCancellationRequested();
            var all = await inventory.GetPortsAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var ports = all.Where(p => p != null && p.IsUsb && Regex.IsMatch(p.PortName ?? "", "\\ACOM[1-9][0-9]*\\z", RegexOptions.IgnoreCase))
                .GroupBy(p => p.PortName, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderByDescending(p => p.PnpDeviceId.IndexOf("VID_191C", StringComparison.OrdinalIgnoreCase) >= 0).ThenBy(p => p.PortName, StringComparer.OrdinalIgnoreCase).ToArray();
            var validators = new List<string>(); var hoppers = new List<Tuple<string, bool>>(); var diagnostics = new List<string>();
            foreach (var port in ports)
            {
                token.ThrowIfCancellationRequested();
                var nv9 = await TryProbe(t => probe.ProbeNv9Async(port, settings, t), port.PortName, diagnostics, token).ConfigureAwait(false);
                if (nv9 != null && nv9.IsMatch) validators.Add(port.PortName);
                foreach (bool echo in new[] { true, false })
                {
                    var hopper = await TryProbe(t => probe.ProbeHopperAsync(port, settings, echo, t), port.PortName, diagnostics, token).ConfigureAwait(false);
                    if (hopper != null && hopper.IsMatch) hoppers.Add(Tuple.Create(port.PortName, echo));
                }
            }
            token.ThrowIfCancellationRequested();
            string nv9Port = validators.Count == 1 ? validators[0] : null;
            string hopperPort = hoppers.Count == 1 ? hoppers[0].Item1 : null;
            bool hopperEcho = hoppers.Count == 1 ? hoppers[0].Item2 : settings.HopperEcho;
            bool conflict = nv9Port != null && string.Equals(nv9Port, hopperPort, StringComparison.OrdinalIgnoreCase);
            if (conflict) { nv9Port = null; hopperPort = null; }
            bool complete = nv9Port != null && hopperPort != null;
            var messages = new List<string>();
            if (ports.Length == 0) messages.Add(all.Count == 0 ? "Nema COM portova." : "Nema COM portova sa potvrđenim USB/PnP podacima; ne skeniram nepoznate ili Bluetooth portove.");
            messages.Add(validators.Count == 0 ? "NV9/SSP validator nije potvrđen." : validators.Count > 1 ? "Više SSP validatora: " + string.Join(", ", validators) + "; potreban ručni izbor." : "SSP banknote validator: " + validators[0] + ". Unit 0 ne dokazuje tačan fizički model NV9.");
            messages.Add(hoppers.Count == 0 ? "Azkoyen U-II standard nije potvrđen." : hoppers.Count > 1 ? "Dvosmislen hopper port/echo: " + string.Join(", ", hoppers.Select(x => x.Item1 + " echo=" + x.Item2)) + "; potreban ručni izbor." : "Azkoyen U-II standard: " + hoppers[0].Item1 + ", echo=" + hoppers[0].Item2 + ".");
            if (conflict) messages.Add("Isti port odgovara objema ulogama; rezultat je odbijen.");
            if (diagnostics.Count > 0) messages.Add("Preskočeni neodgovarajući/zauzeti portovi: " + string.Join("; ", diagnostics.Distinct()));
            return new DeviceDiscoveryResult(nv9Port, hopperPort, hopperEcho, complete, string.Join(" ", messages));
        }
        private static async Task<DeviceProbeResult> TryProbe(Func<CancellationToken, Task<DeviceProbeResult>> action, string port, List<string> errors, CancellationToken token)
        {
            try
            {
                using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    bounded.CancelAfter(TimeSpan.FromSeconds(3));
                    var result = await action(bounded.Token).ConfigureAwait(false);
                    bounded.Token.ThrowIfCancellationRequested();
                    return result;
                }
            }
            catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); errors.Add(port + " timeout"); return null; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is TimeoutException || ex is NotSupportedException || ex is InvalidOperationException)
            { errors.Add(port + ": " + ex.Message); return null; }
        }
    }
    public sealed class WindowsUsbPortInventory : IDevicePortInventory
    {
        public Task<IReadOnlyList<DevicePortInfo>> GetPortsAsync(CancellationToken token)
        {
            return Task.Run<IReadOnlyList<DevicePortInfo>>(() =>
            {
                token.ThrowIfCancellationRequested();
                var names = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
                var found = new Dictionary<string, DevicePortInfo>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var options = new EnumerationOptions { Timeout = TimeSpan.FromSeconds(2), ReturnImmediately = false };
                    using (var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPClass = 'Ports'", options))
                    using (var results = searcher.Get())
                        foreach (ManagementObject item in results)
                            using (item)
                            {
                                token.ThrowIfCancellationRequested();
                                string name = Convert.ToString(item["Name"]), id = Convert.ToString(item["PNPDeviceID"]);
                                var match = Regex.Match(name ?? "", @"\((COM[1-9][0-9]*)\)", RegexOptions.IgnoreCase);
                                if (match.Success && names.Contains(match.Groups[1].Value)) found[match.Groups[1].Value] = new DevicePortInfo(match.Groups[1].Value, id, name);
                            }
                }
                catch (ManagementException) { } catch (UnauthorizedAccessException) { } catch (System.Runtime.InteropServices.COMException) { }
                token.ThrowIfCancellationRequested();
                // Registry-only names are reported but IsUsb=false, so discovery will not blindly probe them.
                return names.Select(n => found.ContainsKey(n) ? found[n] : new DevicePortInfo(n, null, "USB/PnP metadata unavailable")).ToArray();
            }, token);
        }
    }
}
