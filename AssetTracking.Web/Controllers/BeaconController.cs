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
                // Find or create the beacon device by MacAddress
                var device = await _context.BeaconDevices
                    .FirstOrDefaultAsync(d => d.MacAddress == telemetryDto.MacAddress);

                if (device == null && telemetryDto.MacAddress == "E2C56DB5-DFFB-48D2-B060-D0F5A71096E0-1-616")
                {
                    // Fallback to locate the existing Minew E7 device under its old MAC address format
                    device = await _context.BeaconDevices
                        .FirstOrDefaultAsync(d => d.MacAddress == "E2C56DB5-1-616");
                    
                    if (device != null)
                    {
                        // Migrate the MAC address to the new format to keep it working seamlessly
                        device.MacAddress = telemetryDto.MacAddress;
                        _logger.LogInformation("Migrated device MAC address from old format to {MacAddress}", telemetryDto.MacAddress);
                    }
                }

                if (device == null)
                {
                    device = new BeaconDevice
                    {
                        MacAddress = telemetryDto.MacAddress,
                        DeviceName = string.IsNullOrEmpty(telemetryDto.DeviceName) ? "Minew E7" : telemetryDto.DeviceName,
                        Status = "Online",
                        LastSeen = DateTime.Now
                    };
                    _context.BeaconDevices.Add(device);
                }
                else
                {
                    // Update device fields
                    if (!string.IsNullOrEmpty(telemetryDto.DeviceName))
                    {
                        device.DeviceName = telemetryDto.DeviceName;
                    }
                    device.Status = "Online";
                    device.LastSeen = DateTime.Now;
                }

                // Save changes to generate DeviceId for new devices
                await _context.SaveChangesAsync();

                // Create and insert the telemetry log
                var telemetry = new BeaconTelemetry
                {
                    DeviceId = device.DeviceId,
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
