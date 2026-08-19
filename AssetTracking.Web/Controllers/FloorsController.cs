using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AssetTracking.Web.Data;
using AssetTracking.Web.DTOs;
using AssetTracking.Web.Models;

namespace AssetTracking.Web.Controllers
{
    public class FloorsController : Controller
    {
        private readonly AppDbContext _context;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<FloorsController> _logger;

        public FloorsController(AppDbContext context, IWebHostEnvironment env, ILogger<FloorsController> logger)
        {
            _context = context;
            _env = env;
            _logger = logger;
        }

        // ==========================================================================
        // MVC VIEW ACTIONS
        // ==========================================================================

        // GET: /Floors
        public async Task<IActionResult> Index(int? buildingId, [FromQuery] bool showInactive = false)
        {
            var buildings = await _context.Buildings
                .AsNoTracking()
                .Where(b => b.IsActive)
                .OrderBy(b => b.BuildingName)
                .ToListAsync();

            ViewBag.Buildings = buildings;
            ViewBag.SelectedBuildingId = buildingId;
            ViewBag.ShowInactive = showInactive;

            var query = _context.Floors
                .AsNoTracking()
                .Include(f => f.Building)
                .AsQueryable();

            if (!showInactive)
            {
                query = query.Where(f => f.IsActive && f.Building.IsActive);
            }

            if (buildingId.HasValue && buildingId > 0)
            {
                query = query.Where(f => f.BuildingId == buildingId.Value);
            }

            var floors = await query
                .OrderBy(f => f.Building.BuildingName)
                .ThenBy(f => f.FloorNumber ?? 999)
                .ThenBy(f => f.FloorName)
                .ToListAsync();

            var scannerCounts = await _context.Scanners
                .AsNoTracking()
                .GroupBy(s => s.FloorId)
                .Select(g => new { FloorId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.FloorId ?? 0, x => x.Count);

            ViewBag.ScannerCounts = scannerCounts;

            return View(floors);
        }

        // GET: /Floors/Create
        public async Task<IActionResult> Create()
        {
            await PopulateBuildingsViewBag();
            return View(new Floor { IsActive = true });
        }

        // POST: /Floors/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("BuildingId,FloorName,FloorNumber,IsActive")] Floor floor, IFormFile? mapImageFile)
        {
            // Remove navigation property from model validation state
            ModelState.Remove(nameof(Floor.Building));
            ModelState.Remove("Building");

            string trimmedName = floor.FloorName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmedName))
            {
                ModelState.AddModelError(nameof(floor.FloorName), "Floor Name is required.");
            }

            if (floor.BuildingId <= 0)
            {
                ModelState.AddModelError(nameof(floor.BuildingId), "Building is required.");
            }
            else
            {
                var building = await _context.Buildings.AsNoTracking().FirstOrDefaultAsync(b => b.BuildingId == floor.BuildingId && b.IsActive);
                if (building == null)
                {
                    ModelState.AddModelError(nameof(floor.BuildingId), "Selected building does not exist or is inactive.");
                }
                else
                {
                    bool exists = await _context.Floors
                        .AsNoTracking()
                        .AnyAsync(f => f.BuildingId == floor.BuildingId && f.IsActive && f.FloorName.ToLower() == trimmedName.ToLower());

                    if (exists)
                    {
                        ModelState.AddModelError(nameof(floor.FloorName), $"Floor '{trimmedName}' already exists in {building.BuildingName}.");
                    }
                }
            }

            if (mapImageFile != null && mapImageFile.Length > 0)
            {
                string ext = Path.GetExtension(mapImageFile.FileName).ToLowerInvariant();
                string[] allowedExts = { ".png", ".jpg", ".jpeg", ".webp", ".svg" };
                if (!allowedExts.Contains(ext) && mapImageFile.ContentType != "image/svg+xml")
                {
                    ModelState.AddModelError("mapImageFile", "Invalid image format. Allowed formats: PNG, JPG, JPEG, WEBP, SVG.");
                }
                else if (ext == ".svg" || mapImageFile.ContentType == "image/svg+xml")
                {
                    using var streamReader = new StreamReader(mapImageFile.OpenReadStream());
                    string svgText = await streamReader.ReadToEndAsync();
                    var (isValid, errorMessage) = ValidateAndSanitizeSvg(svgText);
                    if (!isValid)
                    {
                        ModelState.AddModelError("mapImageFile", errorMessage ?? "Unsafe SVG file detected.");
                    }
                }
            }

