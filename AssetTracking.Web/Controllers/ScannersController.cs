using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AssetTracking.Web.Data;
using AssetTracking.Web.Models;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace AssetTracking.Web.Controllers
{
    public class ScannersController : Controller
    {
        private readonly AppDbContext _context;

        public ScannersController(AppDbContext context)
        {
            _context = context;
        }

        // GET: Scanners
        public async Task<IActionResult> Index()
        {
            var scanners = await _context.Scanners
                .Include(s => s.BuildingRef)
                .Include(s => s.FloorRef)
                .ToListAsync();

            foreach (var scanner in scanners)
            {
                scanner.Status = AssetTracking.Web.Helpers.DateTimeHelper.IsOnline(scanner.LastSeen) ? "Online" : "Offline";
            }
            return View(scanners);
        }

        // GET: Scanners/Edit/5
        public async Task<IActionResult> Edit(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return NotFound();
            }

            var scanner = await _context.Scanners.FindAsync(id);
            if (scanner == null)
            {
                return NotFound();
            }

            await PopulateLocationDropdownsViewBag(scanner.BuildingId, scanner.Building);
            return View(scanner);
        }

        // POST: Scanners/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, [Bind("ScannerId,BuildingId,FloorId,Building,Floor,Location")] ScannerDevice scanner)
        {
            if (id != scanner.ScannerId)
            {
                return NotFound();
            }

            Building? selectedBuilding = null;
            if (scanner.BuildingId.HasValue && scanner.BuildingId > 0)
            {
                selectedBuilding = await _context.Buildings.FindAsync(scanner.BuildingId.Value);
            }
            else if (!string.IsNullOrWhiteSpace(scanner.Building))
            {
                selectedBuilding = await _context.Buildings.FirstOrDefaultAsync(b => b.BuildingName.ToLower() == scanner.Building.Trim().ToLower());
            }

            Floor? selectedFloor = null;
            if (scanner.FloorId.HasValue && scanner.FloorId > 0)
            {
                selectedFloor = await _context.Floors.FindAsync(scanner.FloorId.Value);
            }
            else if (!string.IsNullOrWhiteSpace(scanner.Floor) && selectedBuilding != null)
            {
                selectedFloor = await _context.Floors.FirstOrDefaultAsync(f => f.BuildingId == selectedBuilding.BuildingId && f.FloorName.ToLower() == scanner.Floor.Trim().ToLower());
            }

            if (selectedBuilding == null)
            {
                ModelState.AddModelError(nameof(scanner.Building), "Building is required.");
            }

            if (selectedFloor == null)
            {
                ModelState.AddModelError(nameof(scanner.Floor), "Floor is required.");
            }

            if (string.IsNullOrWhiteSpace(scanner.Location))
            {
                ModelState.AddModelError(nameof(scanner.Location), "Location is required.");
            }

            if (ModelState.IsValid)
            {
                try
                {
                    var existingScanner = await _context.Scanners.FindAsync(id);
                    if (existingScanner == null)
                    {
                        return NotFound();
                    }

                    // Update relational FKs and legacy string properties
                    existingScanner.BuildingId = selectedBuilding?.BuildingId;
                    existingScanner.FloorId = selectedFloor?.FloorId;
                    existingScanner.Building = selectedBuilding?.BuildingName ?? scanner.Building ?? "Unknown";
                    existingScanner.Floor = selectedFloor?.FloorName ?? scanner.Floor ?? "Unknown";
                    existingScanner.Location = scanner.Location;

                    _context.Update(existingScanner);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!ScannerExists(scanner.ScannerId))
                    {
                        return NotFound();
                    }
                    else
                    {
                        throw;
                    }
                }
                return RedirectToAction(nameof(Index));
            }

            await PopulateLocationDropdownsViewBag(selectedBuilding?.BuildingId ?? scanner.BuildingId, selectedBuilding?.BuildingName ?? scanner.Building);
            return View(scanner);
        }

        private async Task PopulateLocationDropdownsViewBag(int? selectedBuildingId, string? selectedBuildingName)
        {
            var buildings = await _context.Buildings
                .AsNoTracking()
                .Where(b => b.IsActive)
                .OrderBy(b => b.BuildingName)
                .ToListAsync();

            ViewBag.Buildings = buildings;

            int buildingIdFilter = selectedBuildingId ?? (buildings.FirstOrDefault(b => b.BuildingName.Equals(selectedBuildingName, StringComparison.OrdinalIgnoreCase))?.BuildingId ?? buildings.FirstOrDefault()?.BuildingId ?? 0);

            var floors = await _context.Floors
                .AsNoTracking()
                .Where(f => f.IsActive && (buildingIdFilter == 0 || f.BuildingId == buildingIdFilter))
                .OrderBy(f => f.FloorNumber ?? 999)
                .ThenBy(f => f.FloorName)
                .ToListAsync();

            ViewBag.Floors = floors;
        }

        private bool ScannerExists(string id)
        {
            return _context.Scanners.Any(e => e.ScannerId == id);
        }

        // GET: Scanners/Delete/5
        public async Task<IActionResult> Delete(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return NotFound();
            }

            var scanner = await _context.Scanners.FindAsync(id);
            if (scanner == null)
            {
                return NotFound();
            }

            // Check if scanner has recent telemetry within last 5 minutes
            var latestTelemetryTime = await _context.BeaconTelemetries
                .Where(t => t.ScannerId == id)
                .OrderByDescending(t => t.ReceiveTime)
                .Select(t => (DateTime?)t.ReceiveTime)
                .FirstOrDefaultAsync();

            bool hasRecentTelemetry = false;
            if (latestTelemetryTime.HasValue)
            {
                var localLatest = AssetTracking.Web.Helpers.DateTimeHelper.EnsureLocal(latestTelemetryTime.Value);
                if ((DateTime.Now - localLatest).TotalMinutes <= 5)
                {
                    hasRecentTelemetry = true;
                }
            }

            ViewBag.HasRecentTelemetry = hasRecentTelemetry;

            return View(scanner);
        }

        // POST: Scanners/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            var scanner = await _context.Scanners.FindAsync(id);
            if (scanner == null)
            {
                return NotFound();
            }

            // Check if scanner has recent telemetry within last 5 minutes
            var latestTelemetryTime = await _context.BeaconTelemetries
                .Where(t => t.ScannerId == id)
                .OrderByDescending(t => t.ReceiveTime)
                .Select(t => (DateTime?)t.ReceiveTime)
                .FirstOrDefaultAsync();

            bool hasRecentTelemetry = false;
            if (latestTelemetryTime.HasValue)
            {
                var localLatest = AssetTracking.Web.Helpers.DateTimeHelper.EnsureLocal(latestTelemetryTime.Value);
                if ((DateTime.Now - localLatest).TotalMinutes <= 5)
                {
                    hasRecentTelemetry = true;
                }
            }

            if (hasRecentTelemetry)
            {
                ModelState.AddModelError(string.Empty, "Cannot delete scanner because it has detected beacon telemetry in the last 5 minutes.");
                ViewBag.HasRecentTelemetry = true;
                return View(scanner);
            }

            _context.Scanners.Remove(scanner);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
    }
}
