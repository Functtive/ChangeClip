using System;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChangeClip.Contracts;
#if ITL_SDK
using ITLlib;
#endif

namespace ChangeClip.Devices
{
    public interface ISspDiscoverySession : IDisposable
    {
        byte[] Command(byte command, params byte[] data);
        void NegotiateEncryption();
    }
    public interface ICcTalkDiscoverySession : IDisposable { byte[] Command(byte header); }
    public interface IDiscoverySessionFactory
    {
        ISspDiscoverySession OpenSsp(DevicePortInfo port, DeviceOptions options, CancellationToken token);
        ICcTalkDiscoverySession OpenCcTalk(DevicePortInfo port, DeviceOptions options, bool echo, CancellationToken token);
    }
    public sealed class ProtocolDeviceIdentityProbe : IDeviceIdentityProbe
    {
        private readonly IDiscoverySessionFactory sessions;
        public ProtocolDeviceIdentityProbe() : this(new SerialDiscoverySessionFactory()) { }
        public ProtocolDeviceIdentityProbe(IDiscoverySessionFactory sessions) { this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions)); }
        public Task<DeviceProbeResult> ProbeNv9Async(DevicePortInfo port, DeviceOptions options, CancellationToken token)
        {
            return Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                DeviceOptionsValidator.ValidateConnectionSettings(options);
                using (var session = sessions.OpenSsp(port, options, token))
                {
                    session.Command(0x11); // SYNC changes only the transport/session, never polls cash events.
                    if (options.UseEncryptedSsp) session.NegotiateEncryption();
                    var setup = session.Command(0x05);
                    if (setup.Length < 13 || setup[0] != 0xF0 || setup[1] != 0 || setup[12] < 1 || setup[12] > 16 || setup.Length < 17 + setup[12] * 2)
                        return new DeviceProbeResult(false, "SSP uređaj nije podržani banknote validator unit 0.");
                    var serial = session.Command(0x0C);
                    token.ThrowIfCancellationRequested();
                    if (serial.Length != 5 || serial[0] != 0xF0) return new DeviceProbeResult(false);
                    return new DeviceProbeResult(true, "ITL SSP validator unit 0; serial=" + BitConverter.ToString(serial, 1) + "; NV9-compatible role, exact model not proven by unit 0.");
                }
            }, token);
        }
        public Task<DeviceProbeResult> ProbeHopperAsync(DevicePortInfo port, DeviceOptions options, bool echo, CancellationToken token)
        {
            return Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                DeviceOptionsValidator.ValidateConnectionSettings(options);
                using (var session = sessions.OpenCcTalk(port, options, echo, token))
                {
                    string manufacturer = Encoding.ASCII.GetString(session.Command(246)).Trim();
                    string category = Encoding.ASCII.GetString(session.Command(245)).Trim();
                    string product = Encoding.ASCII.GetString(session.Command(244)).Trim();
                    if (!manufacturer.Equals("Azkoyen", StringComparison.OrdinalIgnoreCase) || !category.Equals("Payout", StringComparison.OrdinalIgnoreCase) || !product.Equals("Payout", StringComparison.OrdinalIgnoreCase))
                        return new DeviceProbeResult(false, "Nije Azkoyen U-II standard; PIN/encrypted/UPLUS nisu potvrđeni.");
                    var serial = session.Command(242);
                    if (serial.Length != 3) return new DeviceProbeResult(false);
                    var status = CcTalkCodec.ParsePaymentStatus(session.Command(166));
                    token.ThrowIfCancellationRequested();
                    return new DeviceProbeResult(true, "Azkoyen U-II standard; serial=" + BitConverter.ToString(serial) + "; remaining=" + status.Remaining + "; unpaid=" + status.UnpaidLast);
                }
            }, token);
        }
    }
    internal static class DiscoveryCommandPolicy
    {
        internal static void Ssp(byte code)
        {
            if (code != 0x11 && code != 0x05 && code != 0x0C && code != 0x4A && code != 0x4B && code != 0x4C)
                throw new InvalidOperationException("Command is not permitted in SSP discovery.");
        }
        internal static void CcTalk(byte code)
        {
            if (code != 246 && code != 245 && code != 244 && code != 242 && code != 166)
                throw new InvalidOperationException("Command is not permitted in ccTalk discovery.");
        }
    }
    public sealed class SerialDiscoverySessionFactory : IDiscoverySessionFactory
    {
        public ISspDiscoverySession OpenSsp(DevicePortInfo port, DeviceOptions options, CancellationToken token)
        {
#if ITL_SDK
            return new ItlIdentitySession(port.PortName, options, token);
#else
            throw new NotSupportedException("NV9 discovery requires compiled ITL SDK support.");
#endif
        }
        public ICcTalkDiscoverySession OpenCcTalk(DevicePortInfo port, DeviceOptions options, bool echo, CancellationToken token)
        { return new CcTalkIdentitySession(port.PortName, options.HopperAddress, echo, token); }
    }
    internal sealed class CcTalkIdentitySession : ICcTalkDiscoverySession
    {
        private readonly SerialPort port;
        private readonly byte address;
        private readonly bool echo;
        private readonly CancellationToken token;
        private CancellationTokenRegistration cancellation;
        public CcTalkIdentitySession(string name, byte address, bool echo, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); this.address = address; this.echo = echo; this.token = token;
            port = new SerialPort(name, 9600, Parity.None, 8, StopBits.One) { ReadTimeout = 300, WriteTimeout = 300, Handshake = Handshake.None };
            try { port.Open(); cancellation = token.Register(() => port.Dispose()); token.ThrowIfCancellationRequested(); port.DiscardInBuffer(); }
            catch { port.Dispose(); throw; }
        }
        private byte[] Read(int count)
        {
            var data = new byte[count]; int offset = 0;
            while (offset < count) { token.ThrowIfCancellationRequested(); offset += port.Read(data, offset, count - offset); }
            return data;
        }
        public byte[] Command(byte header)
        {
            DiscoveryCommandPolicy.CcTalk(header); token.ThrowIfCancellationRequested();
            var frame = CcTalkCodec.Encode(address, 1, header);
            port.Write(frame, 0, frame.Length);
            if (echo && !Read(frame.Length).SequenceEqual(frame)) throw new InvalidDataException("Discovery echo mismatch.");
            var prefix = Read(2); if (prefix[1] > 252) throw new InvalidDataException("Invalid discovery reply length.");
            var reply = CcTalkCodec.DecodeReply(prefix.Concat(Read(prefix[1] + 3)).ToArray(), address);
            token.ThrowIfCancellationRequested(); return reply;
        }
        public void Dispose() { cancellation.Dispose(); port.Dispose(); }
    }
