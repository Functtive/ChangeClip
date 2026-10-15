using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using ChangeClip.Contracts;

namespace ChangeClip.Backend
{
    [DataContract]
    internal sealed class LedgerEntry
    {
        [DataMember] public TransactionRecord Transaction { get; set; }
        [DataMember] public string LegacyFingerprint { get; set; }
    }
    internal static class LedgerJson
    {
        internal static byte[] Bytes<T>(T value)
        { using (var stream = new MemoryStream()) { new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value); return stream.ToArray(); } }
        internal static T Read<T>(string path)
        { using (var stream = File.OpenRead(path)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream); }
        internal static string Hash(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        internal static string Fingerprint(TransactionRecord record) { return Hash(Bytes(record)); }
        internal static string Key(string id) { return Hash(Encoding.UTF8.GetBytes(id)); }
        internal static void AtomicWrite<T>(string path, T value, bool replace)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { var bytes = Bytes(value); stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (replace && File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        internal static void Validate(TransactionRecord row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.Id) || string.IsNullOrWhiteSpace(row.Stage) || row.ReceivedMinor < 0 || row.PaidMinor < 0 || row.PaidMinor > row.ReceivedMinor || row.AmountMinor < 0 || row.SchemaVersion < 0 || row.SchemaVersion > 2
                || (row.Credits != null && row.Credits.Any(x => x == null || x.AmountMinor < 0))
                || (row.Batches != null && row.Batches.Any(x => x == null || x.Number <= 0 || x.BaseConfirmedCoins < 0 || x.RequestedCoins <= 0 || x.RequestedCoins > 255 || x.ConfirmedCoins < 0 || x.ConfirmedCoins > x.RequestedCoins)))
                throw new InvalidDataException("Neispravan transakcijski zapis; podaci nisu izmijenjeni.");
            if (row.CoinValueMinor > 0) DeviceOptionsValidator.ValidateCoinValue(row.CoinValueMinor);
            if (row.Stage == TransactionStages.Resolved && string.IsNullOrEmpty(row.ResolutionId)) throw new InvalidDataException("Resolved zapis nema receipt identitet.");
        }
    }

