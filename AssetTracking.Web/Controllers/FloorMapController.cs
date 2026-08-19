using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AssetTracking.Web.Data;
using AssetTracking.Web.DTOs;
using AssetTracking.Web.Helpers;
using AssetTracking.Web.Models;
using AssetTracking.Web.ViewModels;

namespace AssetTracking.Web.Controllers
{
    public class FloorMapController : Controller
    {
        private readonly AppDbContext _context;
        private readonly ILogger<FloorMapController> _logger;

        public FloorMapController(AppDbContext context, ILogger<FloorMapController> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<IActionResult> Index(CancellationToken cancellationToken)
        {
            try
            {
                // Auto-map any legacy unmapped Scanners/Access Points to active Building & Floor records
                await EnsureScannerLocationMappingAsync(cancellationToken);

                // 1. Fetch Buildings and Floors relational data
                var buildingDtos = await _context.Buildings
                    .AsNoTracking()
                    .Where(b => b.IsActive)
                    .OrderBy(b => b.BuildingName)
                    .Select(b => new BuildingDto
                    {
                        BuildingId = b.BuildingId,
                        BuildingName = b.BuildingName,
                        Description = b.Description,
                        IsActive = b.IsActive,
                        Floors = b.Floors
                            .Where(f => f.IsActive)
                            .OrderBy(f => f.FloorNumber ?? 999)
                            .ThenBy(f => f.FloorName)
                            .Select(f => new FloorDto
                            {
                                FloorId = f.FloorId,
                                BuildingId = f.BuildingId,
                                BuildingName = b.BuildingName,
                                FloorName = f.FloorName,
                                FloorNumber = f.FloorNumber,
                                FloorMapImagePath = f.FloorMapImagePath,
                                IsActive = f.IsActive
                            }).ToList()
                    })
                    .ToListAsync(cancellationToken);

                var floorDtosAll = buildingDtos.SelectMany(b => b.Floors).ToList();

                // 2. Fetch Scanners / Access Points with navigation references
                var rawScanners = await _context.Scanners
                    .AsNoTracking()
                    .Include(s => s.BuildingRef)
                    .Include(s => s.FloorRef)
                    .OrderBy(s => s.Building)
                    .ThenBy(s => s.Floor)
                    .ThenBy(s => s.ScannerName)
                    .Select(s => new
                    {
                        s.ScannerId,
                        s.ScannerName,
                        s.BuildingId,
                        s.FloorId,
                        BuildingName = s.BuildingRef != null ? s.BuildingRef.BuildingName : s.Building,
                        FloorName = s.FloorRef != null ? s.FloorRef.FloorName : s.Floor,
                        s.Location,
                        s.Status,
                        s.LastSeen,
                        s.MapXPercent,
                        s.MapYPercent
                    })
                    .ToListAsync(cancellationToken);

                var scannerDtos = rawScanners.Select(s => new FloorMapScannerDto
                {
                    ScannerId = s.ScannerId,
                    ScannerName = !string.IsNullOrWhiteSpace(s.ScannerName) ? s.ScannerName : s.ScannerId,
                    BuildingId = s.BuildingId,
                    FloorId = s.FloorId,
                    Building = !string.IsNullOrWhiteSpace(s.BuildingName) ? s.BuildingName : "Unknown",
                    Floor = !string.IsNullOrWhiteSpace(s.FloorName) ? s.FloorName : "Unknown",
                    Location = !string.IsNullOrWhiteSpace(s.Location) ? s.Location : "Unknown Location",
                    IsOnline = DateTimeHelper.IsOnline(s.LastSeen),
                    MapXPercent = s.MapXPercent,
                    MapYPercent = s.MapYPercent
                }).ToList();

                // 3. Fetch BeaconDevices and join to their single latest Telemetry record + Scanner
                var latestTelemetryIdsQuery = _context.BeaconTelemetries
                    .GroupBy(t => t.DeviceId)
                    .Select(g => g.Max(t => t.TelemetryId));

                var latestTelemetriesQuery = _context.BeaconTelemetries
                    .Where(t => latestTelemetryIdsQuery.Contains(t.TelemetryId));

                var rawBeacons = await (from d in _context.BeaconDevices.AsNoTracking()
                                        where !d.MacAddress.StartsWith("00:11:22:33:44")
                                        join t in latestTelemetriesQuery on d.DeviceId equals t.DeviceId into tGroup
                                        from lt in tGroup.DefaultIfEmpty()
                                        join s in _context.Scanners.AsNoTracking().Include(x => x.BuildingRef).Include(x => x.FloorRef) on lt.ScannerId equals s.ScannerId into sGroup
                                        from sc in sGroup.DefaultIfEmpty()
                                        select new
                                        {
                                            d.DeviceId,
                                            d.MacAddress,
                                            d.DeviceName,
                                            d.Status,
                                            d.LastSeen,
                                            LatestTelemetryId = (long?)lt.TelemetryId,
                                            Rssi = (int?)lt.Rssi,
                                            BatteryLevel = (int?)lt.BatteryLevel,
                                            IsMoving = (bool?)lt.IsMoving,
                                            ReceiveTime = (DateTime?)lt.ReceiveTime,
                                            ScannerId = lt.ScannerId,
                                            ScannerName = sc != null ? sc.ScannerName : null,
                                            BuildingId = sc != null ? (sc.BuildingId ?? (sc.BuildingRef != null ? sc.BuildingRef.BuildingId : (int?)null)) : null,
                                            FloorId = sc != null ? (sc.FloorId ?? (sc.FloorRef != null ? sc.FloorRef.FloorId : (int?)null)) : null,
                                            BuildingName = sc != null ? (sc.BuildingRef != null ? sc.BuildingRef.BuildingName : sc.Building) : null,
                                            FloorName = sc != null ? (sc.FloorRef != null ? sc.FloorRef.FloorName : sc.Floor) : null,
                                            Location = sc != null ? sc.Location : null
                                        })
                                        .OrderBy(d => d.DeviceName ?? d.MacAddress)
                                        .ToListAsync(cancellationToken);

                var beaconDtos = rawBeacons.Select(d =>
                {
                    DateTime? lastSeen = d.LatestTelemetryId.HasValue
                        ? DateTimeHelper.EnsureLocal(d.ReceiveTime!.Value)
                        : (d.LastSeen.HasValue ? DateTimeHelper.EnsureLocal(d.LastSeen.Value) : null);

                    bool isOnline = DateTimeHelper.IsOnline(lastSeen);
                    string status = isOnline
                        ? (d.LatestTelemetryId.HasValue && d.IsMoving == true ? "Moving" : "Online")
                        : "Offline";

                    string? scannerId = d.LatestTelemetryId.HasValue ? d.ScannerId : null;
                    string? scannerName = d.LatestTelemetryId.HasValue ? d.ScannerName : null;
                    if (string.IsNullOrEmpty(scannerName) && !string.IsNullOrEmpty(scannerId))
                    {
                        scannerName = scannerId;
                    }

                    return new FloorMapBeaconDto
                    {
                        BeaconId = d.DeviceId,
                        DeviceName = !string.IsNullOrWhiteSpace(d.DeviceName) ? d.DeviceName : (d.MacAddress ?? "Unknown Beacon"),
                        MacAddress = d.MacAddress ?? "",
                        ScannerId = scannerId,
                        ScannerName = scannerName ?? "Unknown Scanner",
                        BuildingId = d.LatestTelemetryId.HasValue ? d.BuildingId : null,
                        FloorId = d.LatestTelemetryId.HasValue ? d.FloorId : null,
                        Building = (d.LatestTelemetryId.HasValue ? d.BuildingName : null) ?? "Unknown",
                        Floor = (d.LatestTelemetryId.HasValue ? d.FloorName : null) ?? "Unknown",
                        Location = (d.LatestTelemetryId.HasValue ? d.Location : null) ?? "Unknown Location",
                        Rssi = (d.LatestTelemetryId.HasValue ? d.Rssi : 0) ?? 0,
                        BatteryLevel = (d.LatestTelemetryId.HasValue ? d.BatteryLevel : 0) ?? 0,
                        IsMoving = (d.LatestTelemetryId.HasValue ? d.IsMoving : false) ?? false,
                        LastSeen = DateTimeHelper.FormatLastSeen(lastSeen),
                        RawLastSeen = lastSeen,
                        IsOnline = isOnline,
                        Status = status
                    };
                }).ToList();

                // 4. Fallback string building and floor lists for backward compatibility
                var buildings = buildingDtos.Select(b => b.BuildingName).ToList();
                if (buildings.Count == 0)
                {
                    buildings = scannerDtos
                        .Select(s => s.Building)
                        .Where(b => !string.IsNullOrWhiteSpace(b) && b != "Unknown")
                        .Union(beaconDtos.Select(b => b.Building).Where(b => !string.IsNullOrWhiteSpace(b) && b != "Unknown")!)
                        .Where(b => b != null)
                        .Select(b => b!)
                        .Distinct()
                        .OrderBy(b => b)
                        .ToList();
                }

                var floors = floorDtosAll.Select(f => f.FloorName).Distinct().OrderBy(f => f).ToList();
                if (floors.Count == 0)
                {
                    floors = scannerDtos
                        .Select(s => s.Floor)
                        .Where(f => !string.IsNullOrWhiteSpace(f) && f != "Unknown")
                        .Union(beaconDtos.Select(b => b.Floor).Where(f => !string.IsNullOrWhiteSpace(f) && f != "Unknown")!)
                        .Where(f => f != null)
                        .Select(f => f!)
                        .Distinct()
                        .OrderBy(f => f)
                        .ToList();
                }

                var viewModel = new FloorMapViewModel
                {
                    Scanners = scannerDtos,
                    Beacons = beaconDtos,
                    BuildingList = buildingDtos,
                    FloorList = floorDtosAll,
                    Buildings = buildings,
                    Floors = floors
                };

                return View(viewModel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading Floor Map data from database.");
                ViewBag.ErrorMessage = "Unable to load Floor Map data.";

                return View(new FloorMapViewModel());
            }
        }

        /// <summary>
        /// Auto-map any legacy unmapped Scanners/Access Points to active Building & Floor records
        /// </summary>
        private async Task EnsureScannerLocationMappingAsync(CancellationToken cancellationToken)
        {
            try
            {
                var unmappedScanners = await _context.Scanners
                    .Where(s => s.BuildingId == null || s.FloorId == null)
                    .ToListAsync(cancellationToken);

                if (unmappedScanners.Any())
                {
                    var activeBuildings = await _context.Buildings.AsNoTracking().Where(b => b.IsActive).ToListAsync(cancellationToken);
                    var activeFloors = await _context.Floors.AsNoTracking().Where(f => f.IsActive).ToListAsync(cancellationToken);

                    bool updated = false;
                    foreach (var scanner in unmappedScanners)
                    {
                        if (scanner.BuildingId == null && !string.IsNullOrWhiteSpace(scanner.Building))
                        {
                            var matchB = activeBuildings.FirstOrDefault(b =>
                                b.BuildingName.Equals(scanner.Building.Trim(), StringComparison.OrdinalIgnoreCase) ||
                                scanner.Building.Trim().StartsWith(b.BuildingName, StringComparison.OrdinalIgnoreCase) ||
                                b.BuildingName.StartsWith(scanner.Building.Trim(), StringComparison.OrdinalIgnoreCase)
                            );

                            if (matchB != null)
                            {
                                scanner.BuildingId = matchB.BuildingId;
                                scanner.Building = matchB.BuildingName;
                                updated = true;
                            }
                        }

                        if (scanner.FloorId == null && scanner.BuildingId != null && !string.IsNullOrWhiteSpace(scanner.Floor))
                        {
                            var matchF = activeFloors.FirstOrDefault(f =>
                                f.BuildingId == scanner.BuildingId &&
                                (f.FloorName.Equals(scanner.Floor.Trim(), StringComparison.OrdinalIgnoreCase) ||
                                 f.FloorName.EndsWith(scanner.Floor.Trim(), StringComparison.OrdinalIgnoreCase) ||
                                 scanner.Floor.Trim().EndsWith(f.FloorName, StringComparison.OrdinalIgnoreCase) ||
                                 (f.FloorNumber.HasValue && scanner.Floor.Contains(f.FloorNumber.Value.ToString())))
                            );

                            if (matchF != null)
                            {
                                scanner.FloorId = matchF.FloorId;
                                scanner.Floor = matchF.FloorName;
                                updated = true;
                            }
                        }
                    }

                    if (updated)
                    {
                        await _context.SaveChangesAsync(cancellationToken);
                        _logger.LogInformation("Auto-mapped unmapped Scanners/Access Points to active Building & Floor records.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed auto-mapping unmapped scanners to Building/Floor records.");
            }
        }
    }
}