#if ITL_SDK
    internal sealed class ItlIdentitySession : ISspDiscoverySession
    {
        private readonly SSPComms comms = new SSPComms();
        private readonly SSP_COMMAND command = new SSP_COMMAND();
        private readonly SSP_COMMAND_INFO info = new SSP_COMMAND_INFO();
        private readonly DeviceOptions options;
        private readonly CancellationToken token;
        private CancellationTokenRegistration cancellation;
        public ItlIdentitySession(string name, DeviceOptions options, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); this.options = options; this.token = token;
            command.ComPort = name; command.SSPAddress = options.Nv9Address; command.BaudRate = 9600;
            command.Timeout = 300; command.RetryLevel = 1; command.EncryptionStatus = false; comms.RetryOnFailedCRC(false);
            try
            {
                if (!comms.OpenSSPComPort(command)) throw new IOException("SSP discovery port busy/missing.", comms.GetLastException());
                cancellation = token.Register(() => comms.CloseComPort()); token.ThrowIfCancellationRequested();
            }
            catch { comms.CloseComPort(); throw; }
        }
        public byte[] Command(byte code, params byte[] data)
        {
            DiscoveryCommandPolicy.Ssp(code); token.ThrowIfCancellationRequested();
            command.CommandData[0] = code;
            if (data != null) Array.Copy(data, 0, command.CommandData, 1, data.Length);
            command.CommandDataLength = checked((byte)(1 + (data?.Length ?? 0)));
            if (!comms.SSPSendCommand(command, info)) { token.ThrowIfCancellationRequested(); throw new IOException("SSP identity timeout/status " + command.ResponseStatus, comms.GetLastException()); }
            token.ThrowIfCancellationRequested();
            if (command.ResponseDataLength < 1 || command.ResponseData[0] != 0xF0) throw new InvalidDataException("SSP identity reply is not ACK.");
            var reply = new byte[command.ResponseDataLength]; Array.Copy(command.ResponseData, reply, reply.Length); return reply;
        }
        public void NegotiateEncryption()
        {
            var keys = new SSP_KEYS();
            if (!comms.InitiateSSPHostKeys(keys, command)) throw new IOException("SSP discovery session keys failed.");
            token.ThrowIfCancellationRequested();
            Command(0x4A, BitConverter.GetBytes(keys.Generator)); Command(0x4B, BitConverter.GetBytes(keys.Modulus));
            var exchange = Command(0x4C, BitConverter.GetBytes(keys.HostInter));
            if (exchange.Length != 9) throw new InvalidDataException("SSP discovery key reply length.");
            keys.SlaveInterKey = BitConverter.ToUInt64(exchange, 1);
            if (!comms.CreateSSPHostEncryptionKey(keys)) throw new IOException("SSP shared key failed.");
            ulong fixedKey = 0x0123456701234567;
            if (!string.IsNullOrWhiteSpace(options.Nv9FixedKeyHex) && (options.Nv9FixedKeyHex.Length != 16 || !ulong.TryParse(options.Nv9FixedKeyHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out fixedKey)))
                throw new ArgumentException("Invalid SSP fixed key for discovery.");
            command.Key.FixedKey = fixedKey; command.Key.VariableKey = keys.KeyHost; command.EncryptionStatus = true;
        }
        public void Dispose() { cancellation.Dispose(); comms.CloseComPort(); }
    }
#endif
}
