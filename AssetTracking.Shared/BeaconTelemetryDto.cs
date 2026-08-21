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
        public DateTime? ObservedAt { get; set; }
        public DateTime? SentAt { get; set; }
        public bool IsFreshObservation { get; set; } = true;
        public bool IsHeartbeat { get; set; } = false;
        public double ObservationAgeMs { get; set; }

        public int Major { get; set; }
        public int Minor { get; set; }

        public string? ScannerId { get; set; }
        public string? ScannerName { get; set; }
        public string? ScannerBuilding { get; set; }
        public string? ScannerFloor { get; set; }
        public string? ScannerLocation { get; set; }
    }
}
