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
        private readonly Nv9PollTracker tracker = new Nv9PollTracker();
        private bool connected, disposed, intakeEnabled;
        private TaskCompletionSource<bool> disabledIdle;
        private int quietPolls;
        public Nv9BillAcceptor(DeviceOptions options) { this.options = options ?? throw new ArgumentNullException(nameof(options)); }
        // The same lock owns every SDK command and in-flight note transition. POLL automatically accepts; no HOLD waiting for a user.
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
                    CheckDisposed(); if (connected) return; if (loop != null) throw new InvalidOperationException("Faulted NV9 session requires reconciliation and a new adapter instance.");
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
                        connected = true; tracker.FirstPoll = true;
                        running = new CancellationTokenSource(); loop = Task.Run(() => PollLoopAsync(running.Token));
                    }
                    catch { comms.CloseComPort(); throw; }
                }
            });
        }
        private void ParseSetup(byte[] data)
        {
            channels.Clear(); tracker.Amounts.Clear(); tracker.Currencies.Clear();
            if (data.Length < 17 || data[1] != 0) throw new NotSupportedException("NV9 adapter podržava samo banknote validator unit type 0.");
            int count = data[12]; int multiplierOffset = 13 + count * 2; int extended = multiplierOffset + 4;
            if (count < 1 || count > 16 || data.Length < extended + count * 7) throw new InvalidDataException("NV9 protocol-6 setup nije podržan ili je skraćen.");
            int multiplier = (data[multiplierOffset] << 16) | (data[multiplierOffset + 1] << 8) | data[multiplierOffset + 2];
            if (multiplier <= 0 || data[multiplierOffset + 3] != 6) throw new InvalidDataException("NV9 multiplier/protocol nije ispravan.");
            for (int i = 0; i < count; i++)
            {
                string currency = System.Text.Encoding.ASCII.GetString(data, extended + i * 3, 3);
                int value = checked(BitConverter.ToInt32(data, extended + count * 3 + i * 4) * multiplier);
                tracker.Amounts[(byte)(i + 1)] = value; tracker.Currencies[(byte)(i + 1)] = currency;
                if (currency == "BAM" && (value == 1000 || value == 2000)) channels.Add((byte)(i + 1), value);
            }
            if (channels.Count == 0) throw new InvalidDataException("Ručno pripremljeni NV9 dataset nema 10/20 BAM kanale.");
        }
        public Task SetEnabledAsync(bool enabled, CancellationToken token)
        {
            return Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                lock (gate)
                {
                    CheckConnected(); Send(enabled ? (byte)0x0A : (byte)0x09); intakeEnabled = enabled;
                    if (!enabled) { quietPolls = 0; disabledIdle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); }
                }
            });
        }
        public async Task WaitForIdleAsync(CancellationToken token)
        {
            Task completion;
            lock (gate) { token.ThrowIfCancellationRequested(); CheckConnected(); completion = !intakeEnabled && disabledIdle != null ? disabledIdle.Task : tracker.IdleTask; }
            var timeout = Task.Delay(TimeSpan.FromSeconds(20), token);
            if (await Task.WhenAny(completion, timeout).ConfigureAwait(false) != completion)
            {
                token.ThrowIfCancellationRequested();
                throw new TimeoutException("NV9 ima nepotvrđenu in-flight novčanicu; stanje nije sigurno za isplatu.");
            }
            await completion.ConfigureAwait(false);
        }
        // Legacy support only: the running workflow autoaccepts through POLL.
        public Task AcceptEscrowAsync(CancellationToken token) => WaitForIdleAsync(token);
        public async Task ReturnEscrowAsync(CancellationToken token)
        {
            Task completion = null;
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                lock (gate)
                {
                    CheckConnected();
                    if (!tracker.InEscrow) throw new InvalidOperationException("NV9 nema novčanice koja se još može odbiti; automatski prihvat je radni tok.");
                    completion = tracker.IdleTask;
                    Send(0x08); // Explicit service action only, never invoked by the exchange engine.
                }
            }).ConfigureAwait(false);
            if (await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(20), token)).ConfigureAwait(false) != completion)
            { token.ThrowIfCancellationRequested(); throw new TimeoutException("NV9 nije potvrdio servisni REJECT."); }
            await completion.ConfigureAwait(false);
        }
        private async Task PollLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    List<BillEventArgs> events; Exception fault;
                    lock (gate)
                    {
                        if (!connected || disposed) return;
                        tracker.BeginDispatch();
                        // Keep polling after DISABLE: an already-entered note must finish and be journaled.
                        events = tracker.Parse(Send(0x07), out fault);
                        if (fault != null) connected = false;
                    }
                    // Preserve CREDIT/STACKED evidence even when parsing a later event detects a fault.
                    foreach (var e in events) BillEvent?.Invoke(this, e);
                    if (fault != null) throw fault;
                    lock (gate)
                    {
                        tracker.EndDispatch(); // Idle only after every backend event callback returns.
                        if (!intakeEnabled && disabledIdle != null)
                        {
                            quietPolls = tracker.Busy ? 0 : quietPolls + 1;
                            if (quietPolls >= 3) disabledIdle.TrySetResult(true); // Drain three fresh quiet polls after disabling intake.
                        }
                    }
                    await Task.Delay(200, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                lock (gate) { connected = false; tracker.Fail(ex); disabledIdle?.TrySetException(ex); }
                BillEvent?.Invoke(this, new BillEventArgs(BillEventKind.Fault, tracker.Amount, tracker.Currency, "NV9: " + ex.Message, tracker.NoteId));
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
                try { if (connected) Send(0x09); } catch { }
                disposed = true; connected = false; tracker.Fail(new ObjectDisposedException(nameof(Nv9BillAcceptor))); disabledIdle?.TrySetException(new ObjectDisposedException(nameof(Nv9BillAcceptor)));
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
        public Task WaitForIdleAsync(CancellationToken token) => Unavailable();
        public Task AcceptEscrowAsync(CancellationToken token) => Unavailable();
        public Task ReturnEscrowAsync(CancellationToken token) => Unavailable();
        public void Dispose() { }
#endif
    }
}
