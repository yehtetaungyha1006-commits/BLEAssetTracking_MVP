using System.ComponentModel.DataAnnotations;

namespace AssetTracking.Web.DTOs
{
    public class FloorDto
    {
        public int FloorId { get; set; }
        public int BuildingId { get; set; }
        public string BuildingName { get; set; } = string.Empty;
        public string FloorName { get; set; } = string.Empty;
        public int? FloorNumber { get; set; }
        public string? FloorMapImagePath { get; set; }
        public bool IsActive { get; set; }
    }

    public class CreateFloorDto
    {
        [Required(ErrorMessage = "Building is required.")]
        public int BuildingId { get; set; }

        [Required(ErrorMessage = "Floor Name is required.")]
        [StringLength(100, ErrorMessage = "Floor Name cannot exceed 100 characters.")]
        public string FloorName { get; set; } = string.Empty;

        public int? FloorNumber { get; set; }
    }
}
