using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using ChangeClip.Contracts;
using ChangeClip.Service;

public static class Nv9TestChecks
{
    private static int checks;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static void LeaseHeld()
    {
        bool blocked = false;
        try { using (ApplicationLease.Acquire()) { } } catch (InvalidOperationException) { blocked = true; }
        Check(blocked, "NV9 test must retain the shared application lease.");
    }
    private static void LeaseFree() { using (ApplicationLease.Acquire()) { checks++; } }
    private static DeviceOptions Options() => new DeviceOptions { Nv9Port = "COM7", HopperPort = "invalid hopper", HopperAddress = 0, CoinValueMinor = 0, AutoDetectPorts = true, Mode = "Simulator" };
    public static int Run(string root)
    {
        checks = 0;
        RunSessionChecks().GetAwaiter().GetResult();
        var control = new Nv9TestControl(Options, options => new FakeAcceptor());
        Check(control.AcceptButton is Button && control.AcceptButton.Content.ToString() == "Accept" && !control.IsActive, "NV9 control starts inactive with an actual Accept button.");
        Check(control.TotalText.Text.Contains("0,00") && control.StatusText.Text.Contains("stvarno"), "NV9 test explains real storage and has no invented initial balance.");
        control.ShutdownAsync().GetAwaiter().GetResult();
        return checks;
    }
    private static async Task RunSessionChecks()
    {
        var fake = new FakeAcceptor(); int constructions = 0; DeviceOptions passed = null;
        var session = new Nv9TestSession(options => { constructions++; passed = options; return fake; });
        var input = Options();
        await Task.WhenAll(session.StartAsync(input), session.StartAsync(input)).ConfigureAwait(false);
        Check(constructions == 1 && fake.ConnectCalls == 1 && fake.Enabled && session.IsActive, "Concurrent starts cannot create or enable two acceptors.");
        Check(passed.Mode == "Hardware" && !passed.AutoDetectPorts && passed.Nv9Port == "COM7" && passed.HopperPort == "", "NV9 gets a manual hardware copy independent of hopper/coin configuration.");
        Check(input.Mode == "Simulator" && input.AutoDetectPorts && input.HopperPort == "invalid hopper", "Test start must not mutate setup options.");
        LeaseHeld();
        fake.Emit(BillEventKind.Reading, 1000, "reading"); fake.Emit(BillEventKind.Escrow, 1000, "reading");
        Check(session.ReceivedMinor == 0, "Reading, escrow, and starting Accept are not money.");
        fake.Emit(BillEventKind.Credit, 1000, "note-1"); fake.Emit(BillEventKind.Credit, 1000, "note-1"); fake.Emit(BillEventKind.Stacked, 1000, "note-1");
        fake.Emit(BillEventKind.Stacked, 2000, "note-2");
        Check(session.ReceivedMinor == 3000, "Actual credit/stacked evidence counts once per EventId.");
        fake.Drain = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = session.StopAsync();
        await fake.DrainEntered.Task.ConfigureAwait(false);
        Check(!fake.Enabled && !stopping.IsCompleted && !fake.Disposed && session.IsActive, "Stop disables intake but remains active until final note drains.");
        LeaseHeld();
        fake.Emit(BillEventKind.Credit, 1000, "late-note"); fake.Emit(BillEventKind.Stacked, 1000, "late-note");
        Check(session.ReceivedMinor == 4000, "Final in-flight money must still be counted while stopping.");
        fake.Drain.TrySetResult(true); await stopping.ConfigureAwait(false);
        Check(fake.Disposed && !session.IsActive && fake.Order.IndexOf("disable") < fake.Order.IndexOf("drain") && fake.Order.IndexOf("drain") < fake.Order.IndexOf("dispose"), "Safe shutdown order is disable, drain, port dispose, then lease release.");
        LeaseFree();
        await session.ShutdownAsync().ConfigureAwait(false);

        var faultDevice = new FakeAcceptor(); var faultSession = new Nv9TestSession(options => faultDevice);
        await faultSession.StartAsync(Options()).ConfigureAwait(false);
        faultDevice.Emit(BillEventKind.Credit, 2000, "fault-note");
        faultDevice.Emit(BillEventKind.Fault, 0, null, "physical jam");
        await faultSession.StopAsync().ConfigureAwait(false);
        Check(!faultSession.IsActive && !faultDevice.Enabled && faultDevice.Disposed && faultSession.ReceivedMinor == 2000 && faultSession.Status.Contains("physical jam"), "Fault must stop without erasing received cash or replaying intake.");
        LeaseFree(); await faultSession.ShutdownAsync().ConfigureAwait(false);

        var disconnected = new FakeAcceptor(); var lostSession = new Nv9TestSession(options => disconnected);
        await lostSession.StartAsync(Options()).ConfigureAwait(false);
        disconnected.Emit(BillEventKind.Disconnected, 0, null, "USB removed");
        await lostSession.StopAsync().ConfigureAwait(false);
        Check(!lostSession.IsActive && lostSession.Status.Contains("USB removed"), "Disconnect must preserve the actual device error and close intake.");
        await lostSession.ShutdownAsync().ConfigureAwait(false);

        var startingDevice = new FakeAcceptor { ConnectGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
        var racing = new Nv9TestSession(options => startingDevice);
        var start = racing.StartAsync(Options());
        await startingDevice.ConnectEntered.Task.ConfigureAwait(false);
        var stop = racing.StopAsync();
        try { await start.ConfigureAwait(false); } catch (OperationCanceledException) { }
        await stop.ConfigureAwait(false);
        Check(!racing.IsActive && startingDevice.Disposed && startingDevice.EnableCalls == 0, "Stop racing startup must close the port and must never enable afterward.");
        LeaseFree(); await racing.ShutdownAsync().ConfigureAwait(false);

        var stubborn = new FakeAcceptor { FailDispose = true }; var retry = new Nv9TestSession(options => stubborn);
        await retry.StartAsync(Options()).ConfigureAwait(false);
        bool closeFailed = false; try { await retry.StopAsync().ConfigureAwait(false); } catch (IOException) { closeFailed = true; }
        Check(closeFailed && retry.IsActive, "Failed port disposal must not claim an inactive safe session."); LeaseHeld();
        stubborn.FailDispose = false; await retry.StopAsync().ConfigureAwait(false); LeaseFree();
        await retry.ShutdownAsync().ConfigureAwait(false);

        var missing = new Nv9TestSession(options => throw new Exception("Factory should not run for missing NV9 port."));
        bool rejected = false; try { await missing.StartAsync(new DeviceOptions()).ConfigureAwait(false); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && !missing.IsActive, "An NV9 test still requires a real resolved NV9 COM port."); LeaseFree();
        await missing.ShutdownAsync().ConfigureAwait(false);
    }
    private sealed class FakeAcceptor : IBillAcceptor
    {
        internal readonly List<string> Order = new List<string>();
        internal readonly TaskCompletionSource<bool> ConnectEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> DrainEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ConnectGate, Drain;
        internal int ConnectCalls, EnableCalls;
        internal bool Enabled, Disposed, FailDispose;
        public event EventHandler<BillEventArgs> BillEvent;
        public async Task ConnectAsync(CancellationToken token)
        {
            ConnectCalls++; ConnectEntered.TrySetResult(true);
            if (ConnectGate != null)
            {
                using (token.Register(() => ConnectGate.TrySetCanceled())) await ConnectGate.Task.ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
        }
        public Task SetEnabledAsync(bool value, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Enabled = value; if (value) { EnableCalls++; Order.Add("enable"); } else Order.Add("disable"); return Task.CompletedTask; }
        public async Task WaitForIdleAsync(CancellationToken token)
        {
            Order.Add("drain"); DrainEntered.TrySetResult(true);
            if (Drain != null) { using (token.Register(() => Drain.TrySetCanceled())) await Drain.Task.ConfigureAwait(false); }
        }
        public Task AcceptEscrowAsync(CancellationToken token) => throw new Exception("Test session must not use legacy AcceptEscrowAsync.");
        public Task ReturnEscrowAsync(CancellationToken token) => throw new Exception("Test session must not use legacy ReturnEscrowAsync.");
        internal void Emit(BillEventKind kind, int amount, string id, string message = null) => BillEvent?.Invoke(this, new BillEventArgs(kind, amount, "BAM", message, id));
        public void Dispose() { Order.Add("dispose"); if (FailDispose) throw new IOException("port close failed"); Disposed = true; Enabled = false; }
    }
}
