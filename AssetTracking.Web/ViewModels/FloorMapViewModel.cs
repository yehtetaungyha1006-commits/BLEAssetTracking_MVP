using System;
using System.Collections.Generic;
using AssetTracking.Web.DTOs;

namespace AssetTracking.Web.ViewModels
{
    /// <summary>
    /// View Model representing the real-time data payload for the Floor Map module.
    /// </summary>
    public class FloorMapViewModel
    {
        public List<FloorMapScannerDto> Scanners { get; set; } = new();
        public List<FloorMapBeaconDto> Beacons { get; set; } = new();
        public List<BuildingDto> BuildingList { get; set; } = new();
        public List<FloorDto> FloorList { get; set; } = new();
        
        // Legacy list support
        public List<string> Buildings { get; set; } = new();
        public List<string> Floors { get; set; } = new();
    }

    /// <summary>
    /// Data transfer object representing an Access Point / Scanner on the Floor Map.
    /// </summary>
    public class FloorMapScannerDto
    {
        public string ScannerId { get; set; } = string.Empty;
        public string AccessPointId => ScannerId;

        public string ScannerName { get; set; } = string.Empty;
        public string AccessPointName => ScannerName;

        public int? BuildingId { get; set; }
        public int? FloorId { get; set; }
        public string Building { get; set; } = string.Empty;
        public string Floor { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public bool IsOnline { get; set; }
        public double? MapXPercent { get; set; }
        public double? MapYPercent { get; set; }
    }

    /// <summary>
    /// Data transfer object representing a Beacon device's latest state on the Floor Map.
    /// </summary>
    public class FloorMapBeaconDto
    {
        public int BeaconId { get; set; }
        public string DeviceName { get; set; } = string.Empty;
        public string MacAddress { get; set; } = string.Empty;

        public string? ScannerId { get; set; }
        public string? AccessPointId => ScannerId;

        public string? ScannerName { get; set; }
        public string? AccessPointName => ScannerName;

        public int? BuildingId { get; set; }
        public int? FloorId { get; set; }
        public string? Building { get; set; }
        public string? Floor { get; set; }
        public string? Location { get; set; }
        public int Rssi { get; set; }
        public int BatteryLevel { get; set; }
        public bool IsMoving { get; set; }
        public string LastSeen { get; set; } = string.Empty;
        public DateTime? RawLastSeen { get; set; }
        public bool IsOnline { get; set; }
        public string Status { get; set; } = "Offline";
    }
}
