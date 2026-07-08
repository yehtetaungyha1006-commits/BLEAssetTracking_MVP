using AssetTracking.Web.Data;
using AssetTracking.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace AssetTracking.Web.Services
{
    public class AlertEngine
    {
        private readonly AppDbContext _context;
        private readonly ILogger<AlertEngine> _logger;
        private readonly Microsoft.Extensions.Configuration.IConfiguration _configuration;

        public AlertEngine(AppDbContext context, ILogger<AlertEngine> logger, Microsoft.Extensions.Configuration.IConfiguration configuration)
        {
            _context = context;
            _logger = logger;
            _configuration = configuration;
        }

        // Process telemetry-based alerts
        public async Task ProcessTelemetryAlertsAsync(BeaconTelemetry telemetry, BeaconDevice device)
        {
            try
            {
                // 1. Weak RSSI Signal Check
                // Threshold: Rssi <= -85
                if (telemetry.Rssi <= -85)
                {
                    var hasActive = await _context.AlertLogs
                        .AnyAsync(a => a.DeviceId == device.DeviceId && a.AlertType == "Weak RSSI Signal" && !a.IsResolved);
                    
                    if (!hasActive)
                    {
                        var alert = new AlertLog
                        {
                            DeviceId = device.DeviceId,
                            ScannerId = telemetry.ScannerId,
                            AlertType = "Weak RSSI Signal",
                            AlertMessage = $"Device {device.DeviceName} ({device.MacAddress}) has weak signal: {telemetry.Rssi} dBm at Scanner {telemetry.ScannerId}",
                            Severity = "Warning",
                            AlertTime = DateTime.Now,
                            IsResolved = false
                        };
                        _context.AlertLogs.Add(alert);
                    }
                }
                else
                {
                    // Resolve active Weak RSSI alerts
                    var activeAlerts = await _context.AlertLogs
                        .Where(a => a.DeviceId == device.DeviceId && a.AlertType == "Weak RSSI Signal" && !a.IsResolved)
                        .ToListAsync();
                    
                    foreach (var a in activeAlerts)
                    {
                        a.IsResolved = true;
                        a.ResolvedAt = DateTime.Now;
                    }
                }

                // 2. Battery Check
                // Critical Battery < 5%
                // Low Battery < 20% (and >= 5%)
                if (telemetry.BatteryLevel < 5)
                {
                    var hasActiveCritical = await _context.AlertLogs
                        .AnyAsync(a => a.DeviceId == device.DeviceId && a.AlertType == "Critical Battery" && !a.IsResolved);
                    
                    if (!hasActiveCritical)
                    {
                        var alert = new AlertLog
                        {
                            DeviceId = device.DeviceId,
                            ScannerId = telemetry.ScannerId,
                            AlertType = "Critical Battery",
                            AlertMessage = $"Device {device.DeviceName} ({device.MacAddress}) has critical battery: {telemetry.BatteryLevel}%",
                            Severity = "Critical",
                            AlertTime = DateTime.Now,
                            IsResolved = false
                        };
                        _context.AlertLogs.Add(alert);
                    }

                    // Resolve Low Battery if any is active (since it's now Critical)
                    var activeLow = await _context.AlertLogs
                        .Where(a => a.DeviceId == device.DeviceId && a.AlertType == "Low Battery" && !a.IsResolved)
                        .ToListAsync();
                    
                    foreach (var a in activeLow)
                    {
                        a.IsResolved = true;
                        a.ResolvedAt = DateTime.Now;
                    }
                }
                else if (telemetry.BatteryLevel < 20)
                {
                    var hasActiveLow = await _context.AlertLogs
                        .AnyAsync(a => a.DeviceId == device.DeviceId && a.AlertType == "Low Battery" && !a.IsResolved);
                    
                    if (!hasActiveLow)
                    {
                        var alert = new AlertLog
                        {
                            DeviceId = device.DeviceId,
                            ScannerId = telemetry.ScannerId,
                            AlertType = "Low Battery",
                            AlertMessage = $"Device {device.DeviceName} ({device.MacAddress}) has low battery: {telemetry.BatteryLevel}%",
                            Severity = "Warning",
                            AlertTime = DateTime.Now,
                            IsResolved = false
                        };
                        _context.AlertLogs.Add(alert);
                    }

                    // Resolve Critical Battery if active (since it has recovered to Low)
                    var activeCritical = await _context.AlertLogs
                        .Where(a => a.DeviceId == device.DeviceId && a.AlertType == "Critical Battery" && !a.IsResolved)
                        .ToListAsync();
                    
                    if (activeCritical.Any())
                    {
                        foreach (var a in activeCritical)
                        {
                            a.IsResolved = true;
                            a.ResolvedAt = DateTime.Now;
                        }

                        // Create a recovery alert
                        var recoveryAlert = new AlertLog
                        {
                            DeviceId = device.DeviceId,
                            ScannerId = telemetry.ScannerId,
                            AlertType = "Battery Restored",
                            AlertMessage = $"Device {device.DeviceName} ({device.MacAddress}) critical battery recovered to low battery: {telemetry.BatteryLevel}%",
                            Severity = "Info",
                            AlertTime = DateTime.Now,
                            IsResolved = true,
                            ResolvedAt = DateTime.Now
                        };
                        _context.AlertLogs.Add(recoveryAlert);
                    }
                }
                else
                {
                    // Battery level is normal (>= 20%). Resolve Low/Critical battery alerts.
                    var activeBatteryAlerts = await _context.AlertLogs
                        .Where(a => a.DeviceId == device.DeviceId && (a.AlertType == "Low Battery" || a.AlertType == "Critical Battery") && !a.IsResolved)
                        .ToListAsync();
                    
                    if (activeBatteryAlerts.Any())
                    {
                        foreach (var a in activeBatteryAlerts)
                        {
                            a.IsResolved = true;
                            a.ResolvedAt = DateTime.Now;
                        }

                        var recoveryAlert = new AlertLog
                        {
                            DeviceId = device.DeviceId,
                            ScannerId = telemetry.ScannerId,
                            AlertType = "Battery Restored",
                            AlertMessage = $"Device {device.DeviceName} ({device.MacAddress}) battery level returned to normal: {telemetry.BatteryLevel}%",
                            Severity = "Info",
                            AlertTime = DateTime.Now,
                            IsResolved = true,
                            ResolvedAt = DateTime.Now
                        };
                        _context.AlertLogs.Add(recoveryAlert);
                    }
                }

                // 3. Device Back Online Check
                var offlineAlerts = await _context.AlertLogs
                    .Where(a => a.DeviceId == device.DeviceId && a.AlertType == "Device Offline" && !a.IsResolved)
                    .ToListAsync();
                
                if (offlineAlerts.Any())
                {
                    foreach (var a in offlineAlerts)
                    {
                        a.IsResolved = true;
                        a.ResolvedAt = DateTime.Now;
                    }

                    var onlineAlert = new AlertLog
                    {
                        DeviceId = device.DeviceId,
                        ScannerId = telemetry.ScannerId,
                        AlertType = "Device Back Online",
                        AlertMessage = $"Device {device.DeviceName} ({device.MacAddress}) is back online.",
                        Severity = "Info",
                        AlertTime = DateTime.Now,
                        IsResolved = true,
                        ResolvedAt = DateTime.Now
                    };
                    _context.AlertLogs.Add(onlineAlert);
                }

                // 4. Beacon Location Changed Check
                var prevTelemetry = await _context.BeaconTelemetries
                    .Where(t => t.DeviceId == device.DeviceId && t.TelemetryId != telemetry.TelemetryId)
                    .OrderByDescending(t => t.ReceiveTime)
                    .FirstOrDefaultAsync();

                if (prevTelemetry != null && prevTelemetry.ScannerId != null && telemetry.ScannerId != null && prevTelemetry.ScannerId != telemetry.ScannerId)
                {
                    var oldScanner = await _context.Scanners.FindAsync(prevTelemetry.ScannerId);
                    var newScanner = await _context.Scanners.FindAsync(telemetry.ScannerId);

                    string oldName = oldScanner?.ScannerName ?? prevTelemetry.ScannerId;
                    string newName = newScanner?.ScannerName ?? telemetry.ScannerId;

                    var locationAlert = new AlertLog
                    {
                        DeviceId = device.DeviceId,
                        ScannerId = telemetry.ScannerId,
                        AlertType = "Beacon Location Changed",
                        AlertMessage = $"Device {device.DeviceName} ({device.MacAddress}) location changed from {oldName} to {newName}",
                        Severity = "Info",
                        AlertTime = DateTime.Now,
                        IsResolved = true,
                        ResolvedAt = DateTime.Now
                    };
                    _context.AlertLogs.Add(locationAlert);
                }

                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing telemetry alerts for device {DeviceId}", device.DeviceId);
            }
        }

        // Process scanner-based new scanner registration alert
        public async Task ProcessNewScannerAlertAsync(ScannerDevice scanner)
        {
            try
            {
                // Only alert if no alert exists for this scanner
                var hasAlert = await _context.AlertLogs
                    .AnyAsync(a => a.ScannerId == scanner.ScannerId && a.AlertType == "New Scanner Registered");

                if (!hasAlert)
                {
                    var alert = new AlertLog
                    {
                        ScannerId = scanner.ScannerId,
                        AlertType = "New Scanner Registered",
                        AlertMessage = $"New scanner registered: ScannerId = {scanner.ScannerId}",
                        Severity = "Info",
                        AlertTime = DateTime.Now,
                        IsResolved = true, // One-time info event, starts resolved
                        ResolvedAt = DateTime.Now
                    };
                    _context.AlertLogs.Add(alert);
                    await _context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging new scanner alert");
            }
        }

        // Process unknown beacon alert
        public async Task ProcessUnknownBeaconAlertAsync(int major, int minor, int rssi)
        {
            try
            {
                var msgPattern = $"Major={major}, Minor={minor}";
                var hasActive = await _context.AlertLogs
                    .AnyAsync(a => a.AlertType == "Unknown Beacon Detected" && !a.IsResolved && a.AlertMessage.Contains(msgPattern));

                if (!hasActive)
                {
                    var alert = new AlertLog
                    {
                        AlertType = "Unknown Beacon Detected",
                        AlertMessage = $"Unknown beacon detected: Major={major}, Minor={minor}, RSSI={rssi}",
                        Severity = "Warning",
                        AlertTime = DateTime.Now,
                        IsResolved = false
                    };
                    _context.AlertLogs.Add(alert);
                    await _context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging unknown beacon alert");
            }
        }

        // Background Offline check
        public async Task ProcessOfflineAlertsAsync()
        {
            try
            {
                var now = DateTime.Now;
                int offlineTimeout = _configuration.GetValue<int?>("ScannerSettings:OfflineTimeoutSeconds") ?? 30;
                var cutoff30 = now.AddSeconds(-offlineTimeout);

                var offlineDevices = await _context.BeaconDevices
                    .Where(d => d.LastSeen == null || d.LastSeen < cutoff30)
                    .ToListAsync();

                foreach (var device in offlineDevices)
                {
                    var hasActive = await _context.AlertLogs
                        .AnyAsync(a => a.DeviceId == device.DeviceId && a.AlertType == "Device Offline" && !a.IsResolved);

                    if (!hasActive)
                    {
                        var latestTelemetry = await _context.BeaconTelemetries
                            .Where(t => t.DeviceId == device.DeviceId)
                            .OrderByDescending(t => t.ReceiveTime)
                            .FirstOrDefaultAsync();

                        string lastSeenStr = device.LastSeen.HasValue ? device.LastSeen.Value.ToString("yyyy-MM-dd HH:mm:ss") : "Never";

                        var alert = new AlertLog
                        {
                            DeviceId = device.DeviceId,
                            ScannerId = latestTelemetry?.ScannerId,
                            AlertType = "Device Offline",
                            AlertMessage = $"Device {device.DeviceName} ({device.MacAddress}) has gone offline. Last seen at {lastSeenStr}",
                            Severity = "Critical",
                            AlertTime = DateTime.Now,
                            IsResolved = false
                        };
                        _context.AlertLogs.Add(alert);
                    }
                }

                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing background offline alerts");
            }
        }
    }
}
