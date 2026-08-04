using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AssetTracking.Web.Data;
using AssetTracking.Web.Helpers;
using AssetTracking.Web.Models;

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
                // 1. Fetch Scanners with NoTracking
                var rawScanners = await _context.Scanners
                    .AsNoTracking()
                    .OrderBy(s => s.Building)
                    .ThenBy(s => s.Floor)
                    .ThenBy(s => s.ScannerName)
                    .Select(s => new
                    {
                        s.ScannerId,
                        s.ScannerName,
                        s.Building,
                        s.Floor,
                        s.Location,
                        s.Status,
                        s.LastSeen
                    })
                    .ToListAsync(cancellationToken);

                var scannerDtos = rawScanners.Select(s => new FloorMapScannerDto
                {
                    ScannerId = s.ScannerId,
                    ScannerName = !string.IsNullOrWhiteSpace(s.ScannerName) ? s.ScannerName : s.ScannerId,
                    Building = !string.IsNullOrWhiteSpace(s.Building) ? s.Building : "Unknown",
                    Floor = !string.IsNullOrWhiteSpace(s.Floor) ? s.Floor : "Unknown",
                    Location = !string.IsNullOrWhiteSpace(s.Location) ? s.Location : "Unknown Location",
                    IsOnline = DateTimeHelper.IsOnline(s.LastSeen)
                }).ToList();

                // 2. Fetch BeaconDevices and join to their single latest Telemetry record + Scanner
                var latestTelemetryIdsQuery = _context.BeaconTelemetries
                    .GroupBy(t => t.DeviceId)
                    .Select(g => g.Max(t => t.TelemetryId));

                var latestTelemetriesQuery = _context.BeaconTelemetries
                    .Where(t => latestTelemetryIdsQuery.Contains(t.TelemetryId));

                var rawBeacons = await (from d in _context.BeaconDevices.AsNoTracking()
                                        where !d.MacAddress.StartsWith("00:11:22:33:44")
                                        join t in latestTelemetriesQuery on d.DeviceId equals t.DeviceId into tGroup
                                        from lt in tGroup.DefaultIfEmpty()
                                        join s in _context.Scanners.AsNoTracking() on lt.ScannerId equals s.ScannerId into sGroup
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
                                            ScannerName = sc.ScannerName,
                                            Building = sc.Building,
                                            Floor = sc.Floor,
                                            Location = sc.Location
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
                        Building = (d.LatestTelemetryId.HasValue ? d.Building : null) ?? "Unknown",
                        Floor = (d.LatestTelemetryId.HasValue ? d.Floor : null) ?? "Unknown",
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

                // 3. Extract distinct Buildings and Floors for dynamic dropdown filters
                var buildings = scannerDtos
                    .Select(s => s.Building)
                    .Where(b => !string.IsNullOrWhiteSpace(b) && b != "Unknown")
                    .Union(beaconDtos.Select(b => b.Building).Where(b => !string.IsNullOrWhiteSpace(b) && b != "Unknown")!)
                    .Where(b => b != null)
                    .Select(b => b!)
                    .Distinct()
                    .OrderBy(b => b)
                    .ToList();

                var floors = scannerDtos
                    .Select(s => s.Floor)
                    .Where(f => !string.IsNullOrWhiteSpace(f) && f != "Unknown")
                    .Union(beaconDtos.Select(b => b.Floor).Where(b => !string.IsNullOrWhiteSpace(b) && b != "Unknown")!)
                    .Where(f => f != null)
                    .Select(f => f!)
                    .Distinct()
                    .OrderBy(f => f)
                    .ToList();

                var viewModel = new FloorMapViewModel
                {
                    Scanners = scannerDtos,
                    Beacons = beaconDtos,
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
    }
}