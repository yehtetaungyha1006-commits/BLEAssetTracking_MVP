using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AssetTracking.Web.Data;
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

        public DashboardApiController(AppDbContext context)
        {
            _context = context;
        }

        [HttpGet("/api/dashboard")]
        public async Task<IActionResult> GetDashboard()
        {
            try
            {
                // Fetch all devices including their telemetries (excluding demo devices)
                var devices = await _context.BeaconDevices
                    .Include(d => d.Telemetries)
                    .Where(d => !d.MacAddress.StartsWith("00:11:22:33:44"))
                    .ToListAsync();

                var now = DateTime.Now;
                var onlineCutoff = now.AddSeconds(-10);
                var idleCutoff = now.AddSeconds(-30);

                int onlineDevices = 0;
                int offlineDevices = 0;
                int movingDevices = 0;
                int lowBatteryDevices = 0;

                var deviceData = devices.Select(device =>
                {
                    // Find latest telemetry by ReceiveTime descending
                    var latestTelemetry = device.Telemetries
                        .OrderByDescending(t => t.ReceiveTime)
                        .FirstOrDefault();

                    // Calculate online/idle/offline status dynamically from LastSeen and DateTime.Now
                    string status = "Offline";
                    bool isMoving = latestTelemetry != null && latestTelemetry.IsMoving;

                    if (device.LastSeen.HasValue)
                    {
                        var lastSeenVal = device.LastSeen.Value;
                        if (lastSeenVal >= onlineCutoff)
                        {
                            status = isMoving ? "Moving" : "Online";
                            onlineDevices++;
                        }
                        else if (lastSeenVal >= idleCutoff)
                        {
                            status = "Idle";
                            offlineDevices++;
                        }
                        else
                        {
                            status = "Offline";
                            offlineDevices++;
                        }
                    }
                    else
                    {
                        offlineDevices++;
                    }

                    // Count moving and low battery devices from their latest telemetry
                    if (latestTelemetry != null)
                    {
                        if (isMoving && device.LastSeen.HasValue && device.LastSeen.Value >= onlineCutoff)
                        {
                            movingDevices++;
                        }
                        if (latestTelemetry.BatteryLevel < 20)
                        {
                            lowBatteryDevices++;
                        }
                    }

                    return new
                    {
                        macAddress = device.MacAddress,
                        deviceName = device.DeviceName,
                        location = device.Location,
                        rssi = latestTelemetry?.Rssi ?? 0,
                        batteryLevel = latestTelemetry?.BatteryLevel ?? 0,
                        xAxis = latestTelemetry?.XAxis ?? 0.0,
                        yAxis = latestTelemetry?.YAxis ?? 0.0,
                        zAxis = latestTelemetry?.ZAxis ?? 0.0,
                        isMoving = isMoving,
                        status = status,
                        lastSeen = device.LastSeen
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
