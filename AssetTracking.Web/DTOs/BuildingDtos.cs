using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace AssetTracking.Web.DTOs
{
    public class BuildingDto
    {
        public int BuildingId { get; set; }
        public string BuildingName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsActive { get; set; }
        public List<FloorDto> Floors { get; set; } = new();
    }

    public class CreateBuildingDto
    {
        [Required(ErrorMessage = "Building Name is required.")]
        [StringLength(100, ErrorMessage = "Building Name cannot exceed 100 characters.")]
        public string BuildingName { get; set; } = string.Empty;

        [StringLength(250, ErrorMessage = "Description cannot exceed 250 characters.")]
        public string? Description { get; set; }
    }
}
