using System.Threading;
using System.Threading.Tasks;

namespace AssetTracking.Web.Services
{
    public interface IIndoorLocationService
    {
        Task<BeaconLocationResult> DetermineCurrentLocationAsync(
            int deviceId,
            CancellationToken cancellationToken = default);
    }
}
