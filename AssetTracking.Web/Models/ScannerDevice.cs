using System;
using System.Collections.Generic;

namespace AssetTracking.Web.Models
{
    public class ScannerDevice
    {
        public string ScannerId { get; set; } = string.Empty;
        public string ScannerName { get; set; } = string.Empty;
        public string Building { get; set; } = string.Empty;
        public string Floor { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime? LastSeen { get; set; }
        public DateTime CreatedAt { get; set; }

        // Navigation property
        public ICollection<BeaconTelemetry> Telemetries { get; set; } = new List<BeaconTelemetry>();
    }
}
