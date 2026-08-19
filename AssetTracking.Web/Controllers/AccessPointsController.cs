using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AssetTracking.Web.Data;
using AssetTracking.Web.Models;

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

            [JsonPropertyName("mapXPercent")]
            public double? MapXPercent { get; set; }

            [JsonPropertyName("mapYPercent")]
            public double? MapYPercent { get; set; }
        }

        public class AccessPointMapPositionDto
        {
            [JsonPropertyName("scannerId")]
            public string ScannerId { get; set; } = string.Empty;

            [JsonPropertyName("mapXPercent")]
            public double MapXPercent { get; set; }

            [JsonPropertyName("mapYPercent")]
            public double MapYPercent { get; set; }
        }

        // PUT /api/accesspoints/{scannerId}/position
        [HttpPut("/api/accesspoints/{scannerId}/position")]
        public async Task<IActionResult> UpdatePosition(string scannerId, [FromBody] PositionUpdateRequest request)
        {
            if (request == null)
            {
                return BadRequest(new { error = "Invalid request payload." });
            }

            double? xVal = request.XPercent ?? request.MapXPercent;
            double? yVal = request.YPercent ?? request.MapYPercent;

            if (!xVal.HasValue || !yVal.HasValue)
            {
                return BadRequest(new { error = "xPercent and yPercent are required." });
            }

            double x = xVal.Value;
            double y = yVal.Value;

            if (x < 0 || x > 100 || y < 0 || y > 100)
            {
                return BadRequest(new { error = "Coordinates must be between 0 and 100." });
            }

            var scanner = await _context.Scanners.FindAsync(scannerId);
            if (scanner == null)
            {
                return NotFound(new { error = $"Access Point with ID '{scannerId}' does not exist." });
            }

            scanner.MapXPercent = x;
            scanner.MapYPercent = y;

            _context.Update(scanner);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Successfully updated position for Access Point '{ScannerId}' to (X: {XPercent}%, Y: {YPercent}%)", scannerId, x, y);

            return Ok(new { message = "Position updated successfully." });
        }

        // POST /api/accesspoints/map-positions
        [HttpPost("/api/accesspoints/map-positions")]
        public async Task<IActionResult> SaveMapPositions([FromBody] List<AccessPointMapPositionDto> positions)
        {
            if (positions == null || !positions.Any())
            {
                return BadRequest(new { error = "Positions payload cannot be empty." });
            }

            int updatedCount = 0;
            foreach (var dto in positions)
            {
                if (string.IsNullOrWhiteSpace(dto.ScannerId)) continue;
                if (dto.MapXPercent < 0 || dto.MapXPercent > 100 || dto.MapYPercent < 0 || dto.MapYPercent > 100) continue;

                var scanner = await _context.Scanners.FindAsync(dto.ScannerId);
                if (scanner != null)
                {
                    scanner.MapXPercent = dto.MapXPercent;
                    scanner.MapYPercent = dto.MapYPercent;
                    _context.Update(scanner);
                    updatedCount++;
                }
            }

            await _context.SaveChangesAsync();
            _logger.LogInformation("Saved positions for {Count} Access Point(s).", updatedCount);

            return Ok(new { message = $"Successfully updated {updatedCount} Access Point positions." });
        }
    }
}
