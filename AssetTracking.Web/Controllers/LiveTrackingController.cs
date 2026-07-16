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
            var cutoff30 = now.AddSeconds(-30);
            var beacons = await _context.BeaconDevices
                .Include(b => b.Telemetries)
                    .ThenInclude(t => t.Scanner)
                .AsNoTracking()
                .ToListAsync();

            var data = beacons.Select(b => {
                // Find recent telemetries within the last 30 seconds
                var recentTelemetries = b.Telemetries
                    .Where(t => AssetTracking.Web.Helpers.DateTimeHelper.EnsureLocal(t.ReceiveTime) >= cutoff30)
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
                    selectedTelemetry = b.Telemetries.OrderByDescending(t => AssetTracking.Web.Helpers.DateTimeHelper.EnsureLocal(t.ReceiveTime)).FirstOrDefault();
                    status = "Offline";
                }

                double? estimatedDistance = null;
                if (status != "Offline" && selectedTelemetry != null)
                {
                    estimatedDistance = AssetTracking.Web.Helpers.DistanceHelper.EstimateDistanceMeters(selectedTelemetry.Rssi);
                }

                return new {
                    deviceName = b.DeviceName ?? "Unnamed Beacon",
                    macAddress = b.MacAddress,
                    status = status,
                    isMoving = selectedTelemetry?.IsMoving ?? false,
                    scannerId = selectedTelemetry?.ScannerId ?? "-",
                    building = selectedTelemetry?.Scanner?.Building ?? "-",
                    floor = selectedTelemetry?.Scanner?.Floor ?? "-",
                    location = selectedTelemetry?.Scanner?.Location ?? "-",
                    rssi = selectedTelemetry?.Rssi ?? 0,
                    estimatedDistance = estimatedDistance,
                    battery = selectedTelemetry?.BatteryLevel ?? 0,
                    lastSeen = AssetTracking.Web.Helpers.DateTimeHelper.FormatLastSeen(b.LastSeen)
                };
            }).ToList();

            return Json(data);
        }
    }
}
