using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ChangeClip.Contracts;
using ChangeClip.Devices;
using ChangeClip.Service;

internal static class DeviceTestWindowChecks
{
    public static void Run(string root)
    {
        var bill = new Bill();
        var hopper = new Hopper(bill);
        int hopperFactories = 0;
        var paths = new RuntimePaths(Path.Combine(root, "device-window"));
        var window = new SetupWindow(paths,
            (o, t) => Task.FromResult(new DeviceDiscoveryResult("COM31", "COM32", true, true, "OK")),
            o => bill, o => { hopperFactories++; return hopper; });
        Pump(window.ScanDevicesAsync());
        Pump(window.Nv9Test.AcceptAsync());
        Require(bill.Enabled && !window.FindDevicesButton.IsEnabled && !window.SaveButton.IsEnabled,
            "Accept enables the real adapter path and locks configuration");
        bill.Emit(new BillEventArgs(BillEventKind.Credit, 1000, "BAM", eventId: Guid.NewGuid().ToString("N")));
        window.HopperTest.CoinCountTextBox.Text = "4";
        Task payout = window.HopperTest.DispenseAsync();
        Until(() => bill.IdleRequested);
        Require(hopperFactories == 0 && !bill.Enabled && !window.Nv9Test.IsEnabled,
            "Dispense first disables intake and waits for the banknote already inside");
        bill.Emit(new BillEventArgs(BillEventKind.Credit, 2000, "BAM", eventId: Guid.NewGuid().ToString("N")));
        bill.Idle.TrySetResult(true);
        Until(() => hopper.Progress != null);
        Until(() => window.Nv9Test.TotalText.Text.Contains("30"));
        hopper.Progress.Report(2);
        Until(() => window.HopperTest.ReadingsTextBox.Text.Contains("2"));
        Require(bill.Disposed && hopper.PayoutCalls == 1,
            "hopper starts only after NV9 closes its port and releases the shared lease");
        Require(!window.HopperTest.ReadingsTextBox.Text.Contains("4 / 4"),
            "requested coins are not presented as physically counted coins");
        Task repeated = window.HopperTest.DispenseAsync();
        Require(hopper.PayoutCalls == 1, "repeated click cannot issue another payout");
        hopper.Result.TrySetResult(new PayoutResult(4, 4, PayoutOutcome.Completed));
        Pump(payout);
        Pump(repeated);
        Require(hopper.Disposed && !bill.Enabled && window.FindDevicesButton.IsEnabled,
            "completed test releases the port and leaves acceptance under manual control");
        Require(!Directory.Exists(paths.Ledger), "device checks do not mutate customer exchange balances");
        bool closed = false;
        window.Closed += (s, e) => closed = true;
        window.Close();
        Until(() => closed);
        using (ApplicationLease.Acquire()) { }
        Console.WriteLine("PASS: integrated Accept → drain → Dispense window; confirmed progress, duplicate click and close.");
    }

    private static void Pump(Task task)
    {
        Until(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void Until(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Device window check timed out.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(1);
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("FAIL: " + message);
    }

    private sealed class Bill : IBillAcceptor
    {
        public event EventHandler<BillEventArgs> BillEvent;
        public volatile bool Enabled, Disposed, IdleRequested;
        public readonly TaskCompletionSource<bool> Idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ConnectAsync(CancellationToken token) { return Task.CompletedTask; }
        public Task SetEnabledAsync(bool enabled, CancellationToken token) { Enabled = enabled; return Task.CompletedTask; }
        public Task WaitForIdleAsync(CancellationToken token) { IdleRequested = true; return Idle.Task; }
        public Task AcceptEscrowAsync(CancellationToken token) { throw new NotSupportedException(); }
        public Task ReturnEscrowAsync(CancellationToken token) { throw new NotSupportedException(); }
        public void Emit(BillEventArgs args) { BillEvent?.Invoke(this, args); }
        public void Dispose() { Disposed = true; Enabled = false; }
    }

    private sealed class Hopper : ICoinHopper
    {
        private readonly Bill _bill;
        public Hopper(Bill bill) { _bill = bill; }
        public IProgress<int> Progress;
        public int PayoutCalls;
        public bool Disposed;
        public readonly TaskCompletionSource<PayoutResult> Result = new TaskCompletionSource<PayoutResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ConnectAsync(CancellationToken token)
        {
            Require(_bill.Disposed, "NV9 port must be closed before hopper test starts");
            return Task.CompletedTask;
        }
        public Task<HopperStatus> GetStatusAsync(CancellationToken token) { return Task.FromResult(new HopperStatus(true, false)); }
        public Task<PayoutResult> PayoutAsync(int coins, IProgress<int> progress, CancellationToken token)
        {
            Require(coins == 4, "the entered coin count reaches the device adapter");
            Interlocked.Increment(ref PayoutCalls);
            Progress = progress;
            return Result.Task;
        }
        public Task StopAsync(CancellationToken token) { return Task.CompletedTask; }
        public void Dispose() { Disposed = true; }
    }
}
