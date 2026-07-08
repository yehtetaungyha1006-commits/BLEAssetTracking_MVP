using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AssetTracking.Web.Data;
using AssetTracking.Web.Models;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace AssetTracking.Web.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DashboardApiController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _configuration;

        public DashboardApiController(AppDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        [HttpGet("/api/dashboard")]
        public async Task<IActionResult> GetDashboard()
        {
            try
            {
                // Fetch all devices including their telemetries (excluding demo devices)
                var devices = await _context.BeaconDevices
                    .Include(d => d.Telemetries)
                        .ThenInclude(t => t.Scanner)
                    .Where(d => !d.MacAddress.StartsWith("00:11:22:33:44"))
                    .ToListAsync();

                var now = DateTime.Now;
                int offlineTimeout = _configuration.GetValue<int?>("ScannerSettings:OfflineTimeoutSeconds") ?? 30;
                var cutoff30 = now.AddSeconds(-offlineTimeout);

                int onlineDevices = 0;
                int offlineDevices = 0;
                int movingDevices = 0;
                int lowBatteryDevices = 0;

                var deviceData = devices.Select(device =>
                {
                    // Find recent telemetries within the last 30 seconds
                    var recentTelemetries = device.Telemetries
                        .Where(t => t.ReceiveTime >= cutoff30)
                        .ToList();

                    BeaconTelemetry? selectedTelemetry = null;
                    string status = "Offline";

                    if (recentTelemetries.Any())
                    {
                        // Select the telemetry with the highest RSSI
                        selectedTelemetry = recentTelemetries.OrderByDescending(t => t.Rssi).First();
                        status = selectedTelemetry.IsMoving ? "Moving" : "Online";
                        onlineDevices++;
                    }
                    else
                    {
                        // Fall back to the absolute latest telemetry for last known location, but status is Offline
                        selectedTelemetry = device.Telemetries.OrderByDescending(t => t.ReceiveTime).FirstOrDefault();
                        status = "Offline";
                        offlineDevices++;
                    }

                    bool isMoving = selectedTelemetry != null && selectedTelemetry.IsMoving;

                    // Count moving and low battery devices from their selected telemetry
                    if (selectedTelemetry != null)
                    {
                        if (isMoving && status != "Offline")
                        {
                            movingDevices++;
                        }
                        if (selectedTelemetry.BatteryLevel < 20)
                        {
                            lowBatteryDevices++;
                        }
                    }

                    return new
                    {
                        macAddress = device.MacAddress,
                        deviceName = device.DeviceName,
                        rssi = selectedTelemetry?.Rssi ?? 0,
                        batteryLevel = selectedTelemetry?.BatteryLevel ?? 0,
                        xAxis = selectedTelemetry?.XAxis ?? 0.0,
                        yAxis = selectedTelemetry?.YAxis ?? 0.0,
                        zAxis = selectedTelemetry?.ZAxis ?? 0.0,
                        isMoving = isMoving,
                        status = status,
                        lastSeen = device.LastSeen,
                        scannerId = selectedTelemetry?.Scanner?.ScannerId,
                        scannerName = selectedTelemetry?.Scanner?.ScannerName,
                        building = selectedTelemetry?.Scanner?.Building,
                        floor = selectedTelemetry?.Scanner?.Floor,
                        location = selectedTelemetry?.Scanner?.Location
                    };
                }).ToList();

                var summary = new
                {
                    onlineDevices,
                    offlineDevices,
                    movingDevices,
                    lowBatteryDevices
                };

                return Ok(new
                {
                    summary,
                    devices = deviceData
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Internal server error occurred", message = ex.Message });
            }
        }
    }
}
