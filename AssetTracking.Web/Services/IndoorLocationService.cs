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
        public bool LocationChanged { get; set; }
        public string? PreviousScannerId { get; set; }
    }

    public class TelemetryObservation
    {
        public string ScannerId { get; set; } = string.Empty;
        public int RawRssi { get; set; }
        public double FilteredRssi { get; set; }
        public DateTime ReceiveTime { get; set; }
        public bool IsFresh { get; set; } = true;
        public double ObservationAgeMs { get; set; }
    }

    public class BeaconState
    {
        public string? CurrentScannerId { get; set; }
        public string? CandidateScannerId { get; set; }
        public int StableReadingsCount { get; set; }
        public ConcurrentDictionary<string, TelemetryObservation> RecentObservations { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public class IndoorLocationSettings
    {
        public int ObservationWindowSeconds { get; set; } = 2;
        public int MinimumRssi { get; set; } = -95;
        public int NormalSwitchMarginDb { get; set; } = 4;
        public int NormalConfirmationCount { get; set; } = 2;
        public int FastSwitchMarginDb { get; set; } = 12;
        public int FastConfirmationCount { get; set; } = 1;
        public int CurrentApStaleSeconds { get; set; } = 2;
        public double EmaAlpha { get; set; } = 0.6;
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
                ObservationWindowSeconds = settingsSection.GetValue<int>("ObservationWindowSeconds", 2),
                MinimumRssi = settingsSection.GetValue<int>("MinimumRssi", -95),
                NormalSwitchMarginDb = settingsSection.GetValue<int>("NormalSwitchMarginDb", 4),
                NormalConfirmationCount = settingsSection.GetValue<int>("NormalConfirmationCount", 2),
                FastSwitchMarginDb = settingsSection.GetValue<int>("FastSwitchMarginDb", 12),
                FastConfirmationCount = settingsSection.GetValue<int>("FastConfirmationCount", 1),
                CurrentApStaleSeconds = settingsSection.GetValue<int>("CurrentApStaleSeconds", 2),
                EmaAlpha = settingsSection.GetValue<double>("EmaAlpha", 0.6)
            };
        }

        public async Task<BeaconLocationResult> DetermineCurrentLocationAsync(
            int deviceId,
            CancellationToken cancellationToken = default)
        {
            return await EvaluateInMemoryLocationAsync(deviceId, null, null, null, true, 0, cancellationToken);
        }

        public async Task<BeaconLocationResult> RecordTelemetryAndDetermineLocationAsync(
            int deviceId,
            string? scannerId,
            int rssi,
            DateTime receiveTime,
            bool isFreshObservation = true,
            double observationAgeMs = 0,
            CancellationToken cancellationToken = default)
        {
            return await EvaluateInMemoryLocationAsync(deviceId, scannerId, rssi, receiveTime, isFreshObservation, observationAgeMs, cancellationToken);
        }

        private async Task<BeaconLocationResult> EvaluateInMemoryLocationAsync(
            int deviceId,
            string? incomingScannerId,
            int? incomingRssi,
            DateTime? incomingReceiveTime,
            bool isFreshObservation,
            double observationAgeMs,
            CancellationToken cancellationToken)
        {
            var now = incomingReceiveTime ?? DateTime.Now;
            var state = _beaconStates.GetOrAdd(deviceId, id => new BeaconState());

            // 1. Record incoming telemetry observation with EMA filtering if provided AND fresh
            bool isObsFresh = isFreshObservation && observationAgeMs <= 2000.0;
            if (isFreshObservation && !string.IsNullOrEmpty(incomingScannerId) && incomingRssi.HasValue && incomingRssi.Value >= _settings.MinimumRssi)
            {
                double filteredRssi;
                if (state.RecentObservations.TryGetValue(incomingScannerId, out var prevObs))
                {
                    filteredRssi = (_settings.EmaAlpha * incomingRssi.Value) + ((1.0 - _settings.EmaAlpha) * prevObs.FilteredRssi);
                }
                else
                {
                    filteredRssi = incomingRssi.Value;
                }

                state.RecentObservations[incomingScannerId] = new TelemetryObservation
                {
                    ScannerId = incomingScannerId,
                    RawRssi = incomingRssi.Value,
                    FilteredRssi = filteredRssi,
                    ReceiveTime = now,
                    IsFresh = isObsFresh,
                    ObservationAgeMs = observationAgeMs
                };
            }

            // 2. Prune observations older than ObservationWindowSeconds
            var cutoff = now.AddSeconds(-_settings.ObservationWindowSeconds);
            var staleKeys = state.RecentObservations
                .Where(kvp => kvp.Value.ReceiveTime < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in staleKeys)
            {
                state.RecentObservations.TryRemove(key, out _);
            }

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
                    DeterminedAt = now,
                    Reason = "Device not found"
                };
            }

            var scanners = await context.Scanners
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            var activeScanners = scanners
                .Where(s => s.Status != "Offline" && s.Status != "Disabled" && DateTimeHelper.IsOnline(s.LastSeen))
                .ToDictionary(s => s.ScannerId);

            // 3. Find active scanner candidates from in-memory observation cache
            var activeObservations = state.RecentObservations.Values
                .Where(obs => activeScanners.ContainsKey(obs.ScannerId))
                .OrderByDescending(obs => obs.FilteredRssi)
                .ToList();

            var strongestObs = activeObservations.FirstOrDefault();

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

            string? strongestId = strongestObs?.ScannerId;
            double strongestRssi = strongestObs?.FilteredRssi ?? -999;
            bool isStrongestFresh = strongestObs != null && strongestObs.IsFresh && (now - strongestObs.ReceiveTime).TotalSeconds <= 2.0;

            bool locationChanged = false;
            string? previousScannerId = state.CurrentScannerId;

            if (strongestId == null)
            {
                if (state.CurrentScannerId != null)
                {
                    locationChanged = true;
                }
                state.CandidateScannerId = null;
                state.StableReadingsCount = 0;
                state.CurrentScannerId = null;

                return new BeaconLocationResult
                {
                    DeviceId = deviceId,
                    MacAddress = device.MacAddress,
                    IsAvailable = false,
                    DeterminedAt = now,
                    Reason = "No active scanner detected within observation window",
                    LocationChanged = locationChanged,
                    PreviousScannerId = previousScannerId
                };
            }

            if (state.CurrentScannerId == null)
            {
                state.CurrentScannerId = strongestId;
                state.CandidateScannerId = null;
                state.StableReadingsCount = 0;
                locationChanged = true;

                var newScanner = activeScanners[strongestId];
                _logger.LogInformation("[T5] In-Memory Location Initialized | Device: {DeviceId} | To: {ToScanner} ({Location}) | RSSI: {Rssi:F1}",
                    deviceId, newScanner.ScannerName, newScanner.Location, strongestRssi);
            }
            else if (strongestId == state.CurrentScannerId)
            {
                state.CandidateScannerId = null;
                state.StableReadingsCount = 0;
            }
            else
            {
                state.RecentObservations.TryGetValue(state.CurrentScannerId, out var currentObs);
                double currentRssi = currentObs?.FilteredRssi ?? -999;

                bool isCurrentStale = currentObs == null || (now - currentObs.ReceiveTime).TotalSeconds >= _settings.CurrentApStaleSeconds;
                double margin = strongestRssi - currentRssi;

                int requiredReadings;
                if (isCurrentStale || margin >= _settings.FastSwitchMarginDb)
                {
                    requiredReadings = _settings.FastConfirmationCount;
                }
                else
                {
                    requiredReadings = _settings.NormalConfirmationCount;
                }

                bool isEligible = isCurrentStale || (margin >= _settings.NormalSwitchMarginDb);

                if (isEligible)
                {
                    // CRITICAL FIX: Only increment confirmation count if the candidate observation is FRESH and <= 2000ms
                    if (isStrongestFresh)
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

                        if (state.StableReadingsCount >= requiredReadings)
                        {
                            var oldScannerId = state.CurrentScannerId;
                            state.CurrentScannerId = strongestId;
                            state.CandidateScannerId = null;
                            state.StableReadingsCount = 0;
                            locationChanged = true;

                            var oldScannerName = activeScanners.TryGetValue(oldScannerId, out var os) ? os.ScannerName : oldScannerId;
                            var newScanner = activeScanners[strongestId];
                            _logger.LogInformation("[T5/T6] In-Memory Location CHANGED | Device: {DeviceId} | From: {FromScanner} -> To: {ToScanner} ({Location}) | RSSI: {Rssi:F1} (Diff: {Margin:F1}dB, ReqObs: {ReqObs}, FreshObs: {Fresh})",
                                deviceId, oldScannerName, newScanner.ScannerName, newScanner.Location, strongestRssi, margin, requiredReadings, isStrongestFresh);
                        }
                    }
                    else
                    {
                        _logger.LogDebug("[T5] Switching candidate {CandidateAP} skipped confirmation increment because observation is repeated/cached (Age: {Age:F0}ms)", strongestId, strongestObs?.ObservationAgeMs);
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
                double repRssi = strongestId == finalScannerId ? strongestRssi : (state.RecentObservations.TryGetValue(finalScannerId, out var finalObs) ? finalObs.FilteredRssi : -95);
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
                    DeterminedAt = now,
                    Reason = $"Selected scanner: {selectedScanner.ScannerName} (Filtered RSSI: {repRssi:F1} dBm)",
                    LocationChanged = locationChanged,
                    PreviousScannerId = previousScannerId
                };
            }

            return new BeaconLocationResult
            {
                DeviceId = deviceId,
                MacAddress = device.MacAddress,
                IsAvailable = false,
                DeterminedAt = now,
                Reason = "Selected scanner is offline or unavailable",
                LocationChanged = locationChanged,
                PreviousScannerId = previousScannerId
            };
        }
    }
}
