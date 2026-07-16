using System;

namespace AssetTracking.Web.Helpers
{
    public static class DistanceHelper
    {
        public static double? EstimateDistanceMeters(int rssi)
        {
            if (rssi == 0)
            {
                return null;
            }

            double txPower = -59.0;
            double pathLossExponent = 2.5;
            double distance = Math.Pow(10, (txPower - rssi) / (10.0 * pathLossExponent));
            return Math.Round(distance, 1);
        }

        public static double? EstimateDistanceMeters(int? rssi)
        {
            if (!rssi.HasValue)
            {
                return null;
            }
            return EstimateDistanceMeters(rssi.Value);
        }
    }
}
