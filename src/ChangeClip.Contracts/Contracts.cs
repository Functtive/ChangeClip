using System;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ChangeClip.Contracts
{
    public enum ExchangeState
    {
        Disconnected, Initializing, Ready, AwaitingChoice, Accepting,
        PayingOut, Succeeded, Returned, Debt, Fault, ReconciliationRequired
    }

    public sealed class ExchangeSnapshot : EventArgs
    {
        public ExchangeSnapshot(ExchangeState state, string message, int receivedMinor = 0,
            int paidMinor = 0, int owedMinor = 0, bool canExchange = false,
            bool canReturn = false, bool isDebtExact = true, string transactionId = null)
        {
            State = state; Message = message; ReceivedMinor = receivedMinor;
            PaidMinor = paidMinor; OwedMinor = owedMinor; CanExchange = canExchange;
            CanReturn = canReturn; IsDebtExact = isDebtExact; TransactionId = transactionId;
        }
        public ExchangeState State { get; private set; }
        public string Message { get; private set; }
        public int ReceivedMinor { get; private set; }
        public int PaidMinor { get; private set; }
        public int OwedMinor { get; private set; }
        public bool CanExchange { get; private set; }
        public bool CanReturn { get; private set; }
        public bool IsDebtExact { get; private set; }
        public string TransactionId { get; private set; }
    }

    public interface IExchangeSession : IDisposable
    {
        event EventHandler<ExchangeSnapshot> Changed;
        ExchangeSnapshot Snapshot { get; }
        Task StartAsync(CancellationToken cancellationToken);
        Task ExchangeAsync(CancellationToken cancellationToken);
        Task ReturnAsync(CancellationToken cancellationToken);
    }

    public enum BillEventKind { Reading, Escrow, Credit, Stacked, Rejected, Fault, Disconnected }

    public sealed class BillEventArgs : EventArgs
    {
        public BillEventArgs(BillEventKind kind, int amountMinor = 0, string currency = null,
            string message = null, string eventId = null)
        {
            Kind = kind; AmountMinor = amountMinor; Currency = currency;
            Message = message; EventId = eventId;
        }
        public BillEventKind Kind { get; private set; }
        public int AmountMinor { get; private set; }
        public string Currency { get; private set; }
        public string Message { get; private set; }
        public string EventId { get; private set; }
    }

    // Implementations maintain escrow with HOLD until AcceptEscrowAsync/ReturnEscrowAsync.
    // No firmware, dataset, persistent bezel settings or programming API is exposed.
    public interface IBillAcceptor : IDisposable
    {
        event EventHandler<BillEventArgs> BillEvent;
        Task ConnectAsync(CancellationToken cancellationToken);
        Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken);
        Task AcceptEscrowAsync(CancellationToken cancellationToken);
        Task ReturnEscrowAsync(CancellationToken cancellationToken);
    }

    public sealed class HopperStatus
    {
        public HopperStatus(bool connected, bool empty, int? availableCoins = null, string fault = null)
        {
            Connected = connected; Empty = empty; AvailableCoins = availableCoins; Fault = fault;
        }
        public bool Connected { get; private set; }
        public bool Empty { get; private set; }
        public int? AvailableCoins { get; private set; }
        public string Fault { get; private set; }
    }

    public enum PayoutOutcome { Completed, Empty, Fault, Uncertain }

    public sealed class PayoutResult
    {
        public PayoutResult(int requestedCoins, int confirmedCoins, PayoutOutcome outcome,
            bool countReliable = true, string message = null)
        {
            RequestedCoins = requestedCoins; ConfirmedCoins = confirmedCoins;
            Outcome = outcome; CountReliable = countReliable; Message = message;
        }
        public int RequestedCoins { get; private set; }
        public int ConfirmedCoins { get; private set; }
        public PayoutOutcome Outcome { get; private set; }
        public bool CountReliable { get; private set; }
        public string Message { get; private set; }
    }

    public interface ICoinHopper : IDisposable
    {
        Task ConnectAsync(CancellationToken cancellationToken);
        Task<HopperStatus> GetStatusAsync(CancellationToken cancellationToken);
        // Progress is the cumulative number of confirmed coins for this payout.
        Task<PayoutResult> PayoutAsync(int coins, IProgress<int> progress, CancellationToken cancellationToken);
        Task StopAsync(CancellationToken cancellationToken);
    }

    public interface ISimulatorControls
    {
        int HopperCoins { get; }
        void InsertBanknote(int amountMinor);
        void SetHopperCoins(int coins);
    }

    public sealed class DeviceBundle
    {
        public DeviceBundle(IBillAcceptor billAcceptor, ICoinHopper hopper, ISimulatorControls simulator = null)
        {
            BillAcceptor = billAcceptor; Hopper = hopper; Simulator = simulator;
        }
        public IBillAcceptor BillAcceptor { get; private set; }
        public ICoinHopper Hopper { get; private set; }
        public ISimulatorControls Simulator { get; private set; }
    }

    [DataContract]
    public sealed class DeviceOptions
    {
        public DeviceOptions()
        {
            Mode = "Simulator"; CoinValueMinor = 100; SimulatorCoins = 25;
            Nv9Port = ""; HopperPort = ""; HopperAddress = 3;
            Nv9Address = 0; HopperEcho = true; UseEncryptedSsp = true;
        }
        [DataMember(Name = "mode")] public string Mode { get; set; }
        [DataMember(Name = "coinValueMinor")] public int CoinValueMinor { get; set; }
        [DataMember(Name = "simulatorCoins")] public int SimulatorCoins { get; set; }
        [DataMember(Name = "nv9Port")] public string Nv9Port { get; set; }
        [DataMember(Name = "hopperPort")] public string HopperPort { get; set; }
        [DataMember(Name = "hopperAddress")] public byte HopperAddress { get; set; }
        [DataMember(Name = "hopperEcho")] public bool HopperEcho { get; set; }
        [DataMember(Name = "nv9Address")] public byte Nv9Address { get; set; }
        [DataMember(Name = "useEncryptedSsp")] public bool UseEncryptedSsp { get; set; }
        [DataMember(Name = "nv9FixedKeyHex")] public string Nv9FixedKeyHex { get; set; }
    }
}
