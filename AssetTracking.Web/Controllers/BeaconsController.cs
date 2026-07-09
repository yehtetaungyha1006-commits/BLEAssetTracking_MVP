using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AssetTracking.Web.Data;
using AssetTracking.Web.Models;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace AssetTracking.Web.Controllers
{
    public class BeaconsController : Controller
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _configuration;

        public BeaconsController(AppDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        // GET: Beacons
        public async Task<IActionResult> Index()
        {
            var beacons = await _context.BeaconDevices.AsNoTracking().ToListAsync();
            foreach (var beacon in beacons)
            {
                beacon.Status = AssetTracking.Web.Helpers.DateTimeHelper.IsOnline(beacon.LastSeen) ? "Online" : "Offline";
            }
            return View(beacons);
        }

        // GET: Beacons/Create
        public IActionResult Create()
        {
            return View();
        }

        // POST: Beacons/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("MacAddress,DeviceName,Major,Minor")] BeaconDevice beacon)
        {
            // Validation: required fields
            if (string.IsNullOrWhiteSpace(beacon.MacAddress))
            {
                ModelState.AddModelError(nameof(beacon.MacAddress), "MAC Address is required.");
            }
            
            if (beacon.Major < 0)
            {
                ModelState.AddModelError(nameof(beacon.Major), "Major value must be non-negative.");
            }

            if (beacon.Minor < 0)
            {
                ModelState.AddModelError(nameof(beacon.Minor), "Minor value must be non-negative.");
            }

            // Validation: unique constraints
            if (ModelState.IsValid)
            {
                if (await _context.BeaconDevices.AnyAsync(b => b.MacAddress == beacon.MacAddress))
                {
                    ModelState.AddModelError(nameof(beacon.MacAddress), "MAC Address already exists.");
                }

                if (await _context.BeaconDevices.AnyAsync(b => b.Major == beacon.Major && b.Minor == beacon.Minor))
                {
                    ModelState.AddModelError(nameof(beacon.Major), "Combination of Major and Minor must be unique.");
                    ModelState.AddModelError(nameof(beacon.Minor), "Combination of Major and Minor must be unique.");
                }
            }

            if (ModelState.IsValid)
            {
                beacon.Status = "Offline";
                beacon.CreatedAt = DateTime.Now;

                _context.Add(beacon);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            return View(beacon);
        }

        // GET: Beacons/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var beacon = await _context.BeaconDevices.FindAsync(id);
            if (beacon == null)
            {
                return NotFound();
            }
            beacon.Status = AssetTracking.Web.Helpers.DateTimeHelper.GetBeaconDisplayStatus(beacon.LastSeen);
            return View(beacon);
        }

        // POST: Beacons/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("DeviceId,MacAddress,DeviceName,Major,Minor")] BeaconDevice beacon)
        {
            if (id != beacon.DeviceId)
            {
                return NotFound();
            }

            // Validation: required fields
            if (string.IsNullOrWhiteSpace(beacon.MacAddress))
            {
                ModelState.AddModelError(nameof(beacon.MacAddress), "MAC Address is required.");
            }

            if (beacon.Major < 0)
            {
                ModelState.AddModelError(nameof(beacon.Major), "Major value must be non-negative.");
            }

            if (beacon.Minor < 0)
            {
                ModelState.AddModelError(nameof(beacon.Minor), "Minor value must be non-negative.");
            }

            // Validation: unique constraints ignoring self
            if (ModelState.IsValid)
            {
                if (await _context.BeaconDevices.AnyAsync(b => b.MacAddress == beacon.MacAddress && b.DeviceId != id))
                {
                    ModelState.AddModelError(nameof(beacon.MacAddress), "MAC Address already exists.");
                }

                if (await _context.BeaconDevices.AnyAsync(b => b.Major == beacon.Major && b.Minor == beacon.Minor && b.DeviceId != id))
                {
                    ModelState.AddModelError(nameof(beacon.Major), "Combination of Major and Minor must be unique.");
                    ModelState.AddModelError(nameof(beacon.Minor), "Combination of Major and Minor must be unique.");
                }
            }

            if (ModelState.IsValid)
            {
                try
                {
                    var existingBeacon = await _context.BeaconDevices.FindAsync(id);
                    if (existingBeacon == null)
                    {
                        return NotFound();
                    }

                    // Update permitted fields
                    existingBeacon.DeviceName = beacon.DeviceName;
                    existingBeacon.MacAddress = beacon.MacAddress;
                    existingBeacon.Major = beacon.Major;
                    existingBeacon.Minor = beacon.Minor;

                    _context.Update(existingBeacon);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!BeaconExists(beacon.DeviceId))
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
            var existing = await _context.BeaconDevices.AsNoTracking().FirstOrDefaultAsync(b => b.DeviceId == id);
            if (existing != null)
            {
                beacon.LastSeen = existing.LastSeen;
                beacon.CreatedAt = existing.CreatedAt;
            }
            beacon.Status = AssetTracking.Web.Helpers.DateTimeHelper.GetBeaconDisplayStatus(beacon.LastSeen);
            return View(beacon);
        }

        // GET: Beacons/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var beacon = await _context.BeaconDevices
                .FirstOrDefaultAsync(m => m.DeviceId == id);
            if (beacon == null)
            {
                return NotFound();
            }
            beacon.Status = AssetTracking.Web.Helpers.DateTimeHelper.GetBeaconDisplayStatus(beacon.LastSeen);
            return View(beacon);
        }

        // POST: Beacons/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var beacon = await _context.BeaconDevices.FindAsync(id);
            if (beacon != null)
            {
                _context.BeaconDevices.Remove(beacon);
                await _context.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }

        private bool BeaconExists(int id)
        {
            return _context.BeaconDevices.Any(e => e.DeviceId == id);
        }
    }
}
