using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using AssetTracking.Shared;

namespace AssetTracking.Scanner
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly Random _random = new();
        private int _tickCount = 0;

        // Device simulation list
        private readonly List<BeaconSimulationState> _devices = new()
        {
            new BeaconSimulationState("00:11:22:33:44:55", "Beacon-01", 95, 0.0, 0.0, 0.0, false, 616, 10),
            new BeaconSimulationState("00:11:22:33:44:66", "Beacon-02", 78, 10.0, 15.0, 1.2, true, 616, 20),
            new BeaconSimulationState("00:11:22:33:44:77", "Beacon-03", 15, -5.0, 8.5, 0.5, false, 616, 30),
            new BeaconSimulationState("00:11:22:33:44:88", "Beacon-04", 62, 100.0, -50.0, 0.0, false, 616, 40),
            new BeaconSimulationState("00:11:22:33:44:99", "Beacon-05", 45, 2.5, -3.2, 0.1, true, 616, 50)
        };

        public Worker(ILogger<Worker> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;

            // Configure HttpClient to ignore self-signed certificate validation errors on localhost
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            _httpClient = new HttpClient(handler);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("BLE Asset Tracker Simulator Worker started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                _tickCount++;

                foreach (var device in _devices)
                {
                    // Simulating intermittent network connectivity for Beacon-04 (00:11:22:33:44:88)
                    // We send updates for 10 ticks (20s) and skip updates for 10 ticks (20s).
                    if (device.MacAddress == "00:11:22:33:44:88")
                    {
                        int cycle = (_tickCount / 10) % 2;
                        if (cycle == 1)
                        {
                            // Skip sending telemetry to simulate going offline
                            continue;
                        }
                    }

                    // Update values for simulation
                    if (device.IsMoving)
                    {
                        // Random walk coordinate updates
                        device.X += (_random.NextDouble() - 0.5) * 2.0;
                        device.Y += (_random.NextDouble() - 0.5) * 2.0;
                        device.Z += (_random.NextDouble() - 0.5) * 0.2;
                    }

                    // Vary RSSI randomly to simulate signal fluctuation
                    device.Rssi = Math.Clamp(device.Rssi + _random.Next(-5, 6), -95, -40);

                    // Deplete battery slowly
                    if (_tickCount % 15 == 0) // Approx every 30 seconds
                    {
                        device.BatteryLevel = Math.Max(0, device.BatteryLevel - 1);
                    }

                    var telemetry = new BeaconTelemetryDto
                    {
                        MacAddress = device.MacAddress,
                        DeviceName = device.DeviceName,
                        Rssi = device.Rssi,
                        BatteryLevel = device.BatteryLevel,
                        XAxis = device.X,
                        YAxis = device.Y,
                        ZAxis = device.Z,
                        IsMoving = device.IsMoving,
                        ReceiveTime = DateTime.UtcNow,
                        ScannerId = "Scanner-Sim",
                        ScannerName = "Simulated Scanner",
                        ScannerBuilding = "B",
                        ScannerFloor = "1",
                        ScannerLocation = "Warehouse",
                        Major = device.Major,
                        Minor = device.Minor
                    };

                    try
                    {
                        var baseUrl = (_configuration.GetValue<string>("ApiSettings:BaseUrl") ?? "https://localhost:5176").TrimEnd('/');
                        var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/api/beacon/telemetry", telemetry, stoppingToken);
                        if (response.IsSuccessStatusCode)
                        {
                            _logger.LogInformation("Sent telemetry for {DeviceName} ({MacAddress})", telemetry.DeviceName, telemetry.MacAddress);
                        }
                        else
                        {
                            _logger.LogWarning("Failed to send telemetry for {DeviceName}. Status: {StatusCode}", telemetry.DeviceName, response.StatusCode);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error sending telemetry for {DeviceName}", telemetry.DeviceName);
                    }
                }

                await Task.Delay(2000, stoppingToken);
            }
        }

        private class BeaconSimulationState
        {
            public string MacAddress { get; }
            public string DeviceName { get; }
            public int BatteryLevel { get; set; }
            public double X { get; set; }
            public double Y { get; set; }
            public double Z { get; set; }
            public bool IsMoving { get; set; }
            public int Rssi { get; set; }
            public int Major { get; }
            public int Minor { get; }
 
            public BeaconSimulationState(string mac, string name, int battery, double x, double y, double z, bool moving, int major, int minor)
            {
                MacAddress = mac;
                DeviceName = name;
                BatteryLevel = battery;
                X = x;
                Y = y;
                Z = z;
                IsMoving = moving;
                Rssi = -55; // Default RSSI
                Major = major;
                Minor = minor;
            }
        }
    }
}
