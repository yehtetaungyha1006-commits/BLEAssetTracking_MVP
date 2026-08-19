using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AssetTracking.Web.Data;
using AssetTracking.Web.DTOs;
using AssetTracking.Web.Models;

namespace AssetTracking.Web.Controllers
{
    public class BuildingsController : Controller
    {
        private readonly AppDbContext _context;

        public BuildingsController(AppDbContext context)
        {
            _context = context;
        }

        // ==========================================================================
        // MVC VIEW ACTIONS
        // ==========================================================================

        // GET: /Buildings
        public async Task<IActionResult> Index([FromQuery] bool showInactive = false)
        {
            var query = _context.Buildings
                .AsNoTracking()
                .Include(b => b.Floors)
                .AsQueryable();

            if (!showInactive)
            {
                query = query.Where(b => b.IsActive);
            }

            var buildings = await query
                .OrderBy(b => b.BuildingName)
                .ToListAsync();

            var scannerCounts = await _context.Scanners
                .AsNoTracking()
                .GroupBy(s => s.BuildingId)
                .Select(g => new { BuildingId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.BuildingId ?? 0, x => x.Count);

            ViewBag.ScannerCounts = scannerCounts;
            ViewBag.ShowInactive = showInactive;

            return View(buildings);
        }

        // GET: /Buildings/Create
        public IActionResult Create()
        {
            return View(new Building());
        }

        // POST: /Buildings/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("BuildingName,Description,IsActive")] Building building)
        {
            ModelState.Remove(nameof(Building.Floors));
            ModelState.Remove("Floors");

            string trimmedName = building.BuildingName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmedName))
            {
                ModelState.AddModelError(nameof(building.BuildingName), "Building Name is required.");
            }
            else
            {
                bool exists = await _context.Buildings
                    .AsNoTracking()
                    .AnyAsync(b => b.BuildingName.ToLower() == trimmedName.ToLower() && b.IsActive);

                if (exists)
                {
                    ModelState.AddModelError(nameof(building.BuildingName), $"A building named '{trimmedName}' already exists.");
                }
            }

            if (ModelState.IsValid)
            {
                building.BuildingName = trimmedName;
                building.Description = building.Description?.Trim();
                building.IsActive = true;
                building.CreatedAt = DateTime.Now;

                _context.Add(building);
                await _context.SaveChangesAsync();

                TempData["SuccessMessage"] = $"Building '{trimmedName}' added successfully.";
                return RedirectToAction(nameof(Index));
            }

            return View(building);
        }

        // GET: /Buildings/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();

            var building = await _context.Buildings
                .Include(b => b.Floors)
                .FirstOrDefaultAsync(b => b.BuildingId == id);

            if (building == null) return NotFound();