            if (!ModelState.IsValid)
            {
                foreach (var entry in ModelState)
                {
                    foreach (var error in entry.Value.Errors)
                    {
                        _logger.LogWarning("Create Floor ModelState error on '{Key}': {Error}", entry.Key, error.ErrorMessage);
                    }
                }

                await PopulateBuildingsViewBag(floor.BuildingId);
                return View(floor);
            }

            floor.FloorName = trimmedName;
            floor.IsActive = true;
            floor.CreatedAt = DateTime.Now;

            _context.Add(floor);
            await _context.SaveChangesAsync();
            _logger.LogInformation("Saved new Floor ID {FloorId} ('{FloorName}') to database.", floor.FloorId, floor.FloorName);

            if (mapImageFile != null && mapImageFile.Length > 0)
            {
                string? savedPath = await SaveFloorMapImageFile(floor.FloorId, mapImageFile);
                if (savedPath != null)
                {
                    floor.FloorMapImagePath = savedPath;
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("Saved floor map image path '{ImagePath}' for Floor ID {FloorId}.", savedPath, floor.FloorId);
                }
            }

            TempData["SuccessMessage"] = $"Floor '{trimmedName}' added successfully.";
            return RedirectToAction(nameof(Index), new { buildingId = floor.BuildingId });
        }

        // GET: /Floors/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();

            var floor = await _context.Floors
                .Include(f => f.Building)
                .FirstOrDefaultAsync(f => f.FloorId == id);

            if (floor == null) return NotFound();

