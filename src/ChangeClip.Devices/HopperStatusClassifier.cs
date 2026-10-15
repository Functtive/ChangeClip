using System;
using ChangeClip.Contracts;

namespace ChangeClip.Devices
{
    public static class HopperStatusClassifier
    {
        // Pure readiness classification; sensors never establish a known quantity of payout stock.
        public static HopperStatus Classify(HopperPaymentStatus status, bool countUncertain = false, bool connected = true)
        {
            if (!connected) return new HopperStatus(false, false, null, "Hopper nije spojen.");
            if (status == null) throw new ArgumentNullException(nameof(status));
            string fault = countUncertain ? "Prethodna isplata je nejasna; potrebno usklađivanje."
                : status.Remaining > 0 ? "Hopper ima aktivnu prethodnu isplatu; potrebno usklađivanje."
                : status.UnpaidLast > 0 ? "Prethodna isplata nije potpuna." : null;
            return new HopperStatus(true, false, null, fault);
        }
    }
}
