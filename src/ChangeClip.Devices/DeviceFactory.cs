using System;
using ChangeClip.Contracts;

namespace ChangeClip.Devices
{
    public static class DeviceFactory
    {
        // Reports the compiled adapter capability; never probes USB or guesses from a file on disk.
        public static bool Nv9RuntimeAvailable
        {
            get
            {
#if ITL_SDK
                return true;
#else
                return false;
#endif
            }
        }
        public static bool SimulatorAvailable
        {
            get
            {
#if HARDWARE_RELEASE
                return false;
#else
                return true;
#endif
            }
        }
        public static DeviceBundle Create(DeviceOptions options)
        {
#if HARDWARE_RELEASE
            DeviceOptionsValidator.Validate(options, requireHardware: true);
#else
            DeviceOptionsValidator.Validate(options);
            if (string.Equals(options.Mode, "Simulator", StringComparison.OrdinalIgnoreCase))
            {
                var bill = new SimulatedBillAcceptor();
                var hopper = new SimulatedCoinHopper(options.SimulatorCoins);
                return new DeviceBundle(bill, hopper, new SimulatorControls(bill, hopper));
            }
#endif
            return new DeviceBundle(new Nv9BillAcceptor(options), new AzkoyenCoinHopper(options));
        }
    }
}
