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
        await Task.Delay(300);
        Check(events.Count == 2 && events[1].Kind == BillEventKind.Escrow, "Escrow must remain held until explicit choice.");
        Check(events.All(e => e.Kind != BillEventKind.Credit && e.Kind != BillEventKind.Stacked), "No automatic credit/stacking.");
        Throws<InvalidOperationException>(() => devices.Simulator.InsertBanknote(2000));
        await devices.BillAcceptor.SetEnabledAsync(false, CancellationToken.None);
        await devices.BillAcceptor.ReturnEscrowAsync(CancellationToken.None);
        Check(events.Last().Kind == BillEventKind.Rejected && events.Last().AmountMinor == 1000, "Return must reject held note while input disabled.");
        Check(!events.Any(e => e.Kind == BillEventKind.Credit), "Returned note must never create credit.");
        string rejectedId = events.Last().EventId;
        await devices.BillAcceptor.SetEnabledAsync(true, CancellationToken.None);
        devices.Simulator.InsertBanknote(2000);
        await devices.BillAcceptor.AcceptEscrowAsync(CancellationToken.None);
        var credit = events.Single(e => e.Kind == BillEventKind.Credit);
        var stacked = events.Single(e => e.Kind == BillEventKind.Stacked);
        Check(credit.AmountMinor == 2000 && stacked.AmountMinor == 2000, "Accepted amount must be correct.");
        Check(credit.EventId == stacked.EventId && credit.EventId != rejectedId, "One unique identity per note, stable across final events.");
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
