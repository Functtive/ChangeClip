using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ChangeClip.Contracts;
#if ITL_SDK
using ITLlib;
#endif

namespace ChangeClip.Devices
{
    public sealed class Nv9BillAcceptor : IBillAcceptor
    {
        public event EventHandler<BillEventArgs> BillEvent;
#if ITL_SDK
        private readonly object gate = new object();
        private readonly DeviceOptions options;
        private readonly SSPComms comms = new SSPComms();
        private readonly SSP_COMMAND command = new SSP_COMMAND();
        private readonly SSP_COMMAND_INFO info = new SSP_COMMAND_INFO();
        private readonly Dictionary<byte, int> channels = new Dictionary<byte, int>();
        private CancellationTokenSource running;
        private Task loop;
        private TaskCompletionSource<bool> decision;
        private bool connected, disposed, waitingChoice, accepting, creditSeen, firstPoll = true;
        private int amount;
        private string noteId;
        public Nv9BillAcceptor(DeviceOptions options) { this.options = options ?? throw new ArgumentNullException(nameof(options)); }
        // The same lock owns every SDK command and escrow transition. No demo HoldCount is used.
        private byte[] Send(byte code, params byte[] args)
        {
            command.CommandData[0] = code;
            if (args != null) Array.Copy(args, 0, command.CommandData, 1, args.Length);
            command.CommandDataLength = checked((byte)(1 + (args?.Length ?? 0)));
            if (!comms.SSPSendCommand(command, info)) throw new IOException("NV9 SSP komanda nije potvrđena: " + command.ResponseStatus, comms.GetLastException());
            if (command.ResponseDataLength < 1 || command.ResponseData[0] != 0xF0) throw new IOException("NV9 SSP odgovor: " + (command.ResponseDataLength > 0 ? command.ResponseData[0].ToString("X2") : "prazan"));
            var result = new byte[command.ResponseDataLength]; Array.Copy(command.ResponseData, result, result.Length); return result;
        }
        public Task ConnectAsync(CancellationToken token)
        {
            return Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                lock (gate)
                {
                    CheckDisposed(); if (connected) return;
                    command.ComPort = options.Nv9Port; command.SSPAddress = options.Nv9Address;
                    command.BaudRate = 9600; command.Timeout = 500; command.RetryLevel = 1;
                    command.EncryptionStatus = false; comms.RetryOnFailedCRC(false);
                    if (!comms.OpenSSPComPort(command)) throw new IOException("NV9 COM port nije otvoren.", comms.GetLastException());
                    try
                    {
                        Send(0x11); // SYNC, not a device reset.
                        if (options.UseEncryptedSsp)
                        {
                            var keys = new SSP_KEYS();
                            if (!comms.InitiateSSPHostKeys(keys, command)) throw new IOException("ITL ključ nije pripremljen.");
                            Send(0x4A, BitConverter.GetBytes(keys.Generator));
                            Send(0x4B, BitConverter.GetBytes(keys.Modulus));
                            var exchange = Send(0x4C, BitConverter.GetBytes(keys.HostInter));
                            if (exchange.Length != 9) throw new InvalidDataException("NV9 key exchange dužina nije ispravna.");
                            keys.SlaveInterKey = BitConverter.ToUInt64(exchange, 1);
                            if (!comms.CreateSSPHostEncryptionKey(keys)) throw new IOException("ITL session ključ nije kreiran.");
                            ulong fixedKey = 0x0123456701234567;
                            if (!string.IsNullOrWhiteSpace(options.Nv9FixedKeyHex) && !ulong.TryParse(options.Nv9FixedKeyHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out fixedKey))
                                throw new ArgumentException("nv9FixedKeyHex mora biti 64-bitni hex ključ bez 0x prefiksa.");
                            command.Key.FixedKey = fixedKey; command.Key.VariableKey = keys.KeyHost; command.EncryptionStatus = true;
                        }
                        Send(0x09); // Start disabled; never accept during discovery.
                        Send(0x06, 6); // Volatile host protocol version for channel/currency events.
                        ParseSetup(Send(0x05));
                        ushort mask = 0; foreach (var channel in channels) mask |= (ushort)(1 << (channel.Key - 1));
                        Send(0x02, (byte)mask, (byte)(mask >> 8));
                        connected = true; firstPoll = true;
                        running = new CancellationTokenSource(); loop = Task.Run(() => PollLoopAsync(running.Token));
                    }
                    catch { comms.CloseComPort(); throw; }
                }
            });
        }
        private void ParseSetup(byte[] data)
        {
            channels.Clear();
            if (data.Length < 17 || data[1] != 0) throw new NotSupportedException("NV9 adapter podržava samo banknote validator unit type 0.");
            int count = data[12]; int multiplierOffset = 13 + count * 2; int extended = multiplierOffset + 4;
            if (count < 1 || count > 16 || data.Length < extended + count * 7) throw new InvalidDataException("NV9 protocol-6 setup nije podržan ili je skraćen.");
            int multiplier = (data[multiplierOffset] << 16) | (data[multiplierOffset + 1] << 8) | data[multiplierOffset + 2];
            if (multiplier <= 0 || data[multiplierOffset + 3] != 6) throw new InvalidDataException("NV9 multiplier/protocol nije ispravan.");
            for (int i = 0; i < count; i++)
            {
                string currency = System.Text.Encoding.ASCII.GetString(data, extended + i * 3, 3);
                int value = checked(BitConverter.ToInt32(data, extended + count * 3 + i * 4) * multiplier);
                if (currency == "BAM" && (value == 1000 || value == 2000)) channels.Add((byte)(i + 1), value);
            }
            if (channels.Count == 0) throw new InvalidDataException("Ručno pripremljeni NV9 dataset nema 10/20 BAM kanale.");
        }
        public Task SetEnabledAsync(bool enabled, CancellationToken token)
        {
            return Task.Run(() => { token.ThrowIfCancellationRequested(); lock (gate) { CheckConnected(); Send(enabled ? (byte)0x0A : (byte)0x09); } });
        }
        public async Task AcceptEscrowAsync(CancellationToken token)
        {
            Task completion;
            lock (gate)
            {
                token.ThrowIfCancellationRequested(); CheckConnected();
                if (!waitingChoice) throw new InvalidOperationException("NV9 nema novčanice koja čeka odluku.");
                decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                completion = decision.Task; waitingChoice = false; accepting = true;
                // The next loop sends POLL exactly as the documented escrow accept action.
            }
            await WaitDecisionAsync(completion, token).ConfigureAwait(false);
        }
        public async Task ReturnEscrowAsync(CancellationToken token)
        {
            Task completion = null;
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                lock (gate)
                {
                    CheckConnected(); if (!waitingChoice) throw new InvalidOperationException("NV9 nema novčanice koja čeka odluku.");
                    decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    completion = decision.Task; // Capture under this lock: poll may clear decision before Task.Run returns.
                    try { Send(0x08); waitingChoice = false; accepting = false; }
                    catch (Exception ex) { decision.TrySetException(ex); throw; }
                }
            }).ConfigureAwait(false);
            await WaitDecisionAsync(completion, token).ConfigureAwait(false);
        }
        private static async Task WaitDecisionAsync(Task completion, CancellationToken token)
        {
            var timeout = Task.Delay(TimeSpan.FromSeconds(20), token);
            if (await Task.WhenAny(completion, timeout).ConfigureAwait(false) != completion)
            {
                token.ThrowIfCancellationRequested();
                throw new TimeoutException("NV9 nije potvrdio kraj prihvata/vraćanja; stanje mora biti usklađeno.");
            }
            await completion.ConfigureAwait(false);
        }
        private async Task PollLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var events = new List<BillEventArgs>(); TaskCompletionSource<bool> completed = null;
                    lock (gate)
                    {
                        if (!connected || disposed) return;
                        if (waitingChoice) Send(0x18); // Repeated HOLD replaces POLL until the explicit decision.
                        else
                        {
                            ParsePoll(Send(0x07), events, ref completed);
                            firstPoll = false;
                            if (waitingChoice) { Send(0x18); Send(0x09); } // Hold, then block new intake before notifying the UI/backend.
                        }
                    }
                    foreach (var e in events) BillEvent?.Invoke(this, e);
                    completed?.TrySetResult(true);
                    await Task.Delay(200, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                lock (gate) { connected = false; decision?.TrySetException(ex); }
                BillEvent?.Invoke(this, new BillEventArgs(BillEventKind.Fault, amount, "BAM", "NV9: " + ex.Message, noteId));
            }
        }
        private void ParsePoll(byte[] data, List<BillEventArgs> events, ref TaskCompletionSource<bool> completed)
        {
            for (int i = 1; i < data.Length; i++)
            {
                byte code = data[i];
                switch (code)
                {
                    case 0xEF:
                        if (++i >= data.Length) throw new InvalidDataException("NV9 READ payload nedostaje.");
                        byte channel = data[i];
                        if (channel == 0) events.Add(new BillEventArgs(BillEventKind.Reading));
                        else
                        {
                            if (accepting || decision != null) throw new InvalidDataException("Neočekivana nova novčanica tokom odluke.");
                            if (!channels.TryGetValue(channel, out amount)) { Send(0x08); break; }
                            noteId = Guid.NewGuid().ToString("N"); creditSeen = false; waitingChoice = true;
                            events.Add(new BillEventArgs(BillEventKind.Escrow, amount, "BAM", eventId: noteId));
                        }
                        break;
                    case 0xEE:
                        if (++i >= data.Length || !channels.TryGetValue(data[i], out int credited)) throw new InvalidDataException("NV9 CREDIT kanal nije podržan.");
                        if (!accepting || credited != amount || creditSeen) throw new InvalidDataException("Neočekivan ili dupliran NV9 CREDIT; potrebno usklađivanje.");
                        creditSeen = true; events.Add(new BillEventArgs(BillEventKind.Credit, amount, "BAM", eventId: noteId)); break;
                    case 0xEB:
                        if (!accepting || !creditSeen) throw new InvalidDataException("NV9 STACKED bez potvrđenog CREDIT; potrebno usklađivanje.");
                        events.Add(new BillEventArgs(BillEventKind.Stacked, amount, "BAM", eventId: noteId));
                        completed = decision; decision = null; accepting = waitingChoice = false; break;
                    case 0xEC:
                        if (accepting) throw new InvalidDataException("NV9 je odbio novčanicu tokom prihvata.");
                        events.Add(new BillEventArgs(BillEventKind.Rejected, amount, "BAM", eventId: noteId));
                        completed = decision; decision = null; waitingChoice = false; break;
                    case 0xF1:
                        if (!firstPoll) throw new InvalidDataException("NV9 je resetovan tokom sesije; potrebno usklađivanje."); break;
                    case 0xE1: case 0xE2: case 0xE6:
                        if (++i >= data.Length) throw new InvalidDataException("NV9 event payload nedostaje.");
                        throw new IOException("NV9 recovery/fraud event " + code.ToString("X2") + "; ručno usklađivanje.");
                    case 0xE0: case 0xE3: case 0xE7: case 0xE9: case 0xEA:
                        throw new IOException("NV9 fizička greška " + code.ToString("X2"));
                    case 0xE4: case 0xE8: case 0xED: case 0xCC: case 0xB5: case 0xB6: break;
                    default: throw new NotSupportedException("Nepoznat NV9 poll event " + code.ToString("X2") + "; parser zaustavljen.");
                }
            }
        }
        private void CheckDisposed() { if (disposed) throw new ObjectDisposedException(nameof(Nv9BillAcceptor)); }
        private void CheckConnected() { CheckDisposed(); if (!connected) throw new IOException("NV9 nije spojen."); }
        public void Dispose()
        {
            Task pending;
            lock (gate)
            {
                if (disposed) return;
                running?.Cancel();
                try { if (connected) { if (waitingChoice) Send(0x08); Send(0x09); } } catch { }
                disposed = true; connected = false; decision?.TrySetException(new ObjectDisposedException(nameof(Nv9BillAcceptor)));
                comms.CloseComPort(); pending = loop;
            }
            if (pending != null && Task.CurrentId != pending.Id) { try { pending.Wait(1500); } catch { } }
            running?.Dispose();
        }
#else
        public Nv9BillAcceptor(DeviceOptions options) { }
        private static Task Unavailable() => Task.FromException(new NotSupportedException("NV9 hardware zahtijeva lokalni ITL SDK DLL: vendor/ITL/ITLlib.dll; build nema ITL_SDK."));
        public Task ConnectAsync(CancellationToken token) => Unavailable();
        public Task SetEnabledAsync(bool enabled, CancellationToken token) => Unavailable();
        public Task AcceptEscrowAsync(CancellationToken token) => Unavailable();
        public Task ReturnEscrowAsync(CancellationToken token) => Unavailable();
        public void Dispose() { }
#endif
    }
}
