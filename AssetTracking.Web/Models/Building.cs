using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace AssetTracking.Web.Models
{
    public class Building
    {
        public int BuildingId { get; set; }
        public string BuildingName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // Navigation property - ignore validation during form POST model binding
        [ValidateNever]
        public ICollection<Floor> Floors { get; set; } = new List<Floor>();
    }
}
