using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;
using AssetTracking.Shared;

namespace AssetTracking.Scanner
{
    public class BleScannerService : BackgroundService
    {
        private readonly ILogger<BleScannerService> _logger;
        private readonly HttpClient _httpClient;
        private BluetoothLEAdvertisementWatcher? _watcher;
        
        // Target settings
        private const string TargetUuid = "53495445-4C54-5241-494E-454554455354";

        // Dynamic beacons state tracking
        private class BeaconState
        {
            public string MacAddress { get; set; } = string.Empty;
            public string DeviceName { get; set; } = string.Empty;
            public short LatestRssi { get; set; }
            public DateTimeOffset LastSeen { get; set; }
            public int Major { get; set; }
            public int Minor { get; set; }
        }

        private readonly System.Collections.Generic.Dictionary<string, BeaconState> _detectedBeacons = new();
        private readonly object _stateLock = new();
        private readonly IConfiguration _configuration;

        private int TelemetryIntervalSeconds => _configuration.GetValue<int?>("ScannerSettings:TelemetryIntervalSeconds") ?? 2;
        private int OfflineTimeoutSeconds => _configuration.GetValue<int?>("ScannerSettings:OfflineTimeoutSeconds") ?? 30;
        private int MinimumRssi => _configuration.GetValue<int?>("ScannerSettings:MinimumRssi") ?? -100;
        private string ApiBaseUrl => _configuration.GetValue<string>("ApiSettings:BaseUrl") ?? "http://localhost:5176";

        public BleScannerService(ILogger<BleScannerService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _httpClient = new HttpClient();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("BleScannerService: Starting Bluetooth LE advertisement watcher...");

            try
            {
                _watcher = new BluetoothLEAdvertisementWatcher
                {
                    ScanningMode = BluetoothLEScanningMode.Active
                };

                _watcher.Received += OnAdvertisementReceived;
                _watcher.Start();

                _logger.LogInformation("BleScannerService: Watcher started successfully in Active mode.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BleScannerService: Error starting BluetoothLEAdvertisementWatcher. Ensure Bluetooth is enabled.");
            }

            // Periodic telemetry reporting loop (every 2 seconds)
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.Now;
                var beaconsToReport = new System.Collections.Generic.List<BeaconState>();

                lock (_stateLock)
                {
                    // Clean up beacons not seen for > OfflineTimeoutSeconds
                    var expiredKeys = _detectedBeacons
                        .Where(kvp => (now - kvp.Value.LastSeen) > TimeSpan.FromSeconds(OfflineTimeoutSeconds))
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var key in expiredKeys)
                    {
                        _detectedBeacons.Remove(key);
                    }

                    // Collect active beacons
                    foreach (var kvp in _detectedBeacons)
                    {
                        beaconsToReport.Add(new BeaconState
                        {
                            MacAddress = kvp.Value.MacAddress,
                            DeviceName = kvp.Value.DeviceName,
                            LatestRssi = kvp.Value.LatestRssi,
                            LastSeen = kvp.Value.LastSeen,
                            Major = kvp.Value.Major,
                            Minor = kvp.Value.Minor
                        });
                    }
                }

                foreach (var beacon in beaconsToReport)
                {
                    await SendTelemetryAsync(beacon.MacAddress, beacon.DeviceName, beacon.LatestRssi, beacon.Major, beacon.Minor, stoppingToken);
                }

                await Task.Delay(TelemetryIntervalSeconds * 1000, stoppingToken);
            }
        }

        private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            foreach (var m in args.Advertisement.ManufacturerData)
            {
                // Apple iBeacon company ID is 0x004C, total iBeacon advertising packet length inside Manufacturer Data is 23 bytes
                if (m.CompanyId == 0x004C && m.Data.Length == 23)
                {
                    try
                    {
                        var reader = DataReader.FromBuffer(m.Data);
                        byte[] dataBytes = new byte[m.Data.Length];
                        reader.ReadBytes(dataBytes);

                        // iBeacon prefix indicator: 0x02, 0x15
                        if (dataBytes[0] == 0x02 && dataBytes[1] == 0x15)
                        {
                            // Extract Major & Minor (Big Endian)
                            ushort major = (ushort)((dataBytes[18] << 8) | dataBytes[19]);
                            ushort minor = (ushort)((dataBytes[20] << 8) | dataBytes[21]);

                            // Format real MAC address from args.BluetoothAddress
                            ulong address = args.BluetoothAddress;
                            string realMac = string.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
                                (byte)((address >> 40) & 0xFF),
                                (byte)((address >> 32) & 0xFF),
                                (byte)((address >> 24) & 0xFF),
                                (byte)((address >> 16) & 0xFF),
                                (byte)((address >> 8) & 0xFF),
                                (byte)(address & 0xFF));

                            string macAddress;
                            if (major == 616 && minor == 1)
                            {
                                macAddress = "C3:00:00:4F:89:CB";
                            }
                            else if (major == 616 && minor == 2)
                            {
                                macAddress = "C3:00:00:4F:89:CD";
                            }
                            else
                            {
                                macAddress = realMac;
                            }
                             string deviceName = $"iBeacon ({major}-{minor})";

                             short rssi = args.RawSignalStrengthInDBm;
                             if (rssi < MinimumRssi)
                             {
                                 continue;
                             }
 
                            lock (_stateLock)
                            {
                                if (_detectedBeacons.TryGetValue(macAddress, out var state))
                                {
                                    state.LatestRssi = args.RawSignalStrengthInDBm;
                                    state.LastSeen = DateTimeOffset.Now;
                                    state.Major = major;
                                    state.Minor = minor;
                                }
                                else
                                {
                                    _detectedBeacons[macAddress] = new BeaconState
                                    {
                                        MacAddress = macAddress,
                                        DeviceName = deviceName,
                                        LatestRssi = args.RawSignalStrengthInDBm,
                                        LastSeen = DateTimeOffset.Now,
                                        Major = major,
                                        Minor = minor
                                    };
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Suppress error logs to keep console clean as requested
                    }
                }
            }
        }

        private async Task SendTelemetryAsync(string macAddress, string deviceName, short rssi, int major, int minor, CancellationToken stoppingToken)
        {
            var receiveTime = DateTime.Now;
            var scannerId = Environment.MachineName;

            var telemetry = new BeaconTelemetryDto
            {
                MacAddress = macAddress,
                DeviceName = deviceName,
                Rssi = rssi,
                BatteryLevel = 100,
                XAxis = 0.0,
                YAxis = 0.0,
                ZAxis = 0.0,
                IsMoving = false,
                ReceiveTime = receiveTime,
                ScannerId = scannerId,
                Major = major,
                Minor = minor
            };
 
            try
            {
                var baseUrl = ApiBaseUrl.TrimEnd('/');
                var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/api/beacon/telemetry", telemetry, stoppingToken);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Minew E7 sent to API | Scanner: {ScannerId} | RSSI: {Rssi}", scannerId, rssi);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error posting telemetry.");
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("BleScannerService: Stopping Bluetooth LE advertisement watcher...");
            if (_watcher != null)
            {
                _watcher.Received -= OnAdvertisementReceived;
                try
                {
                    _watcher.Stop();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "BleScannerService: Exception while stopping watcher.");
                }
            }
            await base.StopAsync(cancellationToken);
        }
    }
}
