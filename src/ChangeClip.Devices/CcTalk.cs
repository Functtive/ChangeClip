using System;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChangeClip.Contracts;

namespace ChangeClip.Devices
{
    public static class CcTalkCodec
    {
        public static byte[] Encode(byte destination, byte source, byte header, params byte[] data)
        {
            data = data ?? new byte[0];
            if (data.Length > 252) throw new ArgumentOutOfRangeException(nameof(data));
            var frame = new byte[data.Length + 5];
            frame[0] = destination; frame[1] = (byte)data.Length; frame[2] = source; frame[3] = header;
            Array.Copy(data, 0, frame, 4, data.Length);
            int sum = 0; foreach (byte b in frame) sum += b;
            frame[frame.Length - 1] = unchecked((byte)-sum);
            return frame;
        }
        public static byte[] DecodeReply(byte[] frame, byte expectedSource, byte expectedDestination = 1)
        {
            if (frame == null || frame.Length < 5 || frame[1] > 252 || frame.Length != frame[1] + 5) throw new InvalidDataException("ccTalk: neispravna dužina framea.");
            int sum = 0; foreach (byte b in frame) sum += b;
            if ((sum & 255) != 0) throw new InvalidDataException("ccTalk checksum ne odgovara.");
            if (frame[0] != expectedDestination || frame[2] != expectedSource) throw new InvalidDataException("ccTalk: odgovor sa pogrešne adrese.");
            if (frame[3] == 5) throw new CcTalkNackException();
            if (frame[3] != 0) throw new InvalidDataException("ccTalk: odgovor nije ACK.");
            var data = new byte[frame[1]]; Array.Copy(frame, 4, data, 0, data.Length); return data;
        }
        public static int ParseDispenseCount(byte[] data)
        {
            if (data == null || data.Length != 3) throw new InvalidDataException("U-II dispense counter mora imati 3 bajta.");
            return data[0] | (data[1] << 8) | (data[2] << 16);
        }
        public static int CounterDelta(int before, int after)
        {
            if (before < 0 || before > 0xFFFFFF || after < 0 || after > 0xFFFFFF) throw new ArgumentOutOfRangeException();
            return (after - before) & 0xFFFFFF;
        }
        public static HopperPaymentStatus ParsePaymentStatus(byte[] data)
        {
            if (data == null || data.Length != 4) throw new InvalidDataException("U-II status mora imati 4 bajta.");
            return new HopperPaymentStatus(data[0], data[1], data[2], data[3]);
        }
    }
    public sealed class CcTalkNackException : IOException { public CcTalkNackException() : base("Azkoyen je odbio komandu (NACK).") { } }
    public sealed class HopperPaymentStatus
    {
        public HopperPaymentStatus(byte sequence, byte remaining, byte paidLast, byte unpaidLast) { Sequence = sequence; Remaining = remaining; PaidLast = paidLast; UnpaidLast = unpaidLast; }
        public byte Sequence { get; private set; }
        public byte Remaining { get; private set; }
        public byte PaidLast { get; private set; }
        public byte UnpaidLast { get; private set; }
    }
    public sealed class AzkoyenCoinHopper : ICoinHopper
    {
        private readonly DeviceOptions options;
        private readonly object io = new object();
        private SerialPort port;
        private byte[] serial;
        private bool connected, disposed, uncertain;
        private int paying, stopRequested;
        public AzkoyenCoinHopper(DeviceOptions options) { this.options = options ?? throw new ArgumentNullException(nameof(options)); }
        // Each request is sent exactly once. In particular header 167 is NEVER retried.
        private byte[] Request(byte header, params byte[] data)
        {
            lock (io)
            {
                if (disposed) throw new ObjectDisposedException(nameof(AzkoyenCoinHopper));
                if (port == null || !port.IsOpen) throw new IOException("ccTalk COM port nije otvoren.");
                var frame = CcTalkCodec.Encode(options.HopperAddress, 1, header, data);
                port.Write(frame, 0, frame.Length);
                if (options.HopperEcho)
                {
                    var echo = ReadExact(frame.Length);
                    if (!echo.SequenceEqual(frame)) throw new InvalidDataException("ccTalk echo ne odgovara poslanom frameu; provjeri hopperEcho/dongle.");
                }
                var prefix = ReadExact(2);
                if (prefix[1] > 252) throw new InvalidDataException("ccTalk neispravna dužina.");
                var rest = ReadExact(prefix[1] + 3);
                return CcTalkCodec.DecodeReply(prefix.Concat(rest).ToArray(), options.HopperAddress);
            }
        }
        private byte[] ReadExact(int length)
        {
            var data = new byte[length]; int offset = 0;
            while (offset < length) offset += port.Read(data, offset, length - offset);
            return data;
        }
        public Task ConnectAsync(CancellationToken token)
        {
            return Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                lock (io)
                {
                    if (connected) return;
                    if (disposed) throw new ObjectDisposedException(nameof(AzkoyenCoinHopper));
                    if (uncertain) throw new InvalidOperationException("Nejasna prethodna isplata zahtijeva ručno usklađivanje i novu sesiju.");
                    port?.Dispose();
                    port = new SerialPort(options.HopperPort, 9600, Parity.None, 8, StopBits.One) { ReadTimeout = 800, WriteTimeout = 800, Handshake = Handshake.None };
                    try
                    {
                        port.Open(); Thread.Sleep(300);
                        Request(254);
                        string manufacturer = Encoding.ASCII.GetString(Request(246)).Trim();
                        string category = Encoding.ASCII.GetString(Request(245)).Trim();
                        string product = Encoding.ASCII.GetString(Request(244)).Trim();
                        if (!manufacturer.Equals("Azkoyen", StringComparison.OrdinalIgnoreCase) || !category.Equals("Payout", StringComparison.OrdinalIgnoreCase) || !product.Equals("Payout", StringComparison.OrdinalIgnoreCase))
                            throw new NotSupportedException("Podržan je samo provjereni Azkoyen U-II standard ccTalk (Payout); encrypted/UPLUS/PIN varijante zahtijevaju zasebnu validaciju.");
                        serial = Request(242);
                        if (serial.Length != 3) throw new InvalidDataException("U-II serijski sigurnosni kod mora imati 3 bajta.");
                        var status = CcTalkCodec.ParsePaymentStatus(Request(166));
                        if (status.Remaining != 0 || status.UnpaidLast != 0) throw new InvalidOperationException("Hopper ima aktivnu ili nepotpunu prethodnu isplatu; ručno uskladi stanje.");
                        Request(164, 0xA5);
                        connected = true;
                    }
                    catch { connected = false; port.Dispose(); throw; }
                }
            });
        }
        public Task<HopperStatus> GetStatusAsync(CancellationToken token)
        {
            return Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                if (!connected) return new HopperStatus(false, false, null, "Hopper nije spojen.");
                try
                {
                    var status = CcTalkCodec.ParsePaymentStatus(Request(166));
                    // Opto command 236 is test-only, not evidence of available payout stock.
                    return new HopperStatus(true, false, null, uncertain ? "Prethodna isplata je nejasna; potrebno usklađivanje." : status.UnpaidLast > 0 ? "Prethodna isplata nije potpuna." : null);
                }
                catch (Exception ex) { return new HopperStatus(false, false, null, ex.Message); }
            });
        }
        public async Task<PayoutResult> PayoutAsync(int coins, IProgress<int> progress, CancellationToken token)
        {
            if (coins < 1 || coins > 255) throw new ArgumentOutOfRangeException(nameof(coins));
            token.ThrowIfCancellationRequested();
            if (!connected || uncertain) throw new InvalidOperationException("Hopper nije spreman ili prethodna isplata nije usklađena.");
            if (Interlocked.CompareExchange(ref paying, 1, 0) != 0) throw new InvalidOperationException("Isplata je već u toku.");
            Interlocked.Exchange(ref stopRequested, 0);
            int before = 0, confirmed = 0; bool sent = false, acknowledged = false;
            try
            {
                var baseline = await Task.Run(() => CcTalkCodec.ParsePaymentStatus(Request(166))).ConfigureAwait(false);
                if (baseline.Remaining != 0 || baseline.UnpaidLast != 0) return new PayoutResult(coins, 0, PayoutOutcome.Fault, true, "Neriješena prethodna isplata.");
                before = await Task.Run(() => CcTalkCodec.ParseDispenseCount(Request(168))).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var payload = new[] { serial[0], serial[1], serial[2], (byte)coins };
                sent = true; // From here a timeout/cancellation is never treated as proof of zero payout.
                await Task.Run(() => Request(167, payload)).ConfigureAwait(false);
                acknowledged = true;
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while (DateTime.UtcNow < deadline)
                {
                    if (token.IsCancellationRequested || Volatile.Read(ref stopRequested) != 0)
                    {
                        await Task.Run(() => Request(172)).ConfigureAwait(false);
                        int afterStop = await Task.Run(() => CcTalkCodec.ParseDispenseCount(Request(168))).ConfigureAwait(false);
                        int finalCount = CcTalkCodec.CounterDelta(before, afterStop);
                        if (finalCount < confirmed || finalCount > coins) throw new InvalidDataException("Dispense counter je promijenjen/resetovan tokom isplate.");
                        confirmed = finalCount; progress?.Report(confirmed);
                        return new PayoutResult(coins, confirmed, PayoutOutcome.Fault, true, "Isplata zaustavljena; broj potvrđen counterom.");
                    }
                    await Task.Delay(100).ConfigureAwait(false);
                    int current = await Task.Run(() => CcTalkCodec.ParseDispenseCount(Request(168))).ConfigureAwait(false);
                    var state = await Task.Run(() => CcTalkCodec.ParsePaymentStatus(Request(166))).ConfigureAwait(false);
                    if (state.Remaining == 0 && state.Sequence != baseline.Sequence)
                        current = await Task.Run(() => CcTalkCodec.ParseDispenseCount(Request(168))).ConfigureAwait(false); // Final counter after idle status closes the count/status race.
                    int delta = CcTalkCodec.CounterDelta(before, current);
                    if (delta < confirmed || delta > coins || (baseline.Sequence != 0 && state.Sequence == 0)) throw new InvalidDataException("Counter/status isplate je nepouzdan (reset ili druga isplata).");
                    if (delta > confirmed) { confirmed = delta; progress?.Report(confirmed); }
                    if (state.Remaining == 0 && confirmed == coins) return new PayoutResult(coins, confirmed, PayoutOutcome.Completed);
                    if (state.Remaining == 0 && state.Sequence != baseline.Sequence && state.UnpaidLast > 0)
                    {
                        if (state.PaidLast != confirmed || state.UnpaidLast + confirmed != coins) throw new InvalidDataException("U-II status i dispense counter nisu usklađeni.");
                        // The status proves a partial payout, not that stock is empty.
                        return new PayoutResult(coins, confirmed, PayoutOutcome.Fault, true, "Hopper je završio djelimičnu isplatu; uzrok nije potvrđen.");
                    }
                }
                throw new TimeoutException("Hopper nije potvrdio završetak isplate.");
            }
            catch (CcTalkNackException ex) when (!acknowledged) { return new PayoutResult(coins, confirmed, PayoutOutcome.Fault, true, ex.Message); }
            catch (Exception ex)
            {
                if (!sent) throw;
                uncertain = true;
                return new PayoutResult(coins, confirmed, PayoutOutcome.Uncertain, false, ex.Message + " Nalog nije ponovljen; ručno usklađivanje obavezno.");
            }
            finally { Interlocked.Exchange(ref paying, 0); }
        }
        public Task StopAsync(CancellationToken token)
        {
            Interlocked.Exchange(ref stopRequested, 1);
            // Payout loop owns stop/count reconciliation when running.
            if (Volatile.Read(ref paying) != 0) return Task.CompletedTask;
            return Task.Run(() => { token.ThrowIfCancellationRequested(); if (connected) Request(172); });
        }
        public void Dispose() { Interlocked.Exchange(ref stopRequested, 1); lock (io) { disposed = true; connected = false; port?.Dispose(); } }
    }
}
