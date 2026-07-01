using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using AssetTracking.Shared;
using AssetTracking.Web.Data;
using AssetTracking.Web.Models;
using AssetTracking.Web.Hubs;
using System;
using System.Threading.Tasks;

namespace AssetTracking.Web.Controllers
{
    [ApiController]
    [Route("api/beacon")]
    public class BeaconController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IHubContext<BeaconHub> _hubContext;
        private readonly ILogger<BeaconController> _logger;

        public BeaconController(AppDbContext context, IHubContext<BeaconHub> hubContext, ILogger<BeaconController> logger)
        {
            _context = context;
            _hubContext = hubContext;
            _logger = logger;
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
                            ScannerName = telemetryDto.ScannerId,
                            Building = "Unknown",
                            Floor = "Unknown",
                            Location = "Unknown",
                            Status = "Online",
                            LastSeen = DateTime.Now,
                            CreatedAt = DateTime.Now
                        };
                        _context.Scanners.Add(scanner);
                    }
                    else
                    {
                        scanner.Status = "Online";
                        scanner.LastSeen = DateTime.Now;
                    }
                    await _context.SaveChangesAsync();
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

                _logger.LogInformation("Persisted telemetry for device {DeviceName} ({MacAddress}) into database", device.DeviceName, device.MacAddress);

                // Broadcast to SignalR clients for the dashboard
                telemetryDto.MacAddress = device.MacAddress;
                telemetryDto.DeviceName = device.DeviceName ?? "Registered Beacon";
                if (scanner != null)
                {
                    telemetryDto.ScannerName = scanner.ScannerName;
                    telemetryDto.ScannerBuilding = scanner.Building;
                    telemetryDto.ScannerFloor = scanner.Floor;
                    telemetryDto.ScannerLocation = scanner.Location;
                }

                await _hubContext.Clients.All.SendAsync("BeaconUpdate", telemetryDto);

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
