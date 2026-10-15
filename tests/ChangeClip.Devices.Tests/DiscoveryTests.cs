using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChangeClip.Contracts;
using ChangeClip.Devices;

internal static class DiscoveryTests
{
    private static int checks;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static DevicePortInfo Usb(string name) => new DevicePortInfo(name, "USB\\VID_0403&PID_6001", "Generic USB serial");
    private sealed class Inventory : IDevicePortInventory
    {
        internal DevicePortInfo[] Ports = new DevicePortInfo[0];
        public Task<IReadOnlyList<DevicePortInfo>> GetPortsAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<DevicePortInfo>>(Ports); }
    }
    private sealed class Probe : IDeviceIdentityProbe
    {
        internal readonly List<string> Calls = new List<string>();
        internal readonly HashSet<string> Nv9 = new HashSet<string>(), Hopper = new HashSet<string>();
        internal bool Echo, BothEcho, IndependentCancellation, BlockUntilCanceled;
        internal string Busy;
        internal Action CancelCaller;
        public Task<DeviceProbeResult> ProbeNv9Async(DevicePortInfo port, DeviceOptions options, CancellationToken token)
        {
            Calls.Add("SSP " + port.PortName); CancelCaller?.Invoke(); token.ThrowIfCancellationRequested();
            if (IndependentCancellation) return Task.FromCanceled<DeviceProbeResult>(new CancellationToken(true));
            if (BlockUntilCanceled)
            {
                var pending = new TaskCompletionSource<DeviceProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                token.Register(() => pending.TrySetCanceled()); return pending.Task;
            }
            if (Busy == port.PortName) throw new UnauthorizedAccessException("busy");
            return Task.FromResult(new DeviceProbeResult(Nv9.Contains(port.PortName)));
        }
        public Task<DeviceProbeResult> ProbeHopperAsync(DevicePortInfo port, DeviceOptions options, bool echo, CancellationToken token)
        {
            Calls.Add("ccTalk " + port.PortName + " " + echo); token.ThrowIfCancellationRequested();
            if (Busy == port.PortName) throw new IOException("missing/busy");
            return Task.FromResult(new DeviceProbeResult(Hopper.Contains(port.PortName) && (BothEcho || echo == Echo)));
        }
    }
    internal static async Task<int> Run()
    {
        checks = 0;
        var options = new DeviceOptions { Mode = "Hardware", CoinValueMinor = 0, AutoDetectPorts = true };
        var inventory = new Inventory(); var probe = new Probe(); var discovery = new DeviceDiscovery(inventory, probe);
        var empty = await discovery.DetectAsync(options, CancellationToken.None);
        Check(!empty.Complete && empty.Nv9Port == null && probe.Calls.Count == 0, "No ports must not open anything.");
        inventory.Ports = new[] { Usb("COM18"), Usb("COM7") }; probe.Nv9.Add("COM7"); probe.Hopper.Add("COM18"); probe.Echo = true;
        var first = await discovery.DetectAsync(options, CancellationToken.None);
        Check(first.Complete && first.Nv9Port == "COM7" && first.HopperPort == "COM18" && first.HopperEcho, "Detection must use roles, not COM order, and confirm echo.");
        Check(string.IsNullOrEmpty(options.Nv9Port) && string.IsNullOrEmpty(options.HopperPort) && options.CoinValueMinor == 0, "Discovery must not mutate options or require denomination.");
        inventory.Ports = new[] { Usb("COM31"), Usb("COM22") }; probe.Nv9.Clear(); probe.Nv9.Add("COM31"); probe.Hopper.Clear(); probe.Hopper.Add("COM22"); probe.Echo = false;
        var changed = await discovery.DetectAsync(options, CancellationToken.None);
        Check(changed.Complete && changed.Nv9Port == "COM31" && changed.HopperPort == "COM22" && !changed.HopperEcho, "Re-enumerated COM names and no-echo must work.");
        probe.Nv9.Clear(); probe.Hopper.Clear();
        var donglesOnly = await discovery.DetectAsync(options, CancellationToken.None);
        Check(!donglesOnly.Complete && donglesOnly.HopperPort == null, "USB dongle VID/PID alone is not device identity.");
        probe.Nv9.Add("COM31");
        var partial = await discovery.DetectAsync(options, CancellationToken.None);
        Check(!partial.Complete && partial.Nv9Port == "COM31" && partial.HopperPort == null, "Partial results must survive a missing device.");
        probe.Nv9.Add("COM22"); probe.Hopper.Add("COM22");
        var duplicate = await discovery.DetectAsync(options, CancellationToken.None);
        Check(!duplicate.Complete && duplicate.Nv9Port == null && duplicate.HopperPort == "COM22", "Duplicate validators must not choose the first.");
        probe.Nv9.Remove("COM22"); probe.Hopper.Add("COM31");
        var duplicateHopper = await discovery.DetectAsync(options, CancellationToken.None);
        Check(!duplicateHopper.Complete && duplicateHopper.HopperPort == null, "Duplicate hoppers must not choose the first.");
        probe.Hopper.Clear(); probe.Hopper.Add("COM31");
        var conflict = await discovery.DetectAsync(options, CancellationToken.None);
        Check(!conflict.Complete && conflict.Nv9Port == null && conflict.HopperPort == null, "One port cannot serve both roles.");
        probe.Hopper.Clear(); probe.Hopper.Add("COM22"); probe.BothEcho = true;
        var echoAmbiguous = await discovery.DetectAsync(options, CancellationToken.None);
        Check(!echoAmbiguous.Complete && echoAmbiguous.HopperPort == null, "Ambiguous echo must not be guessed.");
        probe.BothEcho = false; probe.Busy = "COM31";
        var busy = await discovery.DetectAsync(options, CancellationToken.None);
        Check(!busy.Complete && busy.Nv9Port == null && busy.HopperPort == "COM22", "Busy/missing port must not hide another valid device.");
        probe.Calls.Clear(); inventory.Ports = new[] { new DevicePortInfo("COM6", "BTHENUM\\abc"), new DevicePortInfo("COM8"), new DevicePortInfo("COM1", "ACPI\\PNP0501") };
        var metadataMissing = await discovery.DetectAsync(options, CancellationToken.None);
        Check(!metadataMissing.Complete && probe.Calls.Count == 0, "Bluetooth/unknown/ACPI ports must not be blindly probed.");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); bool observed = false;
            try { await discovery.DetectAsync(options, canceled.Token); } catch (OperationCanceledException) { observed = true; }
            Check(observed, "Pre-canceled discovery must propagate cancellation.");
        }
        inventory.Ports = new[] { Usb("COM4") }; probe.Busy = null; probe.Hopper.Clear(); probe.Nv9.Clear();
        using (var canceled = new CancellationTokenSource())
        {
            probe.CancelCaller = canceled.Cancel; bool observed = false;
            try { await discovery.DetectAsync(options, canceled.Token); } catch (OperationCanceledException) { observed = true; }
            Check(observed, "In-flight caller cancellation must propagate, not become a partial success.");
            probe.CancelCaller = null;
        }
        probe.IndependentCancellation = true;
        Check(!(await discovery.DetectAsync(options, CancellationToken.None)).Complete, "A canceled probe cannot become an identity match.");
        probe.IndependentCancellation = false; probe.BlockUntilCanceled = true;
        Check(!(await discovery.DetectAsync(options, CancellationToken.None)).Complete, "Linked deadline must cancel a cooperative hung probe.");
        probe.BlockUntilCanceled = false;

        var sessions = new Sessions(); var protocol = new ProtocolDeviceIdentityProbe(sessions);
        var nv9 = await protocol.ProbeNv9Async(Usb("COM7"), options, CancellationToken.None);
        Check(nv9.IsMatch && sessions.SspClosed && sessions.Negotiated && sessions.SspCommands.SequenceEqual(new byte[] { 0x11, 0x4A, 0x4B, 0x4C, 0x05, 0x0C }), "SSP discovery must only SYNC/session/setup/serial and dispose.");
        var hopper = await protocol.ProbeHopperAsync(Usb("COM18"), options, true, CancellationToken.None);
        Check(hopper.IsMatch && sessions.CcClosed && sessions.Echo && sessions.Address == options.HopperAddress && sessions.CcCommands.SequenceEqual(new byte[] { 246, 245, 244, 242, 166 }), "ccTalk identity/status only, supplied address/echo, dispose.");
        sessions.Unit = 3;
        Check(!(await protocol.ProbeNv9Async(Usb("COM7"), options, CancellationToken.None)).IsMatch, "ITL SMART Hopper unit 3 is not an NV9 validator.");
        sessions.ThrowStatus = true; bool statusFailed = false;
        try { await protocol.ProbeHopperAsync(Usb("COM18"), options, false, CancellationToken.None); } catch (IOException) { statusFailed = true; }
        Check(statusFailed && sessions.CcClosed && !sessions.Echo, "A failed status probe must dispose its session.");
        foreach (byte code in new byte[] { 0x01, 0x02, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x18, 0x59, 0x60 })
        { bool denied = false; try { DiscoveryCommandPolicy.Ssp(code); } catch (InvalidOperationException) { denied = true; } Check(denied, "Forbidden SSP discovery command: " + code); }
        foreach (byte code in new byte[] { 1, 164, 167, 172, 218, 219, 140, 139, 138 })
        { bool denied = false; try { DiscoveryCommandPolicy.CcTalk(code); } catch (InvalidOperationException) { denied = true; } Check(denied, "Forbidden ccTalk discovery command: " + code); }
        return checks;
    }
    private sealed class Sessions : IDiscoverySessionFactory
    {
        internal readonly List<byte> SspCommands = new List<byte>(), CcCommands = new List<byte>();
        internal bool SspClosed, CcClosed, Negotiated, Echo, ThrowStatus;
        internal byte Address, Unit;
        public ISspDiscoverySession OpenSsp(DevicePortInfo port, DeviceOptions options, CancellationToken token) { SspClosed = false; SspCommands.Clear(); return new Ssp(this); }
        public ICcTalkDiscoverySession OpenCcTalk(DevicePortInfo port, DeviceOptions options, bool echo, CancellationToken token) { CcClosed = false; CcCommands.Clear(); Echo = echo; Address = options.HopperAddress; return new Cc(this); }
        private sealed class Ssp : ISspDiscoverySession
        {
            private readonly Sessions owner; internal Ssp(Sessions owner) { this.owner = owner; }
            public byte[] Command(byte code, params byte[] data)
            {
                DiscoveryCommandPolicy.Ssp(code); owner.SspCommands.Add(code);
                if (code == 0x05) { var setup = new byte[19]; setup[0] = 0xF0; setup[1] = owner.Unit; setup[12] = 1; return setup; }
                if (code == 0x0C) return new byte[] { 0xF0, 1, 2, 3, 4 };
                return new byte[] { 0xF0 };
            }
            public void NegotiateEncryption() { owner.Negotiated = true; Command(0x4A); Command(0x4B); Command(0x4C); }
            public void Dispose() { owner.SspClosed = true; }
        }
        private sealed class Cc : ICcTalkDiscoverySession
        {
            private readonly Sessions owner; internal Cc(Sessions owner) { this.owner = owner; }
            public byte[] Command(byte code)
            {
                DiscoveryCommandPolicy.CcTalk(code); owner.CcCommands.Add(code);
                if (code == 246) return Encoding.ASCII.GetBytes("Azkoyen");
                if (code == 245 || code == 244) return Encoding.ASCII.GetBytes("Payout");
                if (code == 242) return new byte[] { 1, 2, 3 };
                if (owner.ThrowStatus) throw new IOException("lost identity reply");
                return new byte[] { 7, 2, 1, 0 }; // Active payout is observed without changing it.
            }
            public void Dispose() { owner.CcClosed = true; }
        }
    }
}
