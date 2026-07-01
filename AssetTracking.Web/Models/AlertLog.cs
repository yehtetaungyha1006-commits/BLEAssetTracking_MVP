using System;

namespace AssetTracking.Web.Models
{
    public class AlertLog
    {
        public long AlertId { get; set; }
        public int DeviceId { get; set; }
        public string AlertType { get; set; } = string.Empty;
        public string AlertMessage { get; set; } = string.Empty;
        public DateTime AlertTime { get; set; }

        // Navigation property
        public BeaconDevice? Device { get; set; }
    }
}