    // The caller holds ApplicationLease. Save reads/writes only this transaction, regardless of history size.
    public sealed class LedgerStore : ITransactionStore
    {
        private readonly string transactionsPath, receiptsPath, legacyPath;
        private readonly object sync = new object();
        public LedgerStore(string dataDirectory, string legacyJsonPath = null)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("Data direktorij je obavezan.", nameof(dataDirectory));
            var root = Path.GetFullPath(dataDirectory);
            transactionsPath = Path.Combine(root, "transactions"); receiptsPath = Path.Combine(root, "resolutions");
            legacyPath = string.IsNullOrWhiteSpace(legacyJsonPath) ? null : Path.GetFullPath(legacyJsonPath);
        }
        private string TransactionPath(string id) { return Path.Combine(transactionsPath, LedgerJson.Key(id) + ".json"); }
        private string ReceiptPath(string id) { return Path.Combine(receiptsPath, LedgerJson.Key(id) + ".json"); }
        private LedgerEntry ReadEntry(string path)
        {
            var entry = LedgerJson.Read<LedgerEntry>(path);
            if (entry == null) throw new InvalidDataException("Prazan ledger zapis.");
            LedgerJson.Validate(entry.Transaction);
            if (!string.Equals(Path.GetFileName(path), LedgerJson.Key(entry.Transaction.Id) + ".json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Ledger ID ne odgovara nazivu fajla.");
            return entry;
        }
        public void Save(TransactionRecord transaction)
        {
            LedgerJson.Validate(transaction);
            if (transaction.Stage == TransactionStages.Resolved) throw new InvalidOperationException("Resolved se zapisuje samo kroz trajni resolution receipt.");
            lock (sync)
            {
                var path = TransactionPath(transaction.Id);
                var existing = File.Exists(path) ? ReadEntry(path) : null;
                if (existing?.Transaction.Stage == TransactionStages.Resolved)
                    throw new InvalidOperationException("Izmirena transakcija se ne može prepisati.");
                var copy = transaction.Copy(); copy.UpdatedUtc = DateTime.UtcNow;
                LedgerJson.AtomicWrite(path, new LedgerEntry { Transaction = copy, LegacyFingerprint = existing?.LegacyFingerprint }, true);
            }
        }
        private void MigrateLegacy()
        {
            if (legacyPath == null || !File.Exists(legacyPath)) return;
            foreach (var row in new JsonTransactionStore(legacyPath).ReadAll())
            {
                LedgerJson.Validate(row);
                var fingerprint = LedgerJson.Fingerprint(row); var path = TransactionPath(row.Id);
                if (!File.Exists(path)) LedgerJson.AtomicWrite(path, new LedgerEntry { Transaction = row.Copy(), LegacyFingerprint = fingerprint }, false);
                else
                {
                    var entry = ReadEntry(path);
                    if (entry.LegacyFingerprint != fingerprint && LedgerJson.Fingerprint(entry.Transaction) != fingerprint)
                        throw new InvalidDataException("Legacy/ledger konflikt za transakciju " + row.Id + ". Originalni journal je očuvan.");
                }
            }
        }
        public IReadOnlyList<TransactionRecord> ReadAll()
        {
            lock (sync)
            {
                MigrateLegacy(); RecoverReceipts();
                if (!Directory.Exists(transactionsPath)) return new TransactionRecord[0];
                var records = Directory.EnumerateFiles(transactionsPath, "*.json").Select(ReadEntry).Select(x => x.Transaction.Copy()).OrderBy(x => x.UpdatedUtc).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
                if (records.Any(x => x.Stage == TransactionStages.Resolved && !File.Exists(ReceiptPath(x.Id)))) throw new InvalidDataException("Resolved transakcija nema trajni resolution receipt.");
                return records;
            }
        }
        private ResolutionRecord ReadReceipt(string path)
        {
            var receipt = LedgerJson.Read<ResolutionRecord>(path); ValidateReceipt(receipt);
            if (!string.Equals(Path.GetFileName(path), LedgerJson.Key(receipt.TransactionId) + ".json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Receipt ID ne odgovara nazivu fajla.");
            return receipt;
        }
        private static void ValidateReceipt(ResolutionRecord receipt)
        {
            if (receipt == null || string.IsNullOrWhiteSpace(receipt.Id) || string.IsNullOrWhiteSpace(receipt.OperatorName) || string.IsNullOrWhiteSpace(receipt.Reason) || receipt.ResolvedUtc == default(DateTime)) throw new InvalidDataException("Nepotpun resolution receipt.");
            var original = receipt.Original; LedgerJson.Validate(original);
            if (receipt.TransactionId != original.Id || receipt.OriginalFingerprint != LedgerJson.Fingerprint(original) || TransactionStages.IsClosed(original.Stage) || receipt.VerifiedReceivedMinor < original.ReceivedMinor || receipt.VerifiedReceivedMinor % 1000 != 0 || receipt.VerifiedPaidMinor < original.PaidMinor || receipt.ManualPaidMinor < 0 || (long)receipt.VerifiedPaidMinor + receipt.ManualPaidMinor != receipt.VerifiedReceivedMinor)
                throw new InvalidDataException("Receipt nema potvrđeno potpuno izmirenje originalnog zapisa.");
        }
        private void RecoverReceipts()
        {
            if (!Directory.Exists(receiptsPath)) return;
            foreach (var path in Directory.EnumerateFiles(receiptsPath, "*.json")) ApplyReceipt(ReadReceipt(path));
        }
        private void ApplyReceipt(ResolutionRecord receipt)
        {
            var path = TransactionPath(receipt.TransactionId);
            if (!File.Exists(path)) throw new InvalidDataException("Receipt postoji bez originalne transakcije.");
            var entry = ReadEntry(path); var current = entry.Transaction;
            if (current.Stage == TransactionStages.Resolved)
            {
                var expected = receipt.Original; expected.Stage = TransactionStages.Resolved; expected.ResolutionId = receipt.Id; expected.UpdatedUtc = receipt.ResolvedUtc;
                if (LedgerJson.Fingerprint(current) != LedgerJson.Fingerprint(expected)) throw new InvalidDataException("Izmireni zapis je promijenjen nakon receipt-a.");
                return;
            }
            if (LedgerJson.Fingerprint(current) != receipt.OriginalFingerprint) throw new InvalidDataException("Receipt se odnosi na stariji zapis; noviji zapis nije prepisan.");
            var resolved = current.Copy(); resolved.Stage = TransactionStages.Resolved; resolved.ResolutionId = receipt.Id; resolved.UpdatedUtc = receipt.ResolvedUtc;
            // Original monetary evidence remains unchanged. Receipt holds the separately verified/manual amounts.
            LedgerJson.AtomicWrite(path, new LedgerEntry { Transaction = resolved, LegacyFingerprint = entry.LegacyFingerprint }, true);
        }
        public IReadOnlyList<ResolutionRecord> ReadResolutions()
        {
            lock (sync)
            {
                MigrateLegacy(); RecoverReceipts();
                return !Directory.Exists(receiptsPath) ? new ResolutionRecord[0] : Directory.EnumerateFiles(receiptsPath, "*.json").Select(ReadReceipt).Select(x => x.Copy()).OrderBy(x => x.ResolvedUtc).ToArray();
            }
        }
        internal ResolutionRecord Resolve(string id, string expectedFingerprint, string operatorName, string reason, int verifiedReceivedMinor, int verifiedPaidMinor, int manualPaidMinor)
        {
            lock (sync)
            {
                operatorName = operatorName?.Trim(); reason = reason?.Trim();
                ReadAll(); var receiptPath = ReceiptPath(id);
                if (File.Exists(receiptPath))
                {
                    var existing = ReadReceipt(receiptPath);
                    if (existing.OriginalFingerprint != expectedFingerprint || existing.VerifiedReceivedMinor != verifiedReceivedMinor || existing.VerifiedPaidMinor != verifiedPaidMinor || existing.ManualPaidMinor != manualPaidMinor || existing.OperatorName != operatorName || existing.Reason != reason) throw new InvalidOperationException("Izmirenje već postoji s drugim potvrđenim podacima.");
                    return existing.Copy();
                }
                var path = TransactionPath(id);
                if (!File.Exists(path)) throw new KeyNotFoundException("Transakcija nije pronađena.");
                var original = ReadEntry(path).Transaction;
                if (LedgerJson.Fingerprint(original) != expectedFingerprint) throw new InvalidOperationException("Fingerprint je zastario; ponovo pregledajte zapis.");
                if (TransactionStages.IsClosed(original.Stage) || verifiedReceivedMinor < original.ReceivedMinor || verifiedReceivedMinor % 1000 != 0 || verifiedPaidMinor < original.PaidMinor || manualPaidMinor < 0 || (long)verifiedPaidMinor + manualPaidMinor != verifiedReceivedMinor || string.IsNullOrWhiteSpace(operatorName) || string.IsNullOrWhiteSpace(reason))
                    throw new InvalidOperationException("Potvrdite identitet operatera, razlog i potpuno izmirenje; originalni potvrđeni novac se ne smije umanjiti.");
                var receipt = new ResolutionRecord(Guid.NewGuid().ToString("N"), original, expectedFingerprint, operatorName.Trim(), DateTime.UtcNow, reason.Trim(), verifiedReceivedMinor, verifiedPaidMinor, manualPaidMinor);
                ValidateReceipt(receipt);
                LedgerJson.AtomicWrite(receiptPath, receipt, false); // Receipt-first: no monetary side effects, safe recovery after any later crash.
                ApplyReceipt(receipt);
                return receipt.Copy();
            }
        }
    }

    [DataContract]
    public sealed class ResolutionRecord
    {
        [DataMember(Name = "Original")] private TransactionRecord original;
        [DataMember] public string Id { get; private set; }
        [DataMember] public string TransactionId { get; private set; }
        [DataMember] public string OriginalFingerprint { get; private set; }
        [DataMember] public string OperatorName { get; private set; }
        [DataMember] public DateTime ResolvedUtc { get; private set; }
        [DataMember] public string Reason { get; private set; }
        [DataMember] public int VerifiedReceivedMinor { get; private set; }
        [DataMember] public int VerifiedPaidMinor { get; private set; }
        [DataMember] public int ManualPaidMinor { get; private set; }
        public TransactionRecord Original { get { return original.Copy(); } }
        internal ResolutionRecord(string id, TransactionRecord source, string fingerprint, string operatorName, DateTime time, string reason, int verifiedReceivedMinor, int verifiedPaidMinor, int manualPaidMinor)
        { Id = id; original = source.Copy(); TransactionId = source.Id; OriginalFingerprint = fingerprint; OperatorName = operatorName; ResolvedUtc = time; Reason = reason; VerifiedReceivedMinor = verifiedReceivedMinor; VerifiedPaidMinor = verifiedPaidMinor; ManualPaidMinor = manualPaidMinor; }
        internal ResolutionRecord Copy() { return new ResolutionRecord(Id, original, OriginalFingerprint, OperatorName, ResolvedUtc, Reason, VerifiedReceivedMinor, VerifiedPaidMinor, ManualPaidMinor); }
    }
    public sealed class ResolutionService
    {
        private readonly LedgerStore store;
        public ResolutionService(LedgerStore ledgerStore) { store = ledgerStore ?? throw new ArgumentNullException(nameof(ledgerStore)); }
        public string GetFingerprint(string transactionId)
        {
            var row = store.ReadAll().SingleOrDefault(x => x.Id == transactionId);
            if (row == null) throw new KeyNotFoundException("Transakcija nije pronađena.");
            if (row.Stage == TransactionStages.Resolved) return store.ReadResolutions().Single(x => x.TransactionId == transactionId).OriginalFingerprint;
            return LedgerJson.Fingerprint(row);
        }
        public ResolutionRecord Resolve(string transactionId, string expectedFingerprint, string operatorName, string reason, int verifiedPaidMinor, int manualPaidMinor)
        {
            if (string.IsNullOrWhiteSpace(transactionId) || string.IsNullOrWhiteSpace(expectedFingerprint)) throw new ArgumentException("ID i očekivani fingerprint su obavezni.");
            var row = store.ReadAll().SingleOrDefault(x => x.Id == transactionId);
            if (row == null) throw new KeyNotFoundException("Transakcija nije pronađena.");
            if (row.ReceivedMinor <= 0) throw new InvalidOperationException("Nulti/nepoznati primitak zahtijeva eksplicitni verifiedReceivedMinor overload.");
            return store.Resolve(transactionId, expectedFingerprint, operatorName, reason, row.ReceivedMinor, verifiedPaidMinor, manualPaidMinor);
        }
        public ResolutionRecord Resolve(string transactionId, string expectedFingerprint, string operatorName, string reason, int verifiedReceivedMinor, int verifiedPaidMinor, int manualPaidMinor)
        {
            if (string.IsNullOrWhiteSpace(transactionId) || string.IsNullOrWhiteSpace(expectedFingerprint)) throw new ArgumentException("ID i očekivani fingerprint su obavezni.");
            return store.Resolve(transactionId, expectedFingerprint, operatorName, reason, verifiedReceivedMinor, verifiedPaidMinor, manualPaidMinor);
        }
        public IReadOnlyList<ResolutionRecord> ReadResolutions() { return store.ReadResolutions(); }
    }
}


