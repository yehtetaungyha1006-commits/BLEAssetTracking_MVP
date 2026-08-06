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

        public DashboardApiController(AppDbContext context)
        {
            _context = context;
        }

        [HttpGet("/api/dashboard")]
        public async Task<IActionResult> GetDashboard()
        {
            try
            {
                var now = DateTime.Now;
                var cutoff30 = now.AddSeconds(-30);

                // 1. Fetch devices (excluding demo devices)
                var devices = await _context.BeaconDevices
                    .AsNoTracking()
                    .Where(d => !d.MacAddress.StartsWith("00:11:22:33:44"))
                    .ToListAsync();

                var deviceIds = devices.Select(d => d.DeviceId).ToList();

                // 2. Fetch recent telemetries (last 30 seconds) for these devices
                var recentTelemetries = await _context.BeaconTelemetries
                    .AsNoTracking()
                    .Where(t => deviceIds.Contains(t.DeviceId) && t.ReceiveTime >= cutoff30)
                    .Select(t => new
                    {
                        t.DeviceId,
                        t.TelemetryId,
                        t.Rssi,
                        t.BatteryLevel,
                        t.XAxis,
                        t.YAxis,
                        t.ZAxis,
                        t.IsMoving,
                        t.ReceiveTime,
                        t.ScannerId,
                        Scanner = t.Scanner == null ? null : new
                        {
                            t.Scanner.ScannerId,
                            t.Scanner.ScannerName,
                            t.Scanner.Building,
                            t.Scanner.Floor,
                            t.Scanner.Location
                        }
                    })
                    .ToListAsync();

                // 3. Fetch latest telemetry IDs per device
                var latestTelemetryIdsQuery = _context.BeaconTelemetries
                    .AsNoTracking()
                    .Where(t => deviceIds.Contains(t.DeviceId))
                    .GroupBy(t => t.DeviceId)
                    .Select(g => g.Max(t => t.TelemetryId));

                // 4. Fetch the latest telemetry records
                var latestTelemetries = await _context.BeaconTelemetries
                    .AsNoTracking()
                    .Where(t => latestTelemetryIdsQuery.Contains(t.TelemetryId))
                    .Select(t => new
                    {
                        t.DeviceId,
                        t.TelemetryId,
                        t.Rssi,
                        t.BatteryLevel,
                        t.XAxis,
                        t.YAxis,
                        t.ZAxis,
                        t.IsMoving,
                        t.ReceiveTime,
                        t.ScannerId,
                        Scanner = t.Scanner == null ? null : new
                        {
                            t.Scanner.ScannerId,
                            t.Scanner.ScannerName,
                            t.Scanner.Building,
                            t.Scanner.Floor,
                            t.Scanner.Location
                        }
                    })
                    .ToListAsync();

                // Group recent telemetries by DeviceId and pick the one with highest RSSI
                var recentGrouped = recentTelemetries
                    .GroupBy(t => t.DeviceId)
                    .ToDictionary(
                        g => g.Key,
                        g => g.OrderByDescending(t => t.Rssi).First()
                    );

                // Dictionary of latest telemetries
                var latestDict = latestTelemetries
                    .ToDictionary(t => t.DeviceId);

                int onlineDevices = 0;
                int offlineDevices = 0;
                int movingDevices = 0;
                int lowBatteryDevices = 0;

                var deviceData = devices.Select(device =>
                {
                    // Look up recent telemetry
                    recentGrouped.TryGetValue(device.DeviceId, out var selectedTelemetry);
                    string status = "Offline";

                    if (selectedTelemetry != null)
                    {
                        status = selectedTelemetry.IsMoving ? "Moving" : "Online";
                        onlineDevices++;
                    }
                    else
                    {
                        // Fall back to latest telemetry
                        latestDict.TryGetValue(device.DeviceId, out selectedTelemetry);
                        status = "Offline";
                        offlineDevices++;
                    }

                    bool isMoving = selectedTelemetry != null && selectedTelemetry.IsMoving;

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

                    double? estimatedDistance = null;
                    if (status != "Offline" && selectedTelemetry != null)
                    {
                        estimatedDistance = AssetTracking.Web.Helpers.DistanceHelper.EstimateDistanceMeters(selectedTelemetry.Rssi);
                    }

                    return new
                    {
                        macAddress = device.MacAddress,
                        deviceName = device.DeviceName,
                        rssi = selectedTelemetry?.Rssi ?? 0,
                        estimatedDistance = estimatedDistance,
                        batteryLevel = selectedTelemetry?.BatteryLevel ?? 0,
                        xAxis = selectedTelemetry?.XAxis ?? 0.0,
                        yAxis = selectedTelemetry?.YAxis ?? 0.0,
                        zAxis = selectedTelemetry?.ZAxis ?? 0.0,
                        isMoving = isMoving,
                        status = status,
                        lastSeen = device.LastSeen.HasValue ? AssetTracking.Web.Helpers.DateTimeHelper.EnsureLocal(device.LastSeen.Value) : (DateTime?)null,
                        lastSeenFormatted = AssetTracking.Web.Helpers.DateTimeHelper.FormatLastSeen(device.LastSeen),
                        scannerId = selectedTelemetry?.ScannerId,
                        building = selectedTelemetry?.Scanner?.Building,
                        floor = selectedTelemetry?.Scanner?.Floor,
                        location = selectedTelemetry?.Scanner?.Location
                    };
                }).ToList();

                var activeAlerts = await _context.AlertLogs
                    .Where(a => !a.IsResolved)
                    .ToListAsync();

                var alertsSummary = new
                {
                    critical = activeAlerts.Count(a => string.Equals(a.Severity, "Critical", StringComparison.OrdinalIgnoreCase)),
                    warning = activeAlerts.Count(a => string.Equals(a.Severity, "Warning", StringComparison.OrdinalIgnoreCase)),
                    info = activeAlerts.Count(a => string.Equals(a.Severity, "Info", StringComparison.OrdinalIgnoreCase)),
                    active = activeAlerts.Count
                };

                // Get recent 5 alerts
                var recentAlerts = await _context.AlertLogs
                    .Include(a => a.Device)
                    .OrderByDescending(a => a.AlertTime)
                    .Take(5)
                    .ToListAsync();

                var recentAlertsData = recentAlerts.Select(a => new
                {
                    deviceName = a.Device?.DeviceName ?? a.Device?.MacAddress ?? "Unknown Device",
                    alertType = a.AlertType,
                    relativeTime = AssetTracking.Web.Helpers.DateTimeHelper.FormatLastSeen(a.AlertTime)
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
                    devices = deviceData,
                    alertsSummary,
                    recentAlerts = recentAlertsData
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Internal server error occurred", message = ex.Message });
            }
        }
    }
}
