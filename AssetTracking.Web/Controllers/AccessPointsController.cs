using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AssetTracking.Web.Data;
using AssetTracking.Web.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AssetTracking.Web.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AccessPointsController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILogger<AccessPointsController> _logger;

        public AccessPointsController(AppDbContext context, ILogger<AccessPointsController> logger)
        {
            _context = context;
            _logger = logger;
        }

        public class PositionUpdateRequest
        {
            [JsonPropertyName("xPercent")]
            public double? XPercent { get; set; }

            [JsonPropertyName("yPercent")]
            public double? YPercent { get; set; }
        }

        [HttpPut("/api/accesspoints/{scannerId}/position")]
        public async Task<IActionResult> UpdatePosition(string scannerId, [FromBody] PositionUpdateRequest request)
        {
            if (request == null || !request.XPercent.HasValue || !request.YPercent.HasValue)
            {
                return BadRequest(new { error = "Invalid request payload. xPercent and yPercent are required." });
            }

            double x = request.XPercent.Value;
            double y = request.YPercent.Value;

            if (x < 0 || x > 100 || y < 0 || y > 100)
            {
                return BadRequest(new { error = "Coordinates must be between 0 and 100." });
            }

            var scanner = await _context.Scanners.FindAsync(scannerId);
            if (scanner == null)
            {
                return NotFound(new { error = $"Access Point with ID '{scannerId}' does not exist." });
            }

            // Update only coordinates
            scanner.MapXPercent = x;
            scanner.MapYPercent = y;

            _context.Update(scanner);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Successfully updated position for Access Point '{ScannerId}' to (X: {XPercent}%, Y: {YPercent}%)", scannerId, x, y);

            return Ok(new { message = "Position updated successfully." });
        }
    }
}
