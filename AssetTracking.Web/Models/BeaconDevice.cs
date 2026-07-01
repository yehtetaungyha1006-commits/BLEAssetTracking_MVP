using System;
using System.Collections.Generic;

namespace AssetTracking.Web.Models
{
    public class BeaconDevice
    {
        public int DeviceId { get; set; }
        public string MacAddress { get; set; } = string.Empty;
        public string? DeviceName { get; set; }
        public string? Location { get; set; }
        public string? Status { get; set; }
        public DateTime? LastSeen { get; set; }
        public DateTime CreatedAt { get; set; }

        // Navigation properties
        public ICollection<BeaconTelemetry> Telemetries { get; set; } = new List<BeaconTelemetry>();
        public ICollection<AlertLog> Alerts { get; set; } = new List<AlertLog>();
    }
}
