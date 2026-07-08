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
        private readonly IConfiguration _configuration;

        public LiveTrackingController(AppDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
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
            int offlineTimeout = _configuration.GetValue<int?>("ScannerSettings:OfflineTimeoutSeconds") ?? 30;
            var cutoff30 = now.AddSeconds(-offlineTimeout);
            var beacons = await _context.BeaconDevices
                .Include(b => b.Telemetries)
                    .ThenInclude(t => t.Scanner)
                .AsNoTracking()
                .ToListAsync();

            var data = beacons.Select(b => {
                // Find recent telemetries within the last 30 seconds
                var recentTelemetries = b.Telemetries
                    .Where(t => t.ReceiveTime >= cutoff30)
                    .ToList();

                BeaconTelemetry? selectedTelemetry = null;
                string status = "Offline";

                if (recentTelemetries.Any())
                {
                    // Select the telemetry with the highest RSSI
                    selectedTelemetry = recentTelemetries.OrderByDescending(t => t.Rssi).First();
                    status = "Online";
                }
                else
                {
                    // Fall back to absolute latest telemetry for last known location, but status is Offline
                    selectedTelemetry = b.Telemetries.OrderByDescending(t => t.ReceiveTime).FirstOrDefault();
                    status = "Offline";
                }

                return new {
                    deviceName = b.DeviceName ?? "Unnamed Beacon",
                    macAddress = b.MacAddress,
                    status = status,
                    scannerName = selectedTelemetry?.Scanner?.ScannerName ?? "-",
                    building = selectedTelemetry?.Scanner?.Building ?? "-",
                    floor = selectedTelemetry?.Scanner?.Floor ?? "-",
                    location = selectedTelemetry?.Scanner?.Location ?? "-",
                    rssi = selectedTelemetry?.Rssi ?? 0,
                    battery = selectedTelemetry?.BatteryLevel ?? 0,
                    lastSeen = b.LastSeen?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Never"
                };
            }).ToList();

            return Json(data);
        }
    }
}
