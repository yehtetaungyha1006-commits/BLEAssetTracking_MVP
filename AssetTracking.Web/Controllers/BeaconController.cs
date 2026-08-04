using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using AssetTracking.Shared;
using AssetTracking.Web.Data;
using AssetTracking.Web.Models;
using AssetTracking.Web.Hubs;
using System;
using System.Threading.Tasks;

using AssetTracking.Web.Services;

namespace AssetTracking.Web.Controllers
{
    [ApiController]
    [Route("api/beacon")]
    public class BeaconController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IHubContext<BeaconHub> _hubContext;
        private readonly ILogger<BeaconController> _logger;
        private readonly AlertEngine _alertEngine;
        private readonly IConfiguration _configuration;
        private readonly IIndoorLocationService _indoorLocationService;

        public BeaconController(
            AppDbContext context,
            IHubContext<BeaconHub> hubContext,
            ILogger<BeaconController> logger,
            AlertEngine alertEngine,
            IConfiguration configuration,
            IIndoorLocationService indoorLocationService)
        {
            _context = context;
            _hubContext = hubContext;
            _logger = logger;
            _alertEngine = alertEngine;
            _configuration = configuration;
            _indoorLocationService = indoorLocationService;
        }

        [HttpPost("telemetry")]
        public async Task<IActionResult> PostTelemetry([FromBody] BeaconTelemetryDto telemetryDto)
        {
            if (telemetryDto == null)
            {
                return BadRequest("Telemetry is null");
            }

            try
            {
                // Find registered BeaconDevice by Major + Minor
                var device = await _context.BeaconDevices
                    .FirstOrDefaultAsync(d => d.Major == telemetryDto.Major && d.Minor == telemetryDto.Minor);

                if (device == null)
                {
                    // Trigger Unknown Beacon Alert
                    await _alertEngine.ProcessUnknownBeaconAlertAsync(telemetryDto.Major, telemetryDto.Minor, telemetryDto.Rssi);

                    // Ignore telemetry, return unregistered message, and log details for debugging
                    _logger.LogWarning("Unregistered beacon ignored: Major={Major}, Minor={Minor}, RSSI={RSSI}", 
                        telemetryDto.Major, telemetryDto.Minor, telemetryDto.Rssi);
                    return Ok(new { status = "Ignored", message = "Unregistered beacon ignored" });
                }

                // Update registered device fields
                device.Status = "Online";
                device.LastSeen = DateTime.Now;

                // Save changes
                await _context.SaveChangesAsync();

                // Process Scanner Auto-registration / Update
                ScannerDevice? scanner = null;
                if (!string.IsNullOrEmpty(telemetryDto.ScannerId))
                {
                    scanner = await _context.Scanners
                        .FirstOrDefaultAsync(s => s.ScannerId == telemetryDto.ScannerId);

                    if (scanner == null)
                    {
                        scanner = new ScannerDevice
                        {
                            ScannerId = telemetryDto.ScannerId,
                            ScannerName = "Unknown",
                            Building = "Unknown",
                            Floor = "Unknown",
                            Location = "Unknown",
                            Status = "Online",
                            LastSeen = DateTime.Now,
                            CreatedAt = DateTime.Now
                        };
                        _context.Scanners.Add(scanner);
                        await _context.SaveChangesAsync();

                        // Trigger New Scanner Alert
                        await _alertEngine.ProcessNewScannerAlertAsync(scanner);
                    }
                    else
                    {
                        scanner.Status = "Online";
                        scanner.LastSeen = DateTime.Now;
                        await _context.SaveChangesAsync();
                    }
                }

                // Create and insert the telemetry log
                var telemetry = new BeaconTelemetry
                {
                    DeviceId = device.DeviceId,
                    ScannerId = telemetryDto.ScannerId,
                    Rssi = telemetryDto.Rssi,
                    BatteryLevel = telemetryDto.BatteryLevel,
                    XAxis = telemetryDto.XAxis,
                    YAxis = telemetryDto.YAxis,
                    ZAxis = telemetryDto.ZAxis,
                    IsMoving = telemetryDto.IsMoving,
                    ReceiveTime = DateTime.Now
                };
                
                _context.BeaconTelemetries.Add(telemetry);
                await _context.SaveChangesAsync();

                // Trigger Telemetry Alert Evaluation
                await _alertEngine.ProcessTelemetryAlertsAsync(telemetry, device);

                _logger.LogInformation("Persisted telemetry for device {DeviceName} ({MacAddress}) into database", device.DeviceName, device.MacAddress);

                // Call IndoorLocationService for that Beacon to determine stable multi-scanner location
                var locationResult = await _indoorLocationService.DetermineCurrentLocationAsync(device.DeviceId);

                // Broadcast to SignalR clients
                var payload = new
                {
                    macAddress = device.MacAddress,
                    deviceName = device.DeviceName ?? "Registered Beacon",
                    rssi = locationResult.IsAvailable ? (int)locationResult.RepresentativeRssi : telemetryDto.Rssi,
                    batteryLevel = telemetryDto.BatteryLevel,
                    xAxis = telemetryDto.XAxis,
                    yAxis = telemetryDto.YAxis,
                    zAxis = telemetryDto.ZAxis,
                    isMoving = telemetryDto.IsMoving,
                    receiveTime = DateTime.Now,
                    major = telemetryDto.Major,
                    minor = telemetryDto.Minor,
                    scannerId = locationResult.IsAvailable ? locationResult.ScannerId : telemetryDto.ScannerId,
                    scannerName = locationResult.IsAvailable ? (locationResult.ScannerName ?? locationResult.ScannerId) : (scanner?.ScannerName ?? telemetryDto.ScannerId),
                    scannerBuilding = locationResult.IsAvailable ? locationResult.Building : (scanner?.Building ?? "Unknown"),
                    scannerFloor = locationResult.IsAvailable ? locationResult.Floor : (scanner?.Floor ?? "Unknown"),
                    scannerLocation = locationResult.IsAvailable ? locationResult.Location : (scanner?.Location ?? "Unknown Location"),
                    
                    // Floor Map specific
                    deviceId = device.DeviceId,
                    beaconId = device.DeviceId,
                    building = locationResult.IsAvailable ? locationResult.Building : (scanner?.Building ?? "Unknown"),
                    floor = locationResult.IsAvailable ? locationResult.Floor : (scanner?.Floor ?? "Unknown"),
                    location = locationResult.IsAvailable ? locationResult.Location : (scanner?.Location ?? "Unknown Location"),
                    estimatedDistance = locationResult.IsAvailable ? locationResult.EstimatedDistance : (AssetTracking.Web.Helpers.DistanceHelper.EstimateDistanceMeters(telemetryDto.Rssi) ?? 0.0),
                    status = locationResult.IsAvailable ? (telemetryDto.IsMoving ? "Moving" : "Online") : "Offline",
                    lastSeen = AssetTracking.Web.Helpers.DateTimeHelper.FormatLastSeen(locationResult.IsAvailable ? locationResult.DeterminedAt : DateTime.Now)
                };

                await _hubContext.Clients.All.SendAsync("BeaconUpdate", payload);

                return Ok(new { status = "Success", message = "Telemetry persisted and broadcasted" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling telemetry post for device {MacAddress}", telemetryDto.MacAddress);
                return StatusCode(500, "Internal server error");
            }
        }
    }
}
