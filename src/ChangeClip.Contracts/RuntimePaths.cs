using System;
using System.IO;
using System.Runtime.Serialization.Json;

namespace ChangeClip.Contracts
{
    public sealed class RuntimePaths
    {
        public RuntimePaths(string root = null)
        {
            Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChangeClip"));
        }
        public string Root { get; private set; }
        public string HardwareConfig { get { return Path.Combine(Root, "device.hardware.json"); } }
        public string HardwareData { get { return Path.Combine(Root, "hardware", "data"); } }
        public string Ledger { get { return Path.Combine(HardwareData, "ledger-v3"); } }
        public string LegacyJournal { get { return Path.Combine(HardwareData, "transactions.json"); } }
    }
    public static class DeviceConfiguration
    {
        public static DeviceOptions Read(string path)
        {
            using (var input = File.OpenRead(path))
                return (DeviceOptions)new DataContractJsonSerializer(typeof(DeviceOptions)).ReadObject(input);
        }
        public static void WriteHardware(string path, DeviceOptions options)
        {
            DeviceOptionsValidator.Validate(options, requireHardware: true);
            var absolute = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute));
            string temporary = absolute + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { new DataContractJsonSerializer(typeof(DeviceOptions)).WriteObject(output, options); output.Flush(true); }
                if (File.Exists(absolute)) File.Replace(temporary, absolute, null); else File.Move(temporary, absolute);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
