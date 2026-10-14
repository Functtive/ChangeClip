using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace ChangeClip.Backend
{
    public interface ITransactionStore
    {
        IReadOnlyList<TransactionRecord> ReadAll();
        void Save(TransactionRecord transaction);
    }

    [DataContract]
    public sealed class TransactionRecord
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string Stage { get; set; }
        [DataMember] public int AmountMinor { get; set; }
        [DataMember] public int ReceivedMinor { get; set; }
        [DataMember] public int PaidMinor { get; set; }
        [DataMember] public int CoinValueMinor { get; set; }
        [DataMember] public bool IsDebtExact { get; set; }
        [DataMember] public string CreditEventId { get; set; }
        [DataMember] public string Message { get; set; }
        [DataMember] public DateTime UpdatedUtc { get; set; }
        public TransactionRecord Copy() { return (TransactionRecord)MemberwiseClone(); }
    }

    // A complete, flushed replacement is installed atomically. Any damaged journal fails closed.
    public sealed class JsonTransactionStore : ITransactionStore
    {
        private readonly string path;
        private readonly object sync = new object();
        public JsonTransactionStore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("Journal putanja je obavezna.", nameof(filePath));
            path = Path.GetFullPath(filePath);
        }
        public IReadOnlyList<TransactionRecord> ReadAll()
        {
            lock (sync) return Read().Select(x => x.Copy()).ToArray();
        }
        private List<TransactionRecord> Read()
        {
            if (!File.Exists(path)) return new List<TransactionRecord>();
            using (var stream = File.OpenRead(path))
            {
                var records = (List<TransactionRecord>)Serializer().ReadObject(stream);
                if (records == null || records.Any(x => x == null || string.IsNullOrEmpty(x.Id) || string.IsNullOrEmpty(x.Stage) || x.ReceivedMinor < 0 || x.PaidMinor < 0 || x.PaidMinor > x.ReceivedMinor) || records.Select(x => x.Id).Distinct().Count() != records.Count)
                    throw new InvalidDataException("Neispravan journal; potrebna je ručna provjera.");
                return records;
            }
        }
        private static DataContractJsonSerializer Serializer() { return new DataContractJsonSerializer(typeof(List<TransactionRecord>)); }
        public void Save(TransactionRecord transaction)
        {
            if (transaction == null || string.IsNullOrEmpty(transaction.Id)) throw new ArgumentException("Transakcija mora imati ID.");
            lock (sync)
            {
                var records = Read();
                records.RemoveAll(x => x.Id == transaction.Id);
                var copy = transaction.Copy(); copy.UpdatedUtc = DateTime.UtcNow; records.Add(copy);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { Serializer().WriteObject(stream, records); stream.Flush(true); }
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
    }
}
