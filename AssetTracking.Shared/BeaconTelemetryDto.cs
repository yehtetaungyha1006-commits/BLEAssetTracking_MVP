using System;

namespace AssetTracking.Shared
{
    public class BeaconTelemetryDto
    {
        public string MacAddress { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public int Rssi { get; set; }
        public int BatteryLevel { get; set; }
        public double XAxis { get; set; }
        public double YAxis { get; set; }
        public double ZAxis { get; set; }
        public bool IsMoving { get; set; }
        public DateTime ReceiveTime { get; set; }
    }
}
