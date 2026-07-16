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
        private readonly IConfiguration _configuration;

        public ScannersController(AppDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        // GET: Scanners
        public async Task<IActionResult> Index()
        {
            var scanners = await _context.Scanners.ToListAsync();
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

            return View(scanner);
        }

        // POST: Scanners/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, [Bind("ScannerId,Building,Floor,Location")] ScannerDevice scanner)
        {
            if (id != scanner.ScannerId)
            {
                return NotFound();
            }

            // Perform manual validations on non-empty fields
            if (string.IsNullOrWhiteSpace(scanner.Building))
            {
                ModelState.AddModelError(nameof(scanner.Building), "Building is required.");
            }
            if (string.IsNullOrWhiteSpace(scanner.Floor))
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

                    // Update only Building, Floor, Location
                    existingScanner.Building = scanner.Building;
                    existingScanner.Floor = scanner.Floor;
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
            return View(scanner);
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
