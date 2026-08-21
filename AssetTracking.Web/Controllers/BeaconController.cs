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
        private readonly IIndoorLocationService _indoorLocationService;

        public BeaconController(
            AppDbContext context,
            IHubContext<BeaconHub> hubContext,
            ILogger<BeaconController> logger,
            AlertEngine alertEngine,
            IIndoorLocationService indoorLocationService)
        {
            _context = context;
            _hubContext = hubContext;
            _logger = logger;
            _alertEngine = alertEngine;
            _indoorLocationService = indoorLocationService;
        }

        [HttpPost("telemetry")]
        public async Task<IActionResult> PostTelemetry([FromBody] BeaconTelemetryDto telemetryDto)
        {
            var t3 = DateTime.UtcNow; // T3: API received telemetry

            if (telemetryDto == null)
            {
                return BadRequest("Telemetry is null");
            }

            try
            {
                // Find registered BeaconDevice by Major + Minor FIRST
                BeaconDevice? device = null;
                if (telemetryDto.Major > 0 && telemetryDto.Minor > 0)
                {
                    device = await _context.BeaconDevices
                        .FirstOrDefaultAsync(d => d.Major == telemetryDto.Major && d.Minor == telemetryDto.Minor);
                }

                if (device == null && !string.IsNullOrEmpty(telemetryDto.MacAddress))
                {
                    device = await _context.BeaconDevices
                        .FirstOrDefaultAsync(d => d.MacAddress == telemetryDto.MacAddress);

                    if (device != null && (telemetryDto.Major == 0 && telemetryDto.Minor == 0))
                    {
                        telemetryDto.Major = device.Major;
                        telemetryDto.Minor = device.Minor;
                    }
                }

                if (device == null)
                {
                    // Trigger Unknown Beacon Alert
                    await _alertEngine.ProcessUnknownBeaconAlertAsync(telemetryDto.Major, telemetryDto.Minor, telemetryDto.Rssi);

                    // Ignore telemetry, return unregistered message, and log details for debugging
                    _logger.LogWarning("Unregistered beacon ignored: Major={Major}, Minor={Minor}, RSSI={RSSI}, MAC={MAC}", 
                        telemetryDto.Major, telemetryDto.Minor, telemetryDto.Rssi, telemetryDto.MacAddress);
                    return Ok(new { status = "Ignored", message = "Unregistered beacon ignored" });
                }

                // Server Validation: Verify Major/Minor consistency with Database DeviceId
                if (telemetryDto.Major > 0 && (device.Major != telemetryDto.Major || device.Minor != telemetryDto.Minor))
                {
                    _logger.LogWarning("[SERVER VALIDATION FAILED] Inconsistent telemetry identity! Telemetry Major={TelemetryMajor}, Minor={TelemetryMinor} does not match Db DeviceId={DeviceId} (DbMajor={DbMajor}, DbMinor={DbMinor}). Payload rejected.",
                        telemetryDto.Major, telemetryDto.Minor, device.DeviceId, device.Major, device.Minor);
                    return BadRequest("Telemetry identity inconsistency detected");
                }

                // Update registered device fields
                device.Status = telemetryDto.IsMoving ? "Moving" : "Online";
                device.LastSeen = DateTime.Now;

                // Process Scanner Auto-registration / Update
                ScannerDevice? scanner = null;
                bool newScannerCreated = false;
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
                        newScannerCreated = true;
                    }
                    else
                    {
                        scanner.Status = "Online";
                        scanner.LastSeen = DateTime.Now;
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

                // Consolidated SINGLE SaveChangesAsync call for device, scanner, and telemetry
                await _context.SaveChangesAsync();
                var t4 = DateTime.UtcNow; // T4: Database save completes

                if (newScannerCreated && scanner != null)
                {
                    // Trigger New Scanner Alert
                    await _alertEngine.ProcessNewScannerAlertAsync(scanner);
                }

                // Trigger Telemetry Alert Evaluation
                await _alertEngine.ProcessTelemetryAlertsAsync(telemetry, device);

                // Call IndoorLocationService for that Beacon to determine stable multi-scanner location via in-memory telemetry cache
                var locationResult = await _indoorLocationService.RecordTelemetryAndDetermineLocationAsync(
                    device.DeviceId, 
                    telemetryDto.ScannerId, 
                    telemetryDto.Rssi, 
                    DateTime.Now,
                    telemetryDto.IsFreshObservation,
                    telemetryDto.ObservationAgeMs);
                var t5 = DateTime.UtcNow; // T5: AP selection calculation completes
                var t6 = locationResult.LocationChanged ? t5 : (DateTime?)null; // T6: Selected AP changes timestamp

                var t7 = DateTime.UtcNow; // T7: SignalR event broadcast

                var dbMs = (t4 - t3).TotalMilliseconds;
                var calcMs = (t5 - t4).TotalMilliseconds;
                var broadcastMs = (t7 - t5).TotalMilliseconds;
                var totalServerMs = (t7 - t3).TotalMilliseconds;

                _logger.LogInformation("[DIAGNOSTICS T3-T7] Device: {Device} ({Mac}) | T3->T4 DB: {DbMs:F1}ms | T4->T5 Calc: {CalcMs:F1}ms | T5->T7 SignalR: {BroadcastMs:F1}ms | TotalServer: {TotalMs:F1}ms | SelectedAP: {SelectedAP} | LocationChanged: {LocationChanged}",
                    device.DeviceName, device.MacAddress, dbMs, calcMs, broadcastMs, totalServerMs, locationResult.ScannerId, locationResult.LocationChanged);

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
                    lastSeen = AssetTracking.Web.Helpers.DateTimeHelper.FormatLastSeen(locationResult.IsAvailable ? locationResult.DeterminedAt : DateTime.Now),

                    isHeartbeat = telemetryDto.IsHeartbeat,
                    // Diagnostic Timing Timestamps (T1-T7)
                    t1_observedAt = telemetryDto.ObservedAt,
                    t2_sentAt = telemetryDto.SentAt,
                    t3_apiReceived = t3,
                    t4_dbSaved = t4,
                    t5_apCalculated = t5,
                    t6_apChanged = t6,
                    t7_signalrBroadcast = t7
                };

                await _hubContext.Clients.All.SendAsync("BeaconUpdate", payload);
                await _hubContext.Clients.All.SendAsync("TelemetryUpdated", payload);

                _logger.LogInformation("[SignalR] Beacon: {Major}-{Minor} | RSSI broadcast completed", telemetryDto.Major, telemetryDto.Minor);

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
