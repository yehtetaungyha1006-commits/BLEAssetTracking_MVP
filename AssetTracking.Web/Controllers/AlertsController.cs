using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AssetTracking.Web.Data;
using AssetTracking.Web.Models;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace AssetTracking.Web.Controllers
{
    [Route("Alerts")]
    public class AlertsController : Controller
    {
        private readonly AppDbContext _context;

        public AlertsController(AppDbContext context)
        {
            _context = context;
        }

        // GET: /Alerts
        public async Task<IActionResult> Index(string search, string alertType, string severity, string status, int page = 1)
        {
            const int pageSize = 20;

            IQueryable<AlertLog> query = _context.AlertLogs
                .Include(a => a.Device)
                .Include(a => a.Scanner)
                .AsNoTracking();

            // Search by DeviceName, MAC Address or ScannerId
            if (!string.IsNullOrWhiteSpace(search))
            {
                var lowerSearch = search.ToLower();
                query = query.Where(a => 
                    (a.Device != null && a.Device.DeviceName != null && a.Device.DeviceName.ToLower().Contains(lowerSearch)) ||
                    (a.Device != null && a.Device.MacAddress.ToLower().Contains(lowerSearch)) ||
                    (a.ScannerId != null && a.ScannerId.ToLower().Contains(lowerSearch))
                );
            }

            // Filter by Alert Type
            if (!string.IsNullOrWhiteSpace(alertType))
            {
                query = query.Where(a => a.AlertType == alertType);
            }

            // Filter by Severity
            if (!string.IsNullOrWhiteSpace(severity))
            {
                query = query.Where(a => a.Severity == severity);
            }

            // Filter by Status (Active/Resolved)
            if (!string.IsNullOrWhiteSpace(status))
            {
                bool isResolved = status.Equals("Resolved", StringComparison.OrdinalIgnoreCase);
                query = query.Where(a => a.IsResolved == isResolved);
            }

            // Order by AlertTime descending, then by AlertId descending
            query = query.OrderByDescending(a => a.AlertTime).ThenByDescending(a => a.AlertId);

            // Pagination calculations
            int totalItems = await query.CountAsync();
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
            page = Math.Max(1, Math.Min(page, totalPages > 0 ? totalPages : 1));

            var alerts = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            // Get unique alert types for filter dropdown
            var alertTypes = await _context.AlertLogs
                .Select(a => a.AlertType)
                .Distinct()
                .ToListAsync();

            ViewBag.Search = search;
            ViewBag.AlertType = alertType;
            ViewBag.Severity = severity;
            ViewBag.Status = status;
            ViewBag.AlertTypes = alertTypes;
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.TotalItems = totalItems;

            return View(alerts);
        }
    }
}
