using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChangeClip.Contracts;
using ChangeClip.Devices;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string description) { checks++; if (!value) throw new Exception(description); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { checks++; return; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class ImmediateProgress : IProgress<int> { public readonly List<int> Values = new List<int>(); public void Report(int value) { Values.Add(value); } }
    private static async Task Run()
    {
        var devices = DeviceFactory.Create(new DeviceOptions { SimulatorCoins = 3 });
        Check(devices.Simulator != null, "Default mode must be simulator.");
        var events = new List<BillEventArgs>(); devices.BillAcceptor.BillEvent += (s, e) => events.Add(e);
        await devices.BillAcceptor.ConnectAsync(CancellationToken.None);
        await devices.Hopper.ConnectAsync(CancellationToken.None);
        Throws<InvalidOperationException>(() => devices.Simulator.InsertBanknote(1000));
        await devices.BillAcceptor.SetEnabledAsync(true, CancellationToken.None);
        Throws<ArgumentOutOfRangeException>(() => devices.Simulator.InsertBanknote(500));
        devices.Simulator.InsertBanknote(1000);
        devices.Simulator.InsertBanknote(2000);
        Check(events.Count == 8, "Each insert must immediately dispatch four autoaccept events.");
        Check(events.Take(4).Select(e => e.Kind).SequenceEqual(new[] { BillEventKind.Reading, BillEventKind.Escrow, BillEventKind.Credit, BillEventKind.Stacked }), "Automatic event order.");
        var credits = events.Where(e => e.Kind == BillEventKind.Credit).ToArray();
        var stacked = events.Where(e => e.Kind == BillEventKind.Stacked).ToArray();
        Check(credits.Length == 2 && credits.Sum(e => e.AmountMinor) == 3000, "Multiple enabled banknotes must accumulate real credit.");
        Check(credits[0].EventId == stacked[0].EventId && credits[1].EventId == stacked[1].EventId && credits[0].EventId != credits[1].EventId, "Unique per-note GUID stable across credit/stacked.");
        await devices.BillAcceptor.WaitForIdleAsync(CancellationToken.None);
        await devices.BillAcceptor.SetEnabledAsync(false, CancellationToken.None);
        Throws<InvalidOperationException>(() => devices.Simulator.InsertBanknote(1000));
        Throws<NotSupportedException>(() => devices.BillAcceptor.ReturnEscrowAsync(CancellationToken.None).GetAwaiter().GetResult());
        await devices.BillAcceptor.SetEnabledAsync(true, CancellationToken.None);
        var dispatchStarted = new ManualResetEventSlim(false);
        var releaseDispatch = new ManualResetEventSlim(false);
        bool finalCallbackCompleted = false;
        EventHandler<BillEventArgs> delayedJournal = (s, e) =>
        {
            if (e.Kind == BillEventKind.Stacked) { dispatchStarted.Set(); if (!releaseDispatch.Wait(3000)) throw new TimeoutException("Test dispatch blocked."); finalCallbackCompleted = true; }
        };
        devices.BillAcceptor.BillEvent += delayedJournal;
        var insert = Task.Run(() => devices.Simulator.InsertBanknote(1000));
        Check(dispatchStarted.Wait(2000), "Final journal callback must be in flight.");
        await devices.BillAcceptor.SetEnabledAsync(false, CancellationToken.None);
        var idleWait = devices.BillAcceptor.WaitForIdleAsync(CancellationToken.None);
        Check(!idleWait.IsCompleted && !finalCallbackCompleted, "Disable + WaitForIdle must not outrun final journal callback.");
        releaseDispatch.Set(); await insert; await idleWait;
        Check(finalCallbackCompleted, "Idle only after final callback finishes.");
        devices.BillAcceptor.BillEvent -= delayedJournal;
        await devices.BillAcceptor.SetEnabledAsync(true, CancellationToken.None);
        devices.Simulator.InsertBanknote(2000);
        Check(events.Count(e => e.Kind == BillEventKind.Credit) == 4, "Intake must re-enable after payout cycle.");

        var tracker = new Nv9PollTracker(); tracker.Amounts[1] = 1000; tracker.Currencies[1] = "BAM";
        tracker.BeginDispatch();
        var reading = tracker.Parse(new byte[] { 0xF0, 0xEF, 0 }, out Exception pollFault);
        Check(pollFault == null && reading.Single().Kind == BillEventKind.Reading && !tracker.IdleTask.IsCompleted, "READ zero is in-flight, not idle.");
        tracker.EndDispatch(); Check(!tracker.IdleTask.IsCompleted, "Reading remains pending between polls.");
        tracker.BeginDispatch();
        var escrow = tracker.Parse(new byte[] { 0xF0, 0xEF, 1 }, out pollFault);
        string trackedId = escrow.Single().EventId; tracker.EndDispatch();
        Check(tracker.InEscrow && !tracker.IdleTask.IsCompleted, "Transient escrow stays in-flight while POLL autoaccepts.");
        tracker.BeginDispatch();
        var final = tracker.Parse(new byte[] { 0xF0, 0xEE, 1, 0xEE, 1, 0xEB }, out pollFault);
        Check(pollFault == null && final.Count == 2 && final[0].Kind == BillEventKind.Credit && final[1].Kind == BillEventKind.Stacked, "Actual CREDIT/STACKED with repeated CREDIT deduplicated.");
        Check(final.All(e => e.EventId == trackedId), "NV9 final events retain escrow note identity.");
        Check(!tracker.IdleTask.IsCompleted, "Parsed final event is not idle until dispatched.");
        tracker.EndDispatch(); Check(tracker.IdleTask.IsCompleted && !tracker.IdleTask.IsFaulted, "Final callback dispatch completes idle.");
        tracker.BeginDispatch(); var repeatedTerminal = tracker.Parse(new byte[] { 0xF0, 0xEB }, out pollFault); tracker.EndDispatch();
        Check(repeatedTerminal.Count == 0 && pollFault == null, "Repeated same-note STACKED must not post twice.");
        tracker.BeginDispatch(); tracker.Parse(new byte[] { 0xF0, 0xEF, 1 }, out pollFault); tracker.EndDispatch();
        Check(tracker.NoteId != trackedId, "Next banknote has a new NV9 GUID.");
        tracker.BeginDispatch(); var rejected = tracker.Parse(new byte[] { 0xF0, 0xEC }, out pollFault);
        Check(rejected.Single().Kind == BillEventKind.Rejected && !tracker.IdleTask.IsCompleted, "Rejected is pending until callback dispatch.");
        tracker.EndDispatch(); Check(tracker.IdleTask.IsCompleted, "Rejected final dispatch completes idle.");
        var unexpected = new Nv9PollTracker(); unexpected.Amounts[1] = 1000; unexpected.Currencies[1] = "BAM";
        unexpected.BeginDispatch(); var unexpectedEvents = unexpected.Parse(new byte[] { 0xF0, 0xEE, 1 }, out pollFault);
        Check(unexpectedEvents.Single().Kind == BillEventKind.Credit && unexpectedEvents.Single().AmountMinor == 1000 && pollFault != null, "Unexpected actual CREDIT must survive before Fault.");
        unexpected.Fail(pollFault);
        Throws<InvalidDataException>(() => unexpected.IdleTask.GetAwaiter().GetResult());
        var laterFault = new Nv9PollTracker(); laterFault.Amounts[1] = 1000; laterFault.Currencies[1] = "BAM";
        laterFault.BeginDispatch(); laterFault.Parse(new byte[] { 0xF0, 0xEF, 1 }, out pollFault); laterFault.EndDispatch();
        laterFault.BeginDispatch(); var preserved = laterFault.Parse(new byte[] { 0xF0, 0xEE, 1, 0x99 }, out pollFault);
        Check(preserved.Single().Kind == BillEventKind.Credit && pollFault is NotSupportedException, "Known CREDIT must be dispatched even if a later event is unknown.");
        laterFault.Fail(pollFault); Throws<NotSupportedException>(() => laterFault.IdleTask.GetAwaiter().GetResult());
        var contradiction = new Nv9PollTracker(); contradiction.Amounts[1] = 1000; contradiction.Currencies[1] = "BAM";
        contradiction.BeginDispatch(); contradiction.Parse(new byte[] { 0xF0, 0xEF, 1 }, out pollFault); contradiction.EndDispatch();
        contradiction.BeginDispatch(); var contradictoryEvents = contradiction.Parse(new byte[] { 0xF0, 0xEE, 1, 0xEC }, out pollFault);
        Check(contradictoryEvents.Select(e => e.Kind).SequenceEqual(new[] { BillEventKind.Credit, BillEventKind.Rejected }) && pollFault is InvalidDataException, "CREDIT then REJECTED preserves both events before Fault.");
        Check(contradictoryEvents[0].EventId == contradictoryEvents[1].EventId && contradictoryEvents[0].AmountMinor == 1000, "Contradictory reject must retain credited note identity/amount.");
        Check(!contradiction.IdleTask.IsCompleted, "Contradictory final event is never a safe idle completion.");
        contradiction.Fail(pollFault); contradiction.EndDispatch();
        Throws<InvalidDataException>(() => contradiction.IdleTask.GetAwaiter().GetResult());
        var partialProgress = new ImmediateProgress();
        var partial = await devices.Hopper.PayoutAsync(5, partialProgress, CancellationToken.None);
        Check(partial.Outcome == PayoutOutcome.Empty && partial.ConfirmedCoins == 3 && partial.CountReliable, "Empty simulator must report exact partial payout.");
        Check(partialProgress.Values.SequenceEqual(new[] { 1, 2, 3 }), "Progress must be cumulative confirmed coins.");
        Check(devices.Simulator.HopperCoins == 0, "Payout must reduce stock.");
        devices.Simulator.SetHopperCoins(2);
        var full = await devices.Hopper.PayoutAsync(2, null, CancellationToken.None);
        Check(full.Outcome == PayoutOutcome.Completed && full.ConfirmedCoins == 2, "Full payout completion.");
        devices.Simulator.SetHopperCoins(5);
        var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await devices.Hopper.PayoutAsync(2, null, cancelled.Token); throw new Exception("Cancellation expected."); } catch (OperationCanceledException) { checks++; }
        Check(devices.Simulator.HopperCoins == 5, "Cancellation before payout must not dispense.");
        var stopping = devices.Hopper.PayoutAsync(5, null, CancellationToken.None);
        await devices.Hopper.StopAsync(CancellationToken.None);
        var stopped = await stopping;
        Check(stopped.Outcome == PayoutOutcome.Fault && stopped.CountReliable && stopped.ConfirmedCoins == 0, "Stop must preserve reliable confirmed count.");
        devices.BillAcceptor.Dispose(); devices.Hopper.Dispose();

        var request = CcTalkCodec.Encode(3, 1, 167, 0x11, 0x22, 0x33, 10);
        Check(request[1] == 4 && (request.Sum(x => (int)x) & 255) == 0, "ccTalk payout framing/checksum.");
        var countFrame = CcTalkCodec.Encode(1, 3, 0, 0x56, 0x34, 0x12);
        var count = CcTalkCodec.ParseDispenseCount(CcTalkCodec.DecodeReply(countFrame, 3));
        Check(count == 0x123456, "U-II 24-bit counter is little endian.");
        Check(CcTalkCodec.CounterDelta(0xFFFFFE, 1) == 3, "Counter rollover must preserve delta.");
        var state = CcTalkCodec.ParsePaymentStatus(new byte[] { 7, 0, 3, 2 });
        Check(state.Sequence == 7 && state.Remaining == 0 && state.PaidLast == 3 && state.UnpaidLast == 2, "Partial status parsing.");
        Throws<InvalidDataException>(() => CcTalkCodec.ParseDispenseCount(new byte[] { 1, 2 }));
        Throws<InvalidDataException>(() => CcTalkCodec.ParsePaymentStatus(new byte[] { 1, 2, 3 }));
        Throws<InvalidDataException>(() => CcTalkCodec.DecodeReply(countFrame, 4));
        var corrupted = (byte[])countFrame.Clone(); corrupted[4] ^= 1;
        Throws<InvalidDataException>(() => CcTalkCodec.DecodeReply(corrupted, 3));
        Throws<CcTalkNackException>(() => CcTalkCodec.DecodeReply(CcTalkCodec.Encode(1, 3, 5), 3));
        Throws<ArgumentException>(() => DeviceFactory.Create(new DeviceOptions { Mode = "Hardware" }));
    }
    public static int Main()
    {
        try { Run().GetAwaiter().GetResult(); Console.WriteLine("PASS: " + checks + " device checks (simulator/ccTalk codec; no hardware or money)."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
