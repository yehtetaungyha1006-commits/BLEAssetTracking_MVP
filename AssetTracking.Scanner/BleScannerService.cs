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
            public double XAxis { get; set; }
            public double YAxis { get; set; }
            public double ZAxis { get; set; }
            public bool IsMoving { get; set; }
            public double LastMagnitude { get; set; }
            public bool HasMotionData { get; set; }
        }

        private readonly System.Collections.Generic.Dictionary<string, BeaconState> _detectedBeacons = new();
        private readonly object _stateLock = new();
        private readonly IConfiguration _configuration;
        private bool _loggedNoMotion = false;

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
                            Minor = kvp.Value.Minor,
                            XAxis = kvp.Value.XAxis,
                            YAxis = kvp.Value.YAxis,
                            ZAxis = kvp.Value.ZAxis,
                            IsMoving = kvp.Value.IsMoving,
                            HasMotionData = kvp.Value.HasMotionData
                        });
                    }
                }

                foreach (var beacon in beaconsToReport)
                {
                    await SendTelemetryAsync(
                        beacon.MacAddress, 
                        beacon.DeviceName, 
                        beacon.LatestRssi, 
                        beacon.Major, 
                        beacon.Minor, 
                        beacon.XAxis, 
                        beacon.YAxis, 
                        beacon.ZAxis, 
                        beacon.IsMoving, 
                        beacon.HasMotionData, 
                        stoppingToken);
                }

                await Task.Delay(TelemetryIntervalSeconds * 1000, stoppingToken);
            }
        }

        private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            // Try parsing Minew E7 Sensor Frame (0xA1)
            bool foundE7Frame = false;
            byte[]? e7Bytes = null;

            foreach (var m in args.Advertisement.ManufacturerData)
            {
                if (m.Data.Length >= 15)
                {
                    try
                    {
                        byte[] bytes = new byte[m.Data.Length];
                        using (var reader = DataReader.FromBuffer(m.Data))
                        {
                            reader.ReadBytes(bytes);
                        }
                        if (bytes[0] == 0xA1 && bytes[1] == 0x03)
                        {
                            e7Bytes = bytes;
                            foundE7Frame = true;
                            break;
                        }
                    }
                    catch
                    {
                        // Suppress
                    }
                }
            }

            if (!foundE7Frame)
            {
                foreach (var section in args.Advertisement.DataSections)
                {
                    if (section.Data.Length >= 17)
                    {
                        try
                        {
                            byte[] bytes = new byte[section.Data.Length];
                            using (var reader = DataReader.FromBuffer(section.Data))
                            {
                                reader.ReadBytes(bytes);
                            }
                            if (bytes[0] == 0xE1 && bytes[1] == 0xFF && bytes[2] == 0xA1 && bytes[3] == 0x03)
                            {
                                // Strip the 2-byte UUID prefix
                                e7Bytes = new byte[bytes.Length - 2];
                                Array.Copy(bytes, 2, e7Bytes, 0, e7Bytes.Length);
                                foundE7Frame = true;
                                break;
                            }
                        }
                        catch
                        {
                            // Suppress
                        }
                    }
                }
            }

            if (foundE7Frame && e7Bytes != null)
            {
                try
                {
                    short rawX = (short)((e7Bytes[3] << 8) | e7Bytes[4]);
                    short rawY = (short)((e7Bytes[5] << 8) | e7Bytes[6]);
                    short rawZ = (short)((e7Bytes[7] << 8) | e7Bytes[8]);

                    double xAxis = rawX / 256.0;
                    double yAxis = rawY / 256.0;
                    double zAxis = rawZ / 256.0;

                    string payloadMac = string.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
                        e7Bytes[14],
                        e7Bytes[13],
                        e7Bytes[12],
                        e7Bytes[11],
                        e7Bytes[10],
                        e7Bytes[9]);

                    double magnitude = Math.Sqrt(xAxis * xAxis + yAxis * yAxis + zAxis * zAxis);
                    double threshold = _configuration.GetValue<double?>("ScannerSettings:MotionThreshold") ?? 0.15;

                    lock (_stateLock)
                    {
                        if (_detectedBeacons.TryGetValue(payloadMac, out var state))
                        {
                            double diff = Math.Abs(magnitude - state.LastMagnitude);
                            state.IsMoving = diff > threshold;
                            state.LastMagnitude = magnitude;
                            state.XAxis = xAxis;
                            state.YAxis = yAxis;
                            state.ZAxis = zAxis;
                            state.HasMotionData = true;
                            state.LastSeen = DateTimeOffset.Now;
                        }
                        else
                        {
                            _detectedBeacons[payloadMac] = new BeaconState
                            {
                                MacAddress = payloadMac,
                                DeviceName = $"Minew E7 ({payloadMac})",
                                LatestRssi = args.RawSignalStrengthInDBm,
                                LastSeen = DateTimeOffset.Now,
                                Major = 0,
                                Minor = 0,
                                XAxis = xAxis,
                                YAxis = yAxis,
                                ZAxis = zAxis,
                                IsMoving = false,
                                LastMagnitude = magnitude,
                                HasMotionData = true
                            };
                        }
                    }
                }
                catch
                {
                    // Suppress
                }
            }

            // Normal iBeacon processing
            foreach (var m in args.Advertisement.ManufacturerData)
            {
                if (m.CompanyId == 0x004C && m.Data.Length == 23)
                {
                    try
                    {
                        var reader = DataReader.FromBuffer(m.Data);
                        byte[] dataBytes = new byte[m.Data.Length];
                        reader.ReadBytes(dataBytes);

                        if (dataBytes[0] == 0x02 && dataBytes[1] == 0x15)
                        {
                            ushort major = (ushort)((dataBytes[18] << 8) | dataBytes[19]);
                            ushort minor = (ushort)((dataBytes[20] << 8) | dataBytes[21]);

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
                        // Suppress
                    }
                }
            }
        }

        private async Task SendTelemetryAsync(
            string macAddress, 
            string deviceName, 
            short rssi, 
            int major, 
            int minor, 
            double xAxis, 
            double yAxis, 
            double zAxis, 
            bool isMoving, 
            bool hasMotionData, 
            CancellationToken stoppingToken)
        {
            var receiveTime = DateTime.Now;
            var scannerId = Environment.MachineName;

            if (!hasMotionData)
            {
                if (!_loggedNoMotion)
                {
                    _logger.LogInformation("Motion data not available in current E7 advertising frame.");
                    _loggedNoMotion = true;
                }
            }

            var telemetry = new BeaconTelemetryDto
            {
                MacAddress = macAddress,
                DeviceName = deviceName,
                Rssi = rssi,
                BatteryLevel = 100,
                XAxis = xAxis,
                YAxis = yAxis,
                ZAxis = zAxis,
                IsMoving = isMoving,
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
