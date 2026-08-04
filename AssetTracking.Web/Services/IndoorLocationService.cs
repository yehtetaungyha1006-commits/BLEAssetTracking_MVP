using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AssetTracking.Web.Data;
using AssetTracking.Web.Helpers;
using AssetTracking.Web.Models;

namespace AssetTracking.Web.Services
{
    public class BeaconLocationResult
    {
        public int DeviceId { get; set; }
        public string MacAddress { get; set; } = string.Empty;
        public string? ScannerId { get; set; }
        public string? ScannerName { get; set; }
        public string? Building { get; set; }
        public string? Floor { get; set; }
        public string? Location { get; set; }
        public double RepresentativeRssi { get; set; }
        public double EstimatedDistance { get; set; }
        public bool IsAvailable { get; set; }
        public DateTime DeterminedAt { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    public class BeaconState
    {
        public string? CurrentScannerId { get; set; }
        public string? CandidateScannerId { get; set; }
        public int StableReadingsCount { get; set; }
    }

    public class IndoorLocationSettings
    {
        public int ObservationWindowSeconds { get; set; } = 10;
        public int MinimumRssi { get; set; } = -95;
        public int SwitchMarginDb { get; set; } = 6;
        public int RequiredStableReadings { get; set; } = 3;
    }

    public class IndoorLocationService : IIndoorLocationService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<IndoorLocationService> _logger;
        private readonly IndoorLocationSettings _settings;
        private readonly ConcurrentDictionary<int, BeaconState> _beaconStates = new();

        public IndoorLocationService(
            IServiceScopeFactory scopeFactory,
            ILogger<IndoorLocationService> logger,
            IConfiguration configuration)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;

            var settingsSection = configuration.GetSection("IndoorLocationSettings");
            _settings = new IndoorLocationSettings
            {
                ObservationWindowSeconds = settingsSection.GetValue<int>("ObservationWindowSeconds", 10),
                MinimumRssi = settingsSection.GetValue<int>("MinimumRssi", -95),
                SwitchMarginDb = settingsSection.GetValue<int>("SwitchMarginDb", 6),
                RequiredStableReadings = settingsSection.GetValue<int>("RequiredStableReadings", 3)
            };
        }

        public async Task<BeaconLocationResult> DetermineCurrentLocationAsync(
            int deviceId,
            CancellationToken cancellationToken = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var device = await context.BeaconDevices
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.DeviceId == deviceId, cancellationToken);

            if (device == null)
            {
                return new BeaconLocationResult
                {
                    DeviceId = deviceId,
                    IsAvailable = false,
                    DeterminedAt = DateTime.Now,
                    Reason = "Device not found"
                };
            }

            var cutoff = DateTime.Now.AddSeconds(-_settings.ObservationWindowSeconds);
            var minRssi = _settings.MinimumRssi;

            var telemetries = await context.BeaconTelemetries
                .AsNoTracking()
                .Where(t => t.DeviceId == deviceId && t.ReceiveTime >= cutoff && t.ScannerId != null && t.Rssi >= minRssi)
                .Select(t => new { t.ScannerId, t.Rssi })
                .ToListAsync(cancellationToken);

            var scanners = await context.Scanners
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            var activeScanners = scanners
                .Where(s => s.Status != "Offline" && s.Status != "Disabled" && DateTimeHelper.IsOnline(s.LastSeen))
                .ToDictionary(s => s.ScannerId);

            var grouped = telemetries
                .Where(t => activeScanners.ContainsKey(t.ScannerId!))
                .GroupBy(t => t.ScannerId!)
                .Select(g => new
                {
                    ScannerId = g.Key,
                    MedianRssi = CalculateMedian(g.Select(x => x.Rssi))
                })
                .ToList();

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var msg = $"Beacon location candidates | Device: {deviceId}";
                foreach (var g in grouped.OrderByDescending(x => x.MedianRssi))
                {
                    var scName = activeScanners.TryGetValue(g.ScannerId, out var s) ? s.ScannerName : g.ScannerId;
                    msg += $"\n{scName} | Median RSSI: {g.MedianRssi:F0}";
                }
                _logger.LogDebug(msg);
            }

            var strongestScanner = grouped
                .OrderByDescending(g => g.MedianRssi)
                .FirstOrDefault();

            var state = _beaconStates.GetOrAdd(deviceId, id => new BeaconState());

