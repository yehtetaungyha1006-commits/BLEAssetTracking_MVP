using System;

namespace AssetTracking.Web.Models
{
    public class BeaconTelemetry
    {
        public long TelemetryId { get; set; }
        public int DeviceId { get; set; }
        public int Rssi { get; set; }
        public int BatteryLevel { get; set; }
        public double XAxis { get; set; }
        public double YAxis { get; set; }
        public double ZAxis { get; set; }
        public bool IsMoving { get; set; }
        public DateTime ReceiveTime { get; set; }

        // Navigation property
        public BeaconDevice? Device { get; set; }
    }
}
