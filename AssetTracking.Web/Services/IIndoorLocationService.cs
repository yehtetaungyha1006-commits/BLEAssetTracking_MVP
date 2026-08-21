using System;
using System.Threading;
using System.Threading.Tasks;

namespace AssetTracking.Web.Services
{
    public interface IIndoorLocationService
    {
        Task<BeaconLocationResult> DetermineCurrentLocationAsync(
            int deviceId,
            CancellationToken cancellationToken = default);

        Task<BeaconLocationResult> RecordTelemetryAndDetermineLocationAsync(
            int deviceId,
            string? scannerId,
            int rssi,
            DateTime receiveTime,
            bool isFreshObservation = true,
            double observationAgeMs = 0,
            CancellationToken cancellationToken = default);
    }
}
