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
using AssetTracking.Scanner.Parsers;
using System.Collections.Concurrent;

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
            public int BatteryLevel { get; set; } = 100;
            public double PreviousXAxis { get; set; }
            public double PreviousYAxis { get; set; }
            public double PreviousZAxis { get; set; }
            public DateTimeOffset? LastMotionDetectedAt { get; set; }
        }

        private readonly System.Collections.Generic.Dictionary<string, BeaconState> _detectedBeacons = new(System.StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _lastAccUpdateLoggedTimes = new();
        private readonly object _stateLock = new();
        private readonly IConfiguration _configuration;


        private int TelemetryIntervalSeconds => _configuration.GetValue<int?>("ScannerSettings:TelemetryIntervalSeconds") ?? 2;
        private int OfflineTimeoutSeconds => _configuration.GetValue<int?>("ScannerSettings:OfflineTimeoutSeconds") ?? 30;
        private int MinimumRssi => _configuration.GetValue<int?>("ScannerSettings:MinimumRssi") ?? -100;
        private string ApiBaseUrl => _configuration.GetValue<string>("ApiSettings:BaseUrl") ?? "http://localhost:5176";
        private bool EnableRawAccFrameLogging => _configuration.GetValue<bool?>("ScannerSettings:EnableRawAccFrameLogging") ?? false;
        private double MotionDeltaThresholdG => _configuration.GetValue<double?>("ScannerSettings:MotionDeltaThresholdG") ?? 0.08;
        private int MotionHoldSeconds => _configuration.GetValue<int?>("ScannerSettings:MotionHoldSeconds") ?? 3;

        public BleScannerService(ILogger<BleScannerService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _httpClient = new HttpClient();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("BleScannerService: Starting Bluetooth LE advertisement watcher...");
            _logger.LogInformation("BleScannerService configurations:\n" +
                                   "  API Base URL: {ApiBaseUrl}\n" +
                                   "  Telemetry interval: {TelemetryInterval}s\n" +
                                   "  Offline timeout: {OfflineTimeout}s\n" +
                                   "  Minimum RSSI: {MinimumRssi} dBm\n" +
                                   "  Raw ACC logging: {RawAccLogging}\n" +
                                   "  Motion threshold: {MotionThreshold} G\n" +
                                   "  Motion hold seconds: {MotionHoldSeconds}s",
                                   ApiBaseUrl,
                                   TelemetryIntervalSeconds,
                                   OfflineTimeoutSeconds,
                                   MinimumRssi,
                                   EnableRawAccFrameLogging ? "enabled" : "disabled",
                                   MotionDeltaThresholdG,
                                   MotionHoldSeconds);

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

            // Periodic telemetry reporting loop (every telemetry interval)
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
                        var beaconState = kvp.Value;
                        if (beaconState.HasMotionData && beaconState.IsMoving)
                        {
                            if (beaconState.LastMotionDetectedAt == null || 
                                (now - beaconState.LastMotionDetectedAt.Value).TotalSeconds >= MotionHoldSeconds)
                            {
                                beaconState.IsMoving = false;
                            }
                        }

                        beaconsToReport.Add(new BeaconState
                        {
                            MacAddress = beaconState.MacAddress,
                            DeviceName = beaconState.DeviceName,
                            LatestRssi = beaconState.LatestRssi,
                            LastSeen = beaconState.LastSeen,
                            Major = beaconState.Major,
                            Minor = beaconState.Minor,
                            XAxis = beaconState.XAxis,
                            YAxis = beaconState.YAxis,
                            ZAxis = beaconState.ZAxis,
                            IsMoving = beaconState.IsMoving,
                            HasMotionData = beaconState.HasMotionData,
                            BatteryLevel = beaconState.BatteryLevel,
                            PreviousXAxis = beaconState.PreviousXAxis,
                            PreviousYAxis = beaconState.PreviousYAxis,
                            PreviousZAxis = beaconState.PreviousZAxis,
                            LastMotionDetectedAt = beaconState.LastMotionDetectedAt
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
                        beacon.BatteryLevel,
                        stoppingToken);
                }

                await Task.Delay(TelemetryIntervalSeconds * 1000, stoppingToken);
            }
        }

        private static string FormatBluetoothAddress(ulong address)
        {
            return string.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
                (byte)((address >> 40) & 0xFF),
                (byte)((address >> 32) & 0xFF),
                (byte)((address >> 24) & 0xFF),
                (byte)((address >> 16) & 0xFF),
                (byte)((address >> 8) & 0xFF),
                (byte)(address & 0xFF));
        }


        private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            string formattedMac = FormatBluetoothAddress(args.BluetoothAddress);

            // Process Service Data sections
            foreach (var section in args.Advertisement.DataSections)
            {
                if (section.DataType != 0x16)
                {
                    continue;
                }

                byte[] bytes = new byte[section.Data.Length];
                try
                {
                    using (var reader = DataReader.FromBuffer(section.Data))
                    {
                        reader.ReadBytes(bytes);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to read data section bytes.");
                    continue;
                }

                // Classification of Service Data
                var classification = BleServiceDataClassifier.Classify(bytes);

                // Do not send FEAA Eddystone packets to the ACC parser
                if (classification.ServiceUuid == 0xFEAA)
                {
                    continue;
                }

                // Only call MinewAccFrameParser.TryParse when: Service UUID = FFE1, Frame Type = A1, Version = 03
                if (classification.ServiceUuid == 0xFFE1 && 
                    classification.FrameType == 0xA1 && 
                    bytes.Length >= 4 && 
                    bytes[3] == 0x03)
                {
                    if (EnableRawAccFrameLogging)
                    {
                        var hexBytes = BitConverter.ToString(bytes);
                        _logger.LogDebug("BLE ServiceData raw bytes: {HexBytes}", hexBytes);
                        _logger.LogDebug("DataSection DataType: 0x{DataType:X2}, Length: {Length}, BluetoothAddress: {Address}", 
                            section.DataType, bytes.Length, formattedMac);
                    }

                    try
                    {
                        if (MinewAccFrameParser.TryParse(bytes, out var frame, _logger, formattedMac) && frame != null)
                        {
                            if (string.IsNullOrWhiteSpace(frame.MacAddress) || 
                                frame.MacAddress.Equals("00:00:00:00:00:00", StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.LogWarning("Identity conflict: ACC payload parsed an invalid MAC address '{Mac}'", frame.MacAddress);
                                continue;
                            }

                            lock (_stateLock)
                            {
                                if (_detectedBeacons.TryGetValue(frame.MacAddress, out var state))
                                {
                                    // Check identity conflict:
                                    // If state has Major/Minor set, verify they match the configured MAC identity.
                                    if (state.Major != 0 || state.Minor != 0)
                                    {
                                        bool conflict = false;
                                        if (string.Equals(frame.MacAddress, "C3:00:00:4F:89:CB", StringComparison.OrdinalIgnoreCase))
                                        {
                                            if (state.Major != 616 || state.Minor != 1) conflict = true;
                                        }
                                        else if (string.Equals(frame.MacAddress, "C3:00:00:4F:89:CD", StringComparison.OrdinalIgnoreCase))
                                        {
                                            if (state.Major != 616 || state.Minor != 2) conflict = true;
                                        }

                                        if (conflict)
                                        {
                                            _logger.LogWarning("Identity conflict: ACC MAC {Mac} matches state with incompatible iBeacon identity {Major}-{Minor}", frame.MacAddress, state.Major, state.Minor);
                                        }
                                    }

                                    // Make sure Major and Minor are not overwritten. Pre-populate if they are 0.
                                    if (state.Major == 0 && state.Minor == 0)
                                    {
                                        if (string.Equals(frame.MacAddress, "C3:00:00:4F:89:CB", StringComparison.OrdinalIgnoreCase))
                                        {
                                            state.Major = 616;
                                            state.Minor = 1;
                                        }
                                        else if (string.Equals(frame.MacAddress, "C3:00:00:4F:89:CD", StringComparison.OrdinalIgnoreCase))
                                        {
                                            state.Major = 616;
                                            state.Minor = 2;
                                        }
                                    }

                                    bool motionDetected = false;
                                    if (state.HasMotionData)
                                    {
                                        double deltaX = Math.Abs(frame.XAxis - state.PreviousXAxis);
                                        double deltaY = Math.Abs(frame.YAxis - state.PreviousYAxis);
                                        double deltaZ = Math.Abs(frame.ZAxis - state.PreviousZAxis);

                                        motionDetected = (deltaX >= MotionDeltaThresholdG) || 
                                                         (deltaY >= MotionDeltaThresholdG) || 
                                                         (deltaZ >= MotionDeltaThresholdG);
                                    }
                                    else
                                    {
                                        // For the very first ACC packet: Initialize Previous X/Y/Z, do not mark moving immediately
                                        state.PreviousXAxis = frame.XAxis;
                                        state.PreviousYAxis = frame.YAxis;
                                        state.PreviousZAxis = frame.ZAxis;
                                        state.HasMotionData = true;
                                    }

                                    state.XAxis = frame.XAxis;
                                    state.YAxis = frame.YAxis;
                                    state.ZAxis = frame.ZAxis;
                                    state.BatteryLevel = frame.BatteryLevel;
                                    state.LastSeen = DateTimeOffset.Now;
                                    state.LatestRssi = args.RawSignalStrengthInDBm;

                                    if (state.HasMotionData)
                                    {
                                        if (motionDetected)
                                        {
                                            state.IsMoving = true;
                                            state.LastMotionDetectedAt = DateTimeOffset.Now;
                                        }
                                        else
                                        {
                                            if (state.LastMotionDetectedAt == null || 
                                                (DateTimeOffset.Now - state.LastMotionDetectedAt.Value).TotalSeconds >= MotionHoldSeconds)
                                            {
                                                state.IsMoving = false;
                                            }
                                        }
                                    }

                                    // After processing: PreviousXAxis = current X, PreviousYAxis = current Y, PreviousZAxis = current Z
                                    state.PreviousXAxis = frame.XAxis;
                                    state.PreviousYAxis = frame.YAxis;
                                    state.PreviousZAxis = frame.ZAxis;

                                    // Optional stationary validation: Calculate magnitude & Log at Debug level
                                    double magnitude = Math.Sqrt(frame.XAxis * frame.XAxis + frame.YAxis * frame.YAxis + frame.ZAxis * frame.ZAxis);
                                    _logger.LogDebug("Minew ACC parsed | MAC: {Mac} | Battery: {Battery} | X: {X:F8} | Y: {Y:F8} | Z: {Z:F8} | Magnitude: {Magnitude:F8} | Moving: {Moving}",
                                        frame.MacAddress, frame.BatteryLevel, frame.XAxis, frame.YAxis, frame.ZAxis, magnitude, state.IsMoving.ToString().ToLower());

                                    // Info log at most once every 2 seconds per beacon
                                    var nowLog = DateTimeOffset.Now;
                                    bool shouldLogInfo = false;
                                    if (!_lastAccUpdateLoggedTimes.TryGetValue(frame.MacAddress, out var lastLogged) || 
                                        (nowLog - lastLogged).TotalSeconds >= 2.0)
                                    {
                                        _lastAccUpdateLoggedTimes[frame.MacAddress] = nowLog;
                                        shouldLogInfo = true;
                                    }

                                    if (shouldLogInfo)
                                    {
                                        _logger.LogInformation("Minew ACC updated | MAC: {Mac} | Battery: {Battery}% | X: {X:F3} | Y: {Y:F3} | Z: {Z:F3} | Moving: {Moving}",
                                            frame.MacAddress, frame.BatteryLevel, frame.XAxis, frame.YAxis, frame.ZAxis, state.IsMoving.ToString().ToLower());
                                    }
                                }
                                else
                                {
                                    _logger.LogDebug("ACC-first state created | MAC: {Mac}", frame.MacAddress);

                                    ushort mappedMajor = 0;
                                    ushort mappedMinor = 0;
                                    if (string.Equals(frame.MacAddress, "C3:00:00:4F:89:CB", StringComparison.OrdinalIgnoreCase))
                                    {
                                        mappedMajor = 616;
                                        mappedMinor = 1;
                                    }
                                    else if (string.Equals(frame.MacAddress, "C3:00:00:4F:89:CD", StringComparison.OrdinalIgnoreCase))
                                    {
                                        mappedMajor = 616;
                                        mappedMinor = 2;
                                    }

                                    var newState = new BeaconState
                                    {
                                        MacAddress = frame.MacAddress,
                                        DeviceName = $"Minew E7 ({frame.MacAddress})",
                                        LatestRssi = args.RawSignalStrengthInDBm,
                                        LastSeen = DateTimeOffset.Now,
                                        Major = mappedMajor,
                                        Minor = mappedMinor,
                                        XAxis = frame.XAxis,
                                        YAxis = frame.YAxis,
                                        ZAxis = frame.ZAxis,
                                        PreviousXAxis = frame.XAxis,
                                        PreviousYAxis = frame.YAxis,
                                        PreviousZAxis = frame.ZAxis,
                                        BatteryLevel = frame.BatteryLevel,
                                        HasMotionData = true,
                                        IsMoving = false,
                                        LastMotionDetectedAt = null
                                    };

                                    _detectedBeacons[frame.MacAddress] = newState;

                                    // Optional stationary validation: Calculate magnitude & Log at Debug level
                                    double magnitude = Math.Sqrt(frame.XAxis * frame.XAxis + frame.YAxis * frame.YAxis + frame.ZAxis * frame.ZAxis);
                                    _logger.LogDebug("Minew ACC parsed | MAC: {Mac} | Battery: {Battery} | X: {X:F8} | Y: {Y:F8} | Z: {Z:F8} | Magnitude: {Magnitude:F8} | Moving: {Moving}",
                                        frame.MacAddress, frame.BatteryLevel, frame.XAxis, frame.YAxis, frame.ZAxis, magnitude, newState.IsMoving.ToString().ToLower());

                                    // Log the immediate addition
                                    _logger.LogInformation("Minew ACC updated | MAC: {Mac} | Battery: {Battery}% | X: {X:F3} | Y: {Y:F3} | Z: {Z:F3} | Moving: {Moving}",
                                        frame.MacAddress, frame.BatteryLevel, frame.XAxis, frame.YAxis, frame.ZAxis, newState.IsMoving.ToString().ToLower());
                                    _lastAccUpdateLoggedTimes[frame.MacAddress] = DateTimeOffset.Now;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to parse Minew ACC frame.");
                    }
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

                            string realMac = formattedMac; // Reuse formatted MAC!

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

                            if (major == 616 && minor == 1 && !string.Equals(realMac, "C3:00:00:4F:89:CB", StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.LogWarning("Identity conflict: Received iBeacon major={Major}, minor={Minor} from MAC {RealMac}, but configured MAC is C3:00:00:4F:89:CB", major, minor, realMac);
                            }
                            else if (major == 616 && minor == 2 && !string.Equals(realMac, "C3:00:00:4F:89:CD", StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.LogWarning("Identity conflict: Received iBeacon major={Major}, minor={Minor} from MAC {RealMac}, but configured MAC is C3:00:00:4F:89:CD", major, minor, realMac);
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
                                    if ((state.Major != 0 && state.Major != major) || (state.Minor != 0 && state.Minor != minor))
                                    {
                                        _logger.LogWarning("Identity conflict: Merging iBeacon major={Major}, minor={Minor} into state for MAC {Mac} which already has major={ExistingMajor}, minor={ExistingMinor}", major, minor, macAddress, state.Major, state.Minor);
                                    }

                                    state.LatestRssi = args.RawSignalStrengthInDBm;
                                    state.LastSeen = DateTimeOffset.Now;
                                    state.Major = major;
                                    state.Minor = minor;
                                    state.DeviceName = deviceName; // Replace temporary DeviceName
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
            int batteryLevel,
            CancellationToken stoppingToken)
        {
            var receiveTime = DateTime.Now;
            var scannerId = Environment.MachineName;

            var telemetry = new BeaconTelemetryDto
            {
                MacAddress = macAddress,
                DeviceName = deviceName,
                Rssi = rssi,
                BatteryLevel = batteryLevel,
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
