using System;

namespace AssetTracking.Web.Models
{
    public class AlertLog
    {
        public long AlertId { get; set; }
        public int? DeviceId { get; set; }
        public string AlertType { get; set; } = string.Empty;
        public string AlertMessage { get; set; } = string.Empty;
        public DateTime AlertTime { get; set; }
        
        // New columns for Sprint 7.3 Smart Alert Engine
        public bool IsResolved { get; set; } = false;
        public DateTime? ResolvedAt { get; set; }
        public string Severity { get; set; } = "Info"; // Info, Warning, Critical
        public string? ScannerId { get; set; }

        // Navigation properties
        public BeaconDevice? Device { get; set; }
        public ScannerDevice? Scanner { get; set; }
    }
}
