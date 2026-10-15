using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ChangeClip.Contracts;
[assembly: InternalsVisibleTo("ChangeClip.Devices.Tests")]
namespace ChangeClip.Devices
{
    // Caller owns a lock. Transport-independent state makes polling/idle order testable without money.
    internal sealed class Nv9PollTracker
    {
        internal readonly Dictionary<byte, int> Amounts = new Dictionary<byte, int>();
        internal readonly Dictionary<byte, string> Currencies = new Dictionary<byte, string>();
        private TaskCompletionSource<bool> idle = NewCompletion();
        private bool active, dispatching, credited, terminalSeen;
        internal bool FirstPoll = true;
        internal bool InEscrow { get; private set; }
        internal int Amount { get; private set; }
        internal string Currency { get; private set; }
        internal string NoteId { get; private set; }
        internal Task IdleTask => idle.Task;
        internal bool Busy => active || dispatching;
        internal Nv9PollTracker() { idle.TrySetResult(true); }
        private static TaskCompletionSource<bool> NewCompletion() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void BeginDispatch() { dispatching = true; if (idle.Task.IsCompleted) idle = NewCompletion(); }
        internal void EndDispatch() { dispatching = false; if (!active) idle.TrySetResult(true); }
        internal void Fail(Exception error) { idle.TrySetException(error); }
        private void BeginNote()
        {
            if (active) return;
            if (idle.Task.IsCompleted) idle = NewCompletion();
            active = true; credited = false; terminalSeen = false; InEscrow = false;
            Amount = 0; Currency = null; NoteId = Guid.NewGuid().ToString("N");
        }
        private BillEventArgs Event(BillEventKind kind) => new BillEventArgs(kind, Amount, Currency, eventId: NoteId);
        internal List<BillEventArgs> Parse(byte[] data, out Exception fault)
        {
            var events = new List<BillEventArgs>(); fault = null;
            try
            {
                if (data == null || data.Length < 1 || data[0] != 0xF0) throw new InvalidDataException("NV9 POLL nije ACK.");
                for (int i = 1; i < data.Length; i++)
                {
                    byte code = data[i];
                    switch (code)
                    {
                        case 0xEF:
                            if (++i >= data.Length) throw new InvalidDataException("NV9 READ payload nedostaje.");
                            BeginNote();
                            if (data[i] == 0) events.Add(Event(BillEventKind.Reading));
                            else
                            {
                                if (!Amounts.TryGetValue(data[i], out int readAmount) || !Currencies.TryGetValue(data[i], out string readCurrency)) throw new InvalidDataException("NV9 READ kanal nepoznat.");
                                Amount = readAmount; Currency = readCurrency; InEscrow = true;
                                events.Add(Event(BillEventKind.Escrow));
                            }
                            break;
                        case 0xEE:
                            if (++i >= data.Length) throw new InvalidDataException("NV9 CREDIT payload nedostaje.");
                            bool unexpected = !active;
                            BeginNote();
                            if (credited) break; // Repeated CREDIT for the same in-flight note is not posted twice.
                            Amounts.TryGetValue(data[i], out int value); Currencies.TryGetValue(data[i], out string currency);
                            bool mismatch = (Amount != 0 && Amount != value) || value == 0 || currency != "BAM" || (value != 1000 && value != 2000);
                            Amount = value; Currency = currency; credited = true; InEscrow = false;
                            events.Add(Event(BillEventKind.Credit)); // Always preserve actual CREDIT before reporting a fault.
                            if (unexpected || mismatch) throw new InvalidDataException("Neočekivan NV9 CREDIT; potvrđeni događaj je emitovan prije faulta, potrebno usklađivanje.");
                            break;
                        case 0xEB:
                            if (!active && terminalSeen) break; // Duplicate terminal event for the same note.
                            bool missingCredit = !credited;
                            if (!active) BeginNote();
                            events.Add(Event(BillEventKind.Stacked));
                            active = false; terminalSeen = true; InEscrow = false;
                            if (missingCredit) throw new InvalidDataException("NV9 STACKED bez CREDIT; stvarni STACKED je emitovan, potrebno usklađivanje.");
                            break;
                        case 0xEC:
                            if (!active && terminalSeen && !credited) break;
                            if (!active && !terminalSeen) BeginNote();
                            bool rejectedAfterCredit = credited;
                            events.Add(Event(BillEventKind.Rejected));
                            active = false; terminalSeen = true; InEscrow = false;
                            if (rejectedAfterCredit) throw new InvalidDataException("NV9 REJECTED nakon nepovratnog CREDIT; događaji su sačuvani, potrebno usklađivanje.");
                            break;
                        case 0xF1:
                            if (!FirstPoll) throw new InvalidDataException("NV9 reset tokom sesije; potrebno usklađivanje."); break;
                        case 0xE1: case 0xE2: case 0xE6:
                            if (++i >= data.Length) throw new InvalidDataException("NV9 recovery payload nedostaje.");
                            throw new IOException("NV9 recovery/fraud event " + code.ToString("X2") + "; ručno usklađivanje.");
                        case 0xE0: case 0xE3: case 0xE7: case 0xE9: case 0xEA:
                            throw new IOException("NV9 fizička greška " + code.ToString("X2"));
                        case 0xED: case 0xCC:
                            BeginNote(); break;
                        case 0xE4: case 0xE8: case 0xB5: case 0xB6: break;
                        default: throw new NotSupportedException("Nepoznat NV9 poll event " + code.ToString("X2") + "; parser zaustavljen.");
                    }
                }
            }
            catch (Exception ex) { fault = ex; }
            FirstPoll = false; return events;
        }
    }
}
