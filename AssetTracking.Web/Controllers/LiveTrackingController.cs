using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AssetTracking.Web.Data;
using AssetTracking.Web.Models;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace AssetTracking.Web.Controllers
{
    [Route("LiveTracking")]
    public class LiveTrackingController : Controller
    {
        private readonly AppDbContext _context;

        public LiveTrackingController(AppDbContext context)
        {
            _context = context;
        }

        // GET: /LiveTracking
        [HttpGet("")]
        public IActionResult Index()
        {
            return View();
        }

        // GET: /LiveTracking/Data
        [HttpGet("Data")]
        public async Task<IActionResult> GetLiveData()
        {
            var now = DateTime.Now;
            var cutoff30 = now.AddSeconds(-30);

            var latestTelemetryIdsQuery = _context.BeaconTelemetries
                .AsNoTracking()
                .GroupBy(t => t.DeviceId)
                .Select(g => g.Max(t => t.TelemetryId));

            var latestTelemetriesQuery = _context.BeaconTelemetries
                .AsNoTracking()
                .Where(t => latestTelemetryIdsQuery.Contains(t.TelemetryId));

            var rawData = await (from b in _context.BeaconDevices.AsNoTracking()
                                 join t in latestTelemetriesQuery on b.DeviceId equals t.DeviceId into tGroup
                                 from lt in tGroup.DefaultIfEmpty()
                                 join s in _context.Scanners.AsNoTracking() on lt.ScannerId equals s.ScannerId into sGroup
                                 from sc in sGroup.DefaultIfEmpty()
                                 select new
                                 {
                                     b.DeviceId,
                                     b.DeviceName,
                                     b.MacAddress,
                                     b.LastSeen,
                                     LatestTelemetryId = (long?)lt.TelemetryId,
                                     Rssi = (int?)lt.Rssi,
                                     BatteryLevel = (int?)lt.BatteryLevel,
                                     IsMoving = (bool?)lt.IsMoving,
                                     ReceiveTime = (DateTime?)lt.ReceiveTime,
                                     ScannerId = lt.ScannerId,
                                     ScannerName = sc.ScannerName,
                                     Building = sc.Building,
                                     Floor = sc.Floor,
                                     Location = sc.Location
                                 })
                                 .ToListAsync();

            var data = rawData.Select(d => {
                var hasLatestTelemetry = d.LatestTelemetryId.HasValue;
                DateTime? telemetryTime = hasLatestTelemetry ? AssetTracking.Web.Helpers.DateTimeHelper.EnsureLocal(d.ReceiveTime!.Value) : null;
                
                string status = "Offline";
                if (hasLatestTelemetry && telemetryTime.HasValue && telemetryTime.Value >= cutoff30)
                {
                    status = "Online";
                }

                double? estimatedDistance = null;
                if (status != "Offline" && d.Rssi.HasValue)
                {
                    estimatedDistance = AssetTracking.Web.Helpers.DistanceHelper.EstimateDistanceMeters(d.Rssi.Value);
                }

                return new {
                    deviceName = d.DeviceName ?? "Unnamed Beacon",
                    macAddress = d.MacAddress,
                    status = status,
                    isMoving = hasLatestTelemetry && d.IsMoving == true,
                    scannerId = hasLatestTelemetry ? (d.ScannerId ?? "-") : "-",
                    building = hasLatestTelemetry ? (d.Building ?? "-") : "-",
                    floor = hasLatestTelemetry ? (d.Floor ?? "-") : "-",
                    location = hasLatestTelemetry ? (d.Location ?? "-") : "-",
                    rssi = hasLatestTelemetry ? (d.Rssi ?? 0) : 0,
                    estimatedDistance = estimatedDistance,
                    battery = hasLatestTelemetry ? (d.BatteryLevel ?? 0) : 0,
                    lastSeen = AssetTracking.Web.Helpers.DateTimeHelper.FormatLastSeen(d.LastSeen),
                    rawLastSeen = d.LastSeen
                };
            }).ToList();

            return Json(data);
        }
    }
}
