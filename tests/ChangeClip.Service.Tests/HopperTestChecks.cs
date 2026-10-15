using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using ChangeClip.Contracts;
using ChangeClip.Service;

public static class HopperTestChecks
{
    public static void Run(string root)
    {
        ConfirmedCountsAndPartial();
        BatchAndUncertain();
        ShutdownRetainsLeaseUntilTerminal();
        FailedPortCloseKeepsLease();
        ControlInputAndDuplicateClick();
        ControlRetainsFailedClose();
        CounterResetsForNextPayout();
        ShutdownBeforeDispenseDoesNotConnect();
        Console.WriteLine("PASS: hopper Dispense live counter/green indicator, batching, no retry, duplicate click and shutdown lease");
    }
    private static DeviceOptions Options()
    { return new DeviceOptions { Mode = "Hardware", Nv9Port = "", HopperPort = "COM8", CoinValueMinor = 0, AutoDetectPorts = true }; }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Hopper test: " + message); }
    private static void ConfirmedCountsAndPartial()
    {
        var fake = new FakeHopper { ProgressSteps = new[] { 2, 5 }, Result = new PayoutResult(10, 7, PayoutOutcome.Empty) };
        var received = new List<int>(); var session = new HopperTestSession(Options(), o => fake);
        session.Changed += (s, reading) => received.Add(reading.ConfirmedCoins);
        var result = session.DispenseAsync(10).GetAwaiter().GetResult();
        Check(received.Contains(2) && received.Contains(5) && received.Last() == 7 && !received.Contains(10), "requested count was substituted for actual partial count");
        Check(result.ConfirmedCoins == 7 && result.RemainingCoins == 3 && result.CountReliable && fake.Requests.SequenceEqual(new[] { 10 }), "partial/Empty payout was retried or reported incorrectly");
        Check(fake.Disposed && !session.IsActive, "terminal payout did not release transport");
        session.ShutdownAsync().GetAwaiter().GetResult();
        var invalid = new HopperTestSession(Options(), o => new FakeHopper());
        bool rejected = false; try { invalid.DispenseAsync(0); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "nonpositive count accepted"); invalid.ShutdownAsync().GetAwaiter().GetResult();
    }
    private static void BatchAndUncertain()
    {
        var full = new FakeHopper(); var readings = new List<int>(); var session = new HopperTestSession(Options(), o => full);
        session.Changed += (s, value) => readings.Add(value.ConfirmedCoins);
        var completed = session.DispenseAsync(300).GetAwaiter().GetResult();
        Check(full.Requests.SequenceEqual(new[] { 255, 45 }) && completed.ConfirmedCoins == 300 && readings.Contains(256), "batch progress was not cumulative across 255+45");
        session.ShutdownAsync().GetAwaiter().GetResult();
        var unknown = new FakeHopper { ProgressSteps = new[] { 3 }, Result = new PayoutResult(255, 100, PayoutOutcome.Uncertain, false) };
        var uncertain = new HopperTestSession(Options(), o => unknown); var result = uncertain.DispenseAsync(300).GetAwaiter().GetResult();
        Check(unknown.Requests.SequenceEqual(new[] { 255 }) && result.ConfirmedCoins == 3 && result.RemainingCoins == 297 && !result.CountReliable, "uncertain result invented confirmed counts or retried another batch");
        uncertain.ShutdownAsync().GetAwaiter().GetResult();
    }
    private static void ShutdownRetainsLeaseUntilTerminal()
    {
        var fake = new FakeHopper { Hold = true }; var session = new HopperTestSession(Options(), o => fake);
        var payout = session.DispenseAsync(10); var duplicate = session.DispenseAsync(10);
        PumpUntil(() => fake.Requests.Count == 1);
        Check(ReferenceEquals(payout, duplicate) && fake.Requests.Count == 1, "duplicate session request created a second payout");
        var closing = session.ShutdownAsync();
        Check(fake.StopCalls > 0 && !closing.IsCompleted && !fake.Disposed, "shutdown disposed before payout became terminal");
        bool held = false; try { using (ApplicationLease.Acquire()) { } } catch (InvalidOperationException) { held = true; }
        Check(held, "application lease released before terminal payout result");
        fake.Progress.Report(4); fake.Completion.SetResult(new PayoutResult(10, 4, PayoutOutcome.Fault, true, "Stopped"));
        closing.GetAwaiter().GetResult();
        Check(payout.Result.ConfirmedCoins == 4 && fake.Disposed, "shutdown did not wait for final confirmed count");
        using (ApplicationLease.Acquire()) { }
    }
    private static void FailedPortCloseKeepsLease()
    {
        var fake = new FakeHopper { FailDispose = true }; var session = new HopperTestSession(Options(), o => fake);
        var result = session.DispenseAsync(1).GetAwaiter().GetResult();
        Check(session.IsActive && result.Message.Contains("nije potvrđeno zatvoren"), "failed port close was silently treated as released");
        bool held = false; try { using (ApplicationLease.Acquire()) { } } catch (InvalidOperationException) { held = true; }
        Check(held, "failed port close released application lease");
        bool failed = false; try { session.ShutdownAsync().GetAwaiter().GetResult(); } catch (InvalidOperationException) { failed = true; }
        Check(failed && session.IsActive, "failed shutdown did not retain the device for retry");
        fake.FailDispose = false; session.ShutdownAsync().GetAwaiter().GetResult();
        Check(!session.IsActive && fake.Disposed, "retry shutdown could not release retained port");
        using (ApplicationLease.Acquire()) { }
    }
    private static void ControlInputAndDuplicateClick()
    {
        var fake = new FakeHopper { Hold = true }; var before = new TaskCompletionSource<bool>(); int beforeCalls = 0, factories = 0;
        HopperTestControl control = null;
        control = new HopperTestControl(Options, () =>
        {
            Check(control.IsActive, "IsActive was not set before stopping NV9"); beforeCalls++; return before.Task;
        }, o => { factories++; return fake; });
        Check(control.DispensedCountTextBox.IsReadOnly && control.DispensedCountTextBox.Text == "0" && !control.IsDispensing,
            "Dispensing starts inactive with a read-only zero counter");
        control.CoinCountTextBox.Text = "0";
        control.DispenseAsync().GetAwaiter().GetResult(); Check(beforeCalls == 0 && !control.IsActive, "invalid GUI count started hardware");
        control.CoinCountTextBox.Text = "10";
        var first = control.DispenseAsync(); var duplicate = control.DispenseAsync();
        Check(control.IsActive && !control.IsDispensing && !control.DispenseButton.IsEnabled && ReferenceEquals(first, duplicate) && beforeCalls == 1 && factories == 0, "duplicate click or beforeDispense race");
        before.SetResult(true); PumpUntil(() => fake.Requests.Count == 1);
        PumpUntil(() => control.IsDispensing);
        Check(control.DispensedCountTextBox.Text == "0" && ((SolidColorBrush)control.DispensingIndicator.Fill).Color == Color.FromRgb(60, 211, 139),
            "indicator must turn green when payout starts, before the first counted coin");
        fake.Progress.Report(1); PumpUntil(() => control.DispensedCountTextBox.Text == "1");
        fake.Progress.Report(2); PumpUntil(() => control.DispensedCountTextBox.Text == "2");
        Check(control.IsDispensing && control.DispensedCountTextBox.Text != "10", "GUI displayed requested count without confirmation");
        fake.Completion.SetResult(new PayoutResult(10, 5, PayoutOutcome.Empty)); PumpUntil(() => first.IsCompleted); Drain();
        Check(control.DispensedCountTextBox.Text == "5" && control.StatusText.Text.Contains("Preostalo: 5") && !control.IsActive && !control.IsDispensing && factories == 1,
            "partial payout must retain actual count and turn off the green indicator");
        Pump(control.ShutdownAsync());
    }
    private static void ShutdownBeforeDispenseDoesNotConnect()
    {
        int factories = 0; var before = new TaskCompletionSource<bool>();
        var control = new HopperTestControl(Options, () => before.Task, o => { factories++; return new FakeHopper(); });
        var payout = control.DispenseAsync(); var closing = control.ShutdownAsync();
        Check(!closing.IsCompleted && control.IsActive, "closing did not wait for in-flight beforeDispense callback");
        before.SetResult(true); Pump(closing); Pump(payout);
        Check(factories == 0 && !control.IsActive && !control.DispenseButton.IsEnabled, "port opened after shutdown won beforeDispense race");
    }
    private static void ControlRetainsFailedClose()
    {
        var fake = new FakeHopper { FailDispose = true };
        var control = new HopperTestControl(Options, factory: o => fake);
        var operation = control.DispenseAsync(); Pump(operation);
        Check(control.IsActive && !control.IsDispensing && !control.DispenseButton.IsEnabled && control.StatusText.Text.Contains("Port nije zatvoren"), "GUI did not retain failed port-close ownership");
        bool held = false; try { using (ApplicationLease.Acquire()) { } } catch (InvalidOperationException) { held = true; }
        Check(held, "GUI cleanup failure released lease");
        fake.FailDispose = false; Pump(control.ShutdownAsync());
        Check(!control.IsActive && fake.Disposed, "GUI shutdown retry did not release retained session");
        using (ApplicationLease.Acquire()) { }
    }
    private static void CounterResetsForNextPayout()
    {
        var first = new FakeHopper(); var second = new FakeHopper { Hold = true };
        var devices = new Queue<FakeHopper>(new[] { first, second });
        var control = new HopperTestControl(Options, factory: o => devices.Dequeue());
        control.CoinCountTextBox.Text = "5"; Pump(control.DispenseAsync()); Drain();
        Check(control.DispensedCountTextBox.Text == "5" && !control.IsDispensing, "completed payout does not retain its final count");
        control.CoinCountTextBox.Text = "3"; var next = control.DispenseAsync();
        PumpUntil(() => second.Requests.Count == 1 && control.IsDispensing);
        Check(control.DispensedCountTextBox.Text == "0", "a new payout must start from zero");
        second.Progress.Report(1); PumpUntil(() => control.DispensedCountTextBox.Text == "1");
        second.Completion.SetResult(new PayoutResult(3, 1, PayoutOutcome.Uncertain, false)); Pump(next); Drain();
        Check(control.DispensedCountTextBox.Text == "1" && !control.IsDispensing && control.StatusText.Text.Contains("Nepouzdana"),
            "uncertain payout must retain confirmed count and turn off activity");
        Pump(control.ShutdownAsync());
    }
    private static void Pump(Task task)
    { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> done)
    {
        var timeout = Stopwatch.StartNew();
        while (!done())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Hopper UI test did not finish.");
            Drain(); Thread.Sleep(2);
        }
    }
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private sealed class FakeHopper : ICoinHopper
    {
        public readonly List<int> Requests = new List<int>();
        public readonly TaskCompletionSource<PayoutResult> Completion = new TaskCompletionSource<PayoutResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Hold, Disposed, FailDispose; public int StopCalls; public int[] ProgressSteps; public PayoutResult Result; public IProgress<int> Progress;
        public Task ConnectAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task<HopperStatus> GetStatusAsync(CancellationToken token) { return Task.FromResult(new HopperStatus(true, false)); }
        public Task<PayoutResult> PayoutAsync(int count, IProgress<int> progress, CancellationToken token)
        {
            Requests.Add(count); Progress = progress;
            if (Hold) return Completion.Task;
            foreach (var value in ProgressSteps ?? new[] { 1, count }) progress.Report(value);
            return Task.FromResult(Result ?? new PayoutResult(count, count, PayoutOutcome.Completed));
        }
        public Task StopAsync(CancellationToken token) { StopCalls++; return Task.CompletedTask; }
        public void Dispose() { if (FailDispose) throw new InvalidOperationException("Injected port-close failure"); Disposed = true; }
    }
}