            return View(building);
        }

        // POST: /Buildings/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("BuildingId,BuildingName,Description,IsActive")] Building building)
        {
            if (id != building.BuildingId) return NotFound();

            ModelState.Remove(nameof(Building.Floors));
            ModelState.Remove("Floors");

            string trimmedName = building.BuildingName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmedName))
            {
                ModelState.AddModelError(nameof(building.BuildingName), "Building Name is required.");
            }
            else
            {
                bool exists = await _context.Buildings
                    .AsNoTracking()
                    .AnyAsync(b => b.BuildingId != id && b.BuildingName.ToLower() == trimmedName.ToLower() && b.IsActive);

                if (exists)
                {
                    ModelState.AddModelError(nameof(building.BuildingName), $"A building named '{trimmedName}' already exists.");
                }
            }

            // Safety check when deactivating
            if (!building.IsActive)
            {
                int scannerCount = await _context.Scanners
                    .AsNoTracking()
                    .CountAsync(s => s.BuildingId == id || (s.Building != null && s.Building.ToLower() == trimmedName.ToLower()));

                int activeFloorsCount = await _context.Floors
                    .AsNoTracking()
                    .CountAsync(f => f.BuildingId == id && f.IsActive);

                if (scannerCount > 0 || activeFloorsCount > 0)
                {
                    ViewBag.WarningMessage = $"This building contains {activeFloorsCount} floor(s) and {scannerCount} Access Point(s). Deactivating it will hide associated floors and access points.";
                }
            }

            if (ModelState.IsValid)
            {
                try
                {
                    var existingBuilding = await _context.Buildings.FindAsync(id);
                    if (existingBuilding == null) return NotFound();

                    existingBuilding.BuildingName = trimmedName;
                    existingBuilding.Description = building.Description?.Trim();
                    existingBuilding.IsActive = building.IsActive;

                    _context.Update(existingBuilding);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!BuildingExists(building.BuildingId)) return NotFound();
                    else throw;
                }

                TempData["SuccessMessage"] = $"Building '{trimmedName}' updated successfully.";
                return RedirectToAction(nameof(Index));
            }

            return View(building);
        }

        // GET: /Buildings/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null) return NotFound();

            var building = await _context.Buildings
                .Include(b => b.Floors)
                .FirstOrDefaultAsync(b => b.BuildingId == id);

            if (building == null) return NotFound();

            int scannerCount = await _context.Scanners
                .AsNoTracking()
                .CountAsync(s => s.BuildingId == id || (s.Building != null && s.Building.ToLower() == building.BuildingName.ToLower()));

            int activeFloorsCount = building.Floors.Count(f => f.IsActive);

            ViewBag.ScannerCount = scannerCount;
            ViewBag.FloorsCount = activeFloorsCount;

            return View(building);
        }

        // POST: /Buildings/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var building = await _context.Buildings
                .Include(b => b.Floors)
                .FirstOrDefaultAsync(b => b.BuildingId == id);

            if (building == null) return NotFound();

            int scannerCount = await _context.Scanners
                .AsNoTracking()
                .CountAsync(s => s.BuildingId == id || (s.Building != null && s.Building.ToLower() == building.BuildingName.ToLower()));

            int activeFloorsCount = building.Floors.Count(f => f.IsActive);

            if (scannerCount > 0)
            {
                TempData["ErrorMessage"] = $"Cannot delete building '{building.BuildingName}' because {scannerCount} Access Point(s) are assigned to it. Reassign or remove those Access Points first.";
                return RedirectToAction(nameof(Index));
            }

            building.IsActive = false;
            foreach (var floor in building.Floors)
            {
                floor.IsActive = false;
            }

            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = $"Building '{building.BuildingName}' deleted successfully.";
            return RedirectToAction(nameof(Index));
        }

        private bool BuildingExists(int id)
        {
            return _context.Buildings.Any(e => e.BuildingId == id);
        }

        // ==========================================================================
        // API ENDPOINTS FOR AJAX CALLS
        // ==========================================================================

        [HttpGet("api/buildings")]
        public async Task<IActionResult> GetBuildingsApi()
        {
            var buildings = await _context.Buildings
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
                .ToListAsync();

            return Ok(buildings);
        }

        [HttpPost("api/buildings")]
        public async Task<IActionResult> CreateBuildingApi([FromBody] CreateBuildingDto dto)
        {
            if (dto == null) return BadRequest(new { message = "Invalid data payload." });

            string trimmedName = dto.BuildingName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmedName))
            {
                return BadRequest(new { message = "Building Name is required." });
            }

            bool exists = await _context.Buildings
                .AsNoTracking()
                .AnyAsync(b => b.IsActive && b.BuildingName.ToLower() == trimmedName.ToLower());

            if (exists)
            {
                return BadRequest(new { message = $"A building named '{trimmedName}' already exists." });
            }

            var building = new Building
            {
                BuildingName = trimmedName,
                Description = dto.Description?.Trim(),
                IsActive = true,
                CreatedAt = DateTime.Now
            };

            _context.Buildings.Add(building);
            await _context.SaveChangesAsync();

            return Ok(new BuildingDto
            {
                BuildingId = building.BuildingId,
                BuildingName = building.BuildingName,
                Description = building.Description,
                IsActive = building.IsActive,
                Floors = new()
            });
        }
    }
}
