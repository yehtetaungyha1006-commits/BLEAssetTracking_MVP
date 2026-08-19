using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace AssetTracking.Web.Models
{
    public class Floor
    {
        public int FloorId { get; set; }
        public int BuildingId { get; set; }

        public string FloorName { get; set; } = string.Empty;
        public int? FloorNumber { get; set; }

        public string? FloorMapImagePath { get; set; }

        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // Navigation property - ignore validation during form POST model binding
        [ValidateNever]
        public Building Building { get; set; } = null!;
    }
}