            if (state.CurrentScannerId == null)
            {
                var lastTelemetry = await context.BeaconTelemetries
                    .AsNoTracking()
                    .Where(t => t.DeviceId == deviceId && t.ScannerId != null)
                    .OrderByDescending(t => t.TelemetryId)
                    .FirstOrDefaultAsync(cancellationToken);

                if (lastTelemetry != null && lastTelemetry.ScannerId != null && activeScanners.ContainsKey(lastTelemetry.ScannerId))
                {
                    state.CurrentScannerId = lastTelemetry.ScannerId;
                }
            }

            string? strongestId = strongestScanner?.ScannerId;
            double strongestMedian = strongestScanner?.MedianRssi ?? -999;

            if (strongestId == null)
            {
                state.CandidateScannerId = null;
                state.StableReadingsCount = 0;
                state.CurrentScannerId = null;

                return new BeaconLocationResult
                {
                    DeviceId = deviceId,
                    MacAddress = device.MacAddress,
                    IsAvailable = false,
                    DeterminedAt = DateTime.Now,
                    Reason = "No active scanner detected within observation window"
                };
            }

            if (state.CurrentScannerId == null)
            {
                state.CurrentScannerId = strongestId;
                state.CandidateScannerId = null;
                state.StableReadingsCount = 0;

                var newScanner = activeScanners[strongestId];
                _logger.LogInformation("Beacon location changed | Device: {DeviceId} | From: None | To: {ToScanner} | Location: {Location} | RSSI: {Rssi:F0}",
                    deviceId, newScanner.ScannerName, newScanner.Location, strongestMedian);
            }
            else if (strongestId == state.CurrentScannerId)
            {
                state.CandidateScannerId = null;
                state.StableReadingsCount = 0;
            }
            else
            {
                var currentGroup = grouped.FirstOrDefault(g => g.ScannerId == state.CurrentScannerId);
                double currentMedian = currentGroup?.MedianRssi ?? -999;

                bool isEligible = strongestMedian >= currentMedian + _settings.SwitchMarginDb;

                if (isEligible)
                {
                    if (state.CandidateScannerId == strongestId)
                    {
                        state.StableReadingsCount++;
                    }
                    else
                    {
                        state.CandidateScannerId = strongestId;
                        state.StableReadingsCount = 1;
                    }

                    if (state.StableReadingsCount >= _settings.RequiredStableReadings)
                    {
                        var oldScannerId = state.CurrentScannerId;
                        state.CurrentScannerId = strongestId;
                        state.CandidateScannerId = null;
                        state.StableReadingsCount = 0;

                        var oldScannerName = activeScanners.TryGetValue(oldScannerId, out var os) ? os.ScannerName : oldScannerId;
                        var newScanner = activeScanners[strongestId];
                        _logger.LogInformation("Beacon location changed | Device: {DeviceId} | From: {FromScanner} | To: {ToScanner} | Location: {Location} | RSSI: {Rssi:F0}",
                            deviceId, oldScannerName, newScanner.ScannerName, newScanner.Location, strongestMedian);
                    }
                }
                else
                {
                    state.CandidateScannerId = null;
                    state.StableReadingsCount = 0;
                }
            }

            var finalScannerId = state.CurrentScannerId;
            if (finalScannerId != null && activeScanners.TryGetValue(finalScannerId, out var selectedScanner))
            {
                double repRssi = strongestId == finalScannerId ? strongestMedian : (grouped.FirstOrDefault(g => g.ScannerId == finalScannerId)?.MedianRssi ?? -95);
                double distance = DistanceHelper.EstimateDistanceMeters((int)repRssi) ?? 0.0;

                return new BeaconLocationResult
                {
                    DeviceId = deviceId,
                    MacAddress = device.MacAddress,
                    ScannerId = finalScannerId,
                    ScannerName = selectedScanner.ScannerName,
                    Building = selectedScanner.Building,
                    Floor = selectedScanner.Floor,
                    Location = selectedScanner.Location,
                    RepresentativeRssi = repRssi,
                    EstimatedDistance = distance,
                    IsAvailable = true,
                    DeterminedAt = DateTime.Now,
                    Reason = $"Selected scanner: {selectedScanner.ScannerName} (Median RSSI: {repRssi:F0} dBm)"
                };
            }

            return new BeaconLocationResult
            {
                DeviceId = deviceId,
                MacAddress = device.MacAddress,
                IsAvailable = false,
                DeterminedAt = DateTime.Now,
                Reason = "Selected scanner is offline or unavailable"
            };
        }

        private static double CalculateMedian(IEnumerable<int> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            int count = sorted.Count;
            if (count == 0) return 0;
            if (count % 2 == 1)
            {
                return sorted[count / 2];
            }
            else
            {
                return (sorted[count / 2 - 1] + sorted[count / 2]) / 2.0;
            }
        }
    }
}
