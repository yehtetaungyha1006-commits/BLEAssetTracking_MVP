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

        private readonly ILogger<LiveTrackingController> _logger;

        public LiveTrackingController(AppDbContext context, ILogger<LiveTrackingController> logger)
        {
            _context = context;
            _logger = logger;
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
            var t1 = DateTime.UtcNow; // [T-LIVE-1] API request received
            var now = DateTime.Now;
            var cutoff30 = now.AddSeconds(-30);

            // 1. Load all registered BeaconDevices as base dataset
            var devices = await _context.BeaconDevices
                .AsNoTracking()
                .ToListAsync();

            var deviceIds = devices.Select(d => d.DeviceId).ToList();

            var t2 = DateTime.UtcNow; // [T-LIVE-2] Database query started

            // 2. Efficiently fetch latest telemetry IDs per registered device
            var latestTelemetryIds = await _context.BeaconTelemetries
                .AsNoTracking()
                .Where(t => deviceIds.Contains(t.DeviceId))
                .GroupBy(t => t.DeviceId)
                .Select(g => g.Max(t => t.TelemetryId))
                .ToListAsync();

            // 3. Fetch latest telemetry records by Primary Key ID
            var latestTelemetries = await _context.BeaconTelemetries
                .AsNoTracking()
                .Where(t => latestTelemetryIds.Contains(t.TelemetryId))
                .ToListAsync();

            // 4. Fetch associated scanners
            var scannerIds = latestTelemetries
                .Where(t => !string.IsNullOrEmpty(t.ScannerId))
                .Select(t => t.ScannerId!)
                .Distinct()
                .ToList();

            var scanners = await _context.Scanners
                .AsNoTracking()
                .Where(s => scannerIds.Contains(s.ScannerId))
                .ToDictionaryAsync(s => s.ScannerId, s => s);

            var t3 = DateTime.UtcNow; // [T-LIVE-3] Database query completed

            var telemetryDict = latestTelemetries.ToDictionary(t => t.DeviceId);

            // 5. DTO Mapping starting from all registered devices
            var data = devices.Select(d =>
            {
                telemetryDict.TryGetValue(d.DeviceId, out var lt);

                bool hasLatestTelemetry = lt != null;
                DateTime? telemetryTime = hasLatestTelemetry ? AssetTracking.Web.Helpers.DateTimeHelper.EnsureLocal(lt!.ReceiveTime) : null;

                string status = "Offline";
                if (hasLatestTelemetry && telemetryTime.HasValue && telemetryTime.Value >= cutoff30)
                {
                    status = "Online";
                }

                double? estimatedDistance = null;
                if (status != "Offline" && lt?.Rssi != null)
                {
                    estimatedDistance = AssetTracking.Web.Helpers.DistanceHelper.EstimateDistanceMeters(lt.Rssi);
                }

                ScannerDevice? sc = null;
                if (lt != null && !string.IsNullOrEmpty(lt.ScannerId))
                {
                    scanners.TryGetValue(lt.ScannerId, out sc);
                }

                return new
                {
                    deviceId = d.DeviceId,
                    deviceName = !string.IsNullOrWhiteSpace(d.DeviceName) ? d.DeviceName : "Unnamed Beacon",
                    macAddress = d.MacAddress,
                    status = status,
                    isMoving = hasLatestTelemetry && lt!.IsMoving,
                    scannerId = hasLatestTelemetry ? (lt!.ScannerId ?? "-") : "-",
                    scannerName = sc?.ScannerName ?? (lt?.ScannerId ?? "-"),
                    building = sc?.Building ?? "-",
                    floor = sc?.Floor ?? "-",
                    location = sc?.Location ?? "-",
                    rssi = hasLatestTelemetry ? lt!.Rssi : 0,
                    estimatedDistance = estimatedDistance,
                    battery = hasLatestTelemetry ? lt!.BatteryLevel : 0,
                    lastSeen = AssetTracking.Web.Helpers.DateTimeHelper.FormatLastSeen(d.LastSeen),
                    rawLastSeen = d.LastSeen
                };
            }).ToList();

            var t4 = DateTime.UtcNow; // [T-LIVE-4] DTO mapping completed
            var dbMs = (t3 - t2).TotalMilliseconds;
            var totalMs = (t4 - t1).TotalMilliseconds;

            _logger.LogInformation("[T-LIVE-5] LiveTracking API response prepared | Devices: {DeviceCount} | Telemetries: {TelCount} | DbMs: {DbMs:F1}ms | TotalMs: {TotalMs:F1}ms",
                devices.Count, latestTelemetries.Count, dbMs, totalMs);

            return Json(data);
        }
    }
}