            await PopulateBuildingsViewBag(floor.BuildingId);
            return View(floor);
        }

        // POST: /Floors/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("FloorId,BuildingId,FloorName,FloorNumber,FloorMapImagePath,IsActive")] Floor floor, IFormFile? mapImageFile)
        {
            if (id != floor.FloorId) return NotFound();

            ModelState.Remove(nameof(Floor.Building));
            ModelState.Remove("Building");

            string trimmedName = floor.FloorName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmedName))
            {
                ModelState.AddModelError(nameof(floor.FloorName), "Floor Name is required.");
            }
            else
            {
                var building = await _context.Buildings.AsNoTracking().FirstOrDefaultAsync(b => b.BuildingId == floor.BuildingId && b.IsActive);
                if (building == null)
                {
                    ModelState.AddModelError(nameof(floor.BuildingId), "Selected building does not exist or is inactive.");
                }
                else
                {
                    bool exists = await _context.Floors
                        .AsNoTracking()
                        .AnyAsync(f => f.FloorId != id && f.BuildingId == floor.BuildingId && f.IsActive && f.FloorName.ToLower() == trimmedName.ToLower());

                    if (exists)
                    {
                        ModelState.AddModelError(nameof(floor.FloorName), $"Floor '{trimmedName}' already exists in {building.BuildingName}.");
                    }
                }
            }

            if (mapImageFile != null && mapImageFile.Length > 0)
            {
                string ext = Path.GetExtension(mapImageFile.FileName).ToLowerInvariant();
                string[] allowedExts = { ".png", ".jpg", ".jpeg", ".webp", ".svg" };
                if (!allowedExts.Contains(ext) && mapImageFile.ContentType != "image/svg+xml")
                {
                    ModelState.AddModelError("mapImageFile", "Invalid image format. Allowed formats: PNG, JPG, JPEG, WEBP, SVG.");
                }
                else if (ext == ".svg" || mapImageFile.ContentType == "image/svg+xml")
                {
                    using var streamReader = new StreamReader(mapImageFile.OpenReadStream());
                    string svgText = await streamReader.ReadToEndAsync();
                    var (isValid, errorMessage) = ValidateAndSanitizeSvg(svgText);
                    if (!isValid)
                    {
                        ModelState.AddModelError("mapImageFile", errorMessage ?? "Unsafe SVG file detected.");
                    }
                }
            }

            // Safety check when deactivating
            if (!floor.IsActive)
            {
                int scannerCount = await _context.Scanners
                    .AsNoTracking()
                    .CountAsync(s => s.FloorId == id || (s.BuildingId == floor.BuildingId && s.Floor != null && s.Floor.ToLower() == trimmedName.ToLower()));

                if (scannerCount > 0)
                {
                    ViewBag.WarningMessage = $"This floor is assigned to {scannerCount} Access Point(s). Deactivating it will hide associated Access Points.";
                }
            }

            if (!ModelState.IsValid)
            {
                foreach (var entry in ModelState)
                {
                    foreach (var error in entry.Value.Errors)
                    {
                        _logger.LogWarning("Edit Floor ModelState error on '{Key}': {Error}", entry.Key, error.ErrorMessage);
                    }
                }

                await PopulateBuildingsViewBag(floor.BuildingId);
                return View(floor);
            }

            try
            {
                var existingFloor = await _context.Floors.FindAsync(id);
                if (existingFloor == null) return NotFound();

                if (mapImageFile != null && mapImageFile.Length > 0)
                {
                    string? savedPath = await SaveFloorMapImageFile(id, mapImageFile);
                    if (savedPath != null)
                    {
                        existingFloor.FloorMapImagePath = savedPath;
                    }
                }

                existingFloor.BuildingId = floor.BuildingId;
                existingFloor.FloorName = trimmedName;
                existingFloor.FloorNumber = floor.FloorNumber;
                existingFloor.IsActive = floor.IsActive;

                _context.Update(existingFloor);
                await _context.SaveChangesAsync();
                _logger.LogInformation("Updated Floor ID {FloorId} ('{FloorName}') in database.", id, trimmedName);
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!FloorExists(floor.FloorId)) return NotFound();
                else throw;
            }

            TempData["SuccessMessage"] = $"Floor '{trimmedName}' updated successfully.";
            return RedirectToAction(nameof(Index), new { buildingId = floor.BuildingId });
        }

        // GET: /Floors/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null) return NotFound();

            var floor = await _context.Floors
                .Include(f => f.Building)
                .FirstOrDefaultAsync(f => f.FloorId == id);

            if (floor == null) return NotFound();

            int scannerCount = await _context.Scanners
                .AsNoTracking()
                .CountAsync(s => s.FloorId == id || (s.BuildingId == floor.BuildingId && s.Floor != null && s.Floor.ToLower() == floor.FloorName.ToLower()));

            ViewBag.ScannerCount = scannerCount;
            return View(floor);
        }

        // POST: /Floors/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var floor = await _context.Floors.Include(f => f.Building).FirstOrDefaultAsync(f => f.FloorId == id);
            if (floor == null) return NotFound();

            int scannerCount = await _context.Scanners
                .AsNoTracking()
                .CountAsync(s => s.FloorId == id || (s.BuildingId == floor.BuildingId && s.Floor != null && s.Floor.ToLower() == floor.FloorName.ToLower()));

            if (scannerCount > 0)
            {
                _logger.LogWarning("Floor delete blocked for FloorId={FloorId}, AccessPointsCount={Count}", id, scannerCount);
                TempData["ErrorMessage"] = $"Cannot delete floor '{floor.FloorName}' because {scannerCount} Access Point(s) are assigned to it. Reassign or remove Access Points first.";
                return RedirectToAction(nameof(Index), new { buildingId = floor.BuildingId });
            }

            floor.IsActive = false;
            await _context.SaveChangesAsync();
            _logger.LogInformation("Deactivated Floor ID {FloorId} ('{FloorName}').", id, floor.FloorName);

            TempData["SuccessMessage"] = $"Floor '{floor.FloorName}' deleted successfully.";
            return RedirectToAction(nameof(Index), new { buildingId = floor.BuildingId });
        }

        private async Task PopulateBuildingsViewBag(int? selectedBuildingId = null)
        {
            var buildings = await _context.Buildings
                .AsNoTracking()
                .Where(b => b.IsActive)
                .OrderBy(b => b.BuildingName)
                .ToListAsync();

            ViewBag.Buildings = buildings;
        }

        private async Task<string?> SaveFloorMapImageFile(int floorId, IFormFile file)
        {
            try
            {
                string ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                string[] allowedExts = { ".png", ".jpg", ".jpeg", ".webp", ".svg" };
                if (!allowedExts.Contains(ext) && file.ContentType != "image/svg+xml") return null;

                if (ext == ".svg" || file.ContentType == "image/svg+xml")
                {
                    using (var reader = new StreamReader(file.OpenReadStream()))
                    {
                        string svgText = await reader.ReadToEndAsync();
                        var (isValid, errorMessage) = ValidateAndSanitizeSvg(svgText);
                        if (!isValid)
                        {
                            _logger.LogWarning("Rejected unsafe SVG upload for floorId {FloorId}: {ErrorMessage}", floorId, errorMessage);
                            return null;
                        }
                    }

                    if (string.IsNullOrEmpty(ext)) ext = ".svg";
                }

                string targetDir = Path.Combine(_env.WebRootPath, "images", "floor-maps");
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                string fileName = $"floor-{(floorId > 0 ? floorId.ToString() : "temp")}-{Guid.NewGuid().ToString("N").Substring(0, 8)}{ext}";
                string filePath = Path.Combine(targetDir, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                return $"/images/floor-maps/{fileName}";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving floor map image file.");
                return null;
            }
        }

        /// <summary>
        /// Security validator & sanitizer for SVG map files to prevent XSS / script execution / external fetching.
        /// </summary>
        private static (bool IsValid, string? ErrorMessage) ValidateAndSanitizeSvg(string svgText)
        {
            if (string.IsNullOrWhiteSpace(svgText))
            {
                return (false, "Uploaded SVG file is empty.");
            }

            string lower = svgText.ToLowerInvariant();

            // 1. Reject script tags
            if (lower.Contains("<script") || lower.Contains("</script>"))
            {
                return (false, "Unsafe SVG file detected: SVG contains script tags.");
            }

            // 2. Reject foreignObject
            if (lower.Contains("<foreignobject") || lower.Contains("</foreignobject>"))
            {
                return (false, "Unsafe SVG file detected: SVG contains foreignObject elements.");
            }

            // 3. Reject javascript: URIs
            if (lower.Contains("javascript:"))
            {
                return (false, "Unsafe SVG file detected: SVG contains javascript: execution URIs.");
            }

            // 4. Reject inline event handlers (on[a-z]+=, e.g. onload=, onerror=, onclick=, etc.)
            if (Regex.IsMatch(lower, @"\bon[a-z]+\s*=", RegexOptions.IgnoreCase))
            {
                return (false, "Unsafe SVG file detected: SVG contains inline event handler attributes.");
            }

            // 5. Reject external resource references (http://, https://)
            if (Regex.IsMatch(lower, @"(href|src|xlink:href)\s*=\s*[""']?\s*https?://", RegexOptions.IgnoreCase))
            {
                return (false, "Unsafe SVG file detected: SVG contains external resource references.");
            }

            return (true, null);
        }

        private bool FloorExists(int id)
        {
            return _context.Floors.Any(e => e.FloorId == id);
        }

        // ==========================================================================
        // API ENDPOINTS FOR AJAX CALLS
        // ==========================================================================

        [HttpGet("api/floors")]
        public async Task<IActionResult> GetFloorsApi([FromQuery] int? buildingId)
        {
            var query = _context.Floors
                .AsNoTracking()
                .Include(f => f.Building)
                .Where(f => f.IsActive && f.Building.IsActive);

            if (buildingId.HasValue && buildingId.Value > 0)
            {
                query = query.Where(f => f.BuildingId == buildingId.Value);
            }

            var floors = await query
                .OrderBy(f => f.FloorNumber ?? 999)
                .ThenBy(f => f.FloorName)
                .Select(f => new FloorDto
                {
                    FloorId = f.FloorId,
                    BuildingId = f.BuildingId,
                    BuildingName = f.Building.BuildingName,
                    FloorName = f.FloorName,
                    FloorNumber = f.FloorNumber,
                    FloorMapImagePath = f.FloorMapImagePath,
                    IsActive = f.IsActive
                })
                .ToListAsync();

            return Ok(floors);
        }

        [HttpPost("api/floors")]
        public async Task<IActionResult> CreateFloorApi([FromBody] CreateFloorDto dto)
        {
            if (dto == null) return BadRequest(new { message = "Invalid data payload." });

            string trimmedName = dto.FloorName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmedName)) return BadRequest(new { message = "Floor Name is required." });

            var building = await _context.Buildings.FindAsync(dto.BuildingId);
            if (building == null || !building.IsActive) return BadRequest(new { message = "Selected building does not exist." });

            bool exists = await _context.Floors.AsNoTracking().AnyAsync(f => f.BuildingId == dto.BuildingId && f.IsActive && f.FloorName.ToLower() == trimmedName.ToLower());
            if (exists) return BadRequest(new { message = $"Floor '{trimmedName}' already exists in {building.BuildingName}." });

            var floor = new Floor
            {
                BuildingId = dto.BuildingId,
                FloorName = trimmedName,
                FloorNumber = dto.FloorNumber,
                IsActive = true,
                CreatedAt = DateTime.Now
            };

            _context.Floors.Add(floor);
            await _context.SaveChangesAsync();

            return Ok(new FloorDto
            {
                FloorId = floor.FloorId,
                BuildingId = floor.BuildingId,
                BuildingName = building.BuildingName,
                FloorName = floor.FloorName,
                FloorNumber = floor.FloorNumber,
                FloorMapImagePath = floor.FloorMapImagePath,
                IsActive = floor.IsActive
            });
        }

        [HttpPost("api/floors/{floorId:int}/map-image")]
        public async Task<IActionResult> UploadMapImageApi(int floorId, IFormFile file)
        {
            if (file == null || file.Length == 0) return BadRequest(new { message = "Please select a floor map image file to upload." });

            string ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            string[] allowedExts = { ".png", ".jpg", ".jpeg", ".webp", ".svg" };
            if (!allowedExts.Contains(ext) && file.ContentType != "image/svg+xml")
            {
                return BadRequest(new { message = "Invalid image format. Allowed formats: PNG, JPG, JPEG, WEBP, SVG." });
            }

            if (ext == ".svg" || file.ContentType == "image/svg+xml")
            {
                using var streamReader = new StreamReader(file.OpenReadStream());
                string svgText = await streamReader.ReadToEndAsync();
                var (isValid, errorMessage) = ValidateAndSanitizeSvg(svgText);
                if (!isValid)
                {
                    return BadRequest(new { message = errorMessage ?? "Unsafe SVG file detected." });
                }
            }

            var floor = await _context.Floors.Include(f => f.Building).FirstOrDefaultAsync(f => f.FloorId == floorId && f.IsActive);
            if (floor == null) return NotFound(new { message = "Floor not found." });

            string? savedPath = await SaveFloorMapImageFile(floorId, file);
            if (savedPath == null) return BadRequest(new { message = "Invalid image file or upload failed." });

            floor.FloorMapImagePath = savedPath;
            await _context.SaveChangesAsync();

            return Ok(new FloorDto
            {
                FloorId = floor.FloorId,
                BuildingId = floor.BuildingId,
                BuildingName = floor.Building.BuildingName,
                FloorName = floor.FloorName,
                FloorNumber = floor.FloorNumber,
                FloorMapImagePath = floor.FloorMapImagePath,
                IsActive = floor.IsActive
            });
        }
    }
}
