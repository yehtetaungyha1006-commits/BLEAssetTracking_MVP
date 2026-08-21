using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;
using AssetTracking.Shared;
using AssetTracking.Scanner.Parsers;

namespace AssetTracking.Scanner
{
    public class BleScannerService : BackgroundService
    {
        private readonly ILogger<BleScannerService> _logger;
        private readonly HttpClient _httpClient;
        private BluetoothLEAdvertisementWatcher? _watcher;

        private const string TargetUuid = "53495445-4C54-5241-494E-454554455354";

        public class BeaconKey : IEquatable<BeaconKey>
        {
            public string Uuid { get; }
            public int Major { get; }
            public int Minor { get; }

            public BeaconKey(string uuid, int major, int minor)
            {
                Uuid = uuid ?? string.Empty;
                Major = major;
                Minor = minor;
            }

            public bool Equals(BeaconKey? other)
            {
                if (other is null) return false;
                return string.Equals(Uuid, other.Uuid, StringComparison.OrdinalIgnoreCase) &&
                       Major == other.Major &&
                       Minor == other.Minor;
            }

            public override bool Equals(object? obj) => Equals(obj as BeaconKey);

            public override int GetHashCode() => HashCode.Combine(Uuid.ToLowerInvariant(), Major, Minor);

            public override string ToString() => $"{Major}-{Minor}";
        }

        private class BleAddressAssociation
        {
            public int DeviceId { get; }
            public BeaconKey Key { get; }
            public DateTimeOffset LastIdentityConfirmedAt { get; set; }

            public BleAddressAssociation(int deviceId, BeaconKey key, DateTimeOffset confirmedAt)
            {
                DeviceId = deviceId;
                Key = key;
                LastIdentityConfirmedAt = confirmedAt;
            }
        }

        private class BeaconRealtimeState
        {
            public int DeviceId { get; }
            public BeaconKey Key { get; }
            public string MacAddress { get; set; } = string.Empty;
            public string DeviceName { get; set; } = string.Empty;

            // RSSI State
            public short LatestRssi { get; set; }
            public DateTimeOffset RssiObservedAt { get; set; }
            public DateTimeOffset LastTelemetrySentAt { get; set; }
            public short LastRssiSent { get; set; }
            public DateTimeOffset LastHeartbeatAt { get; set; }

            // Motion & Accelerometer State
            public int BatteryLevel { get; set; } = 100;
            public double XAxis { get; set; }
            public double YAxis { get; set; }
            public double ZAxis { get; set; }
            public double PreviousXAxis { get; set; }
            public double PreviousYAxis { get; set; }
            public double PreviousZAxis { get; set; }
            public bool IsMoving { get; set; }
            public bool HasMotionData { get; set; }
            public DateTimeOffset? LastMotionDetectedAt { get; set; }
            public DateTimeOffset? LastAccObservedAt { get; set; }

            // Observation Gap & Per-Beacon Latency
            public DateTimeOffset? LastFreshRssiObservedAt { get; set; }
            public List<double> ObservationGapsMs { get; } = new();
            public List<double> FreshRssiAges { get; } = new();

            // Per-Beacon Counter Metrics
            public long IdentityPacketsCount { get; set; }
            public long BleAdvertisementsCount { get; set; }
            public long FreshRssiObservationsCount { get; set; }
            public long FreshTelemetrySent { get; set; }
            public long HeartbeatTelemetrySent { get; set; }
            public long HeartbeatWithCachedRssi { get; set; }
            public long MovementTriggeredTelemetries { get; set; }
            public long BatteryTriggeredTelemetries { get; set; }
            public long SkippedDuplicateRssi { get; set; }
            public long SupersededRssiUpdates { get; set; }
            public long StaleRssiMeasurementsRejected { get; set; }
            public long WrongAssociationCount { get; set; }
            public long ActualStaleRssiSends { get; set; } = 0;

            // Per-Beacon Non-blocking Dispatcher Channel
            public Channel<bool> DispatchChannel { get; } = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            });

            public int IsSendingFlag = 0; // 0 = idle, 1 = sending worker active

            public BeaconRealtimeState(int deviceId, BeaconKey key, string macAddress)
            {
                DeviceId = deviceId;
                Key = key;
                MacAddress = macAddress;
                DeviceName = $"iBeacon ({key.Major}-{key.Minor})";
            }
        }

        private readonly ConcurrentDictionary<int, BeaconRealtimeState> _beaconsByDeviceId = new();
        private readonly ConcurrentDictionary<string, BleAddressAssociation> _addressToAssociation = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _keyToDeviceIdMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly IConfiguration _configuration;

        // Global statistics metrics
        private readonly object _statsLock = new();
        private long _totalTelemetrySent = 0;
        private long _freshTelemetrySent = 0;
        private long _heartbeatTelemetrySent = 0;
        private long _heartbeatWithCachedRssi = 0;
        private long _movementTelemetrySent = 0;
        private long _skippedDuplicateRssi = 0;
        private long _supersededRssiUpdates = 0;
        private long _staleRssiMeasurementsRejected = 0;
        private long _wrongAssociationCount = 0;
        private long _actualStaleRssiSends = 0;

        private double _minObsAgeMs = double.MaxValue;
        private double _maxObsAgeMs = 0;
        private double _sumObsAgeMs = 0;
        private readonly List<double> _freshRssiAges = new();

        private long _totalBleRxCount = 0;
        private long _iBeaconFrameCount = 0;
        private long _accFrameCount = 0;
        private long _unsupportedFrameCount = 0;

        private readonly IHostApplicationLifetime? _lifetime;

        private int MinimumRssi => _configuration.GetValue<int?>("ScannerSettings:MinimumRssi") ?? -100;
        private string ApiBaseUrl => _configuration.GetValue<string>("ApiSettings:BaseUrl") ?? "http://localhost:5176";
        private double MotionDeltaThresholdG => _configuration.GetValue<double?>("ScannerSettings:MotionDeltaThresholdG") ?? 0.05;
        private double MotionHoldSeconds => _configuration.GetValue<double?>("ScannerSettings:MotionHoldSeconds") ?? 1.8;
        private int MinSendIntervalMs => 250; // Per-beacon throttle interval
        private double MaxRssiAgeMs => 1200.0; // Fresh RSSI cutoff threshold
        private double AddressMappingExpiryMs => 5000.0; // 5.0 seconds short-lived identity mapping expiration
        private int TestDurationSeconds => _configuration.GetValue<int?>("ScannerSettings:TestDurationSeconds") ?? 0;

        public BleScannerService(ILogger<BleScannerService> logger, IConfiguration configuration, IHostApplicationLifetime? lifetime = null)
        {
            _logger = logger;
            _configuration = configuration;
            _lifetime = lifetime;
            _httpClient = new HttpClient();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("BleScannerService: Starting Event-Driven Bluetooth LE advertisement watcher...");
            _logger.LogInformation("BleScannerService configurations:\n" +
                                   "  API Base URL: {ApiBaseUrl}\n" +
                                   "  Minimum RSSI: {MinimumRssi} dBm\n" +
                                   "  Motion threshold: {MotionThreshold} G\n" +
                                   "  Motion hold seconds: {MotionHoldSeconds}s\n" +
                                   "  Per-beacon Min Send Interval: {MinSendInterval}ms\n" +
                                   "  Max RSSI Freshness Age: {MaxRssiAge}ms\n" +
                                   "  Address Mapping Expiry: {AddressExpiry}ms\n" +
                                   "  Test Duration Seconds: {TestDuration}s",
                                   ApiBaseUrl,
                                   MinimumRssi,
                                   MotionDeltaThresholdG,
                                   MotionHoldSeconds,
                                   MinSendIntervalMs,
                                   MaxRssiAgeMs,
                                   AddressMappingExpiryMs,
                                   TestDurationSeconds);

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
                _logger.LogError(ex, "BleScannerService: Error starting BluetoothLEAdvertisementWatcher.");
            }

            var startTime = DateTimeOffset.UtcNow;

            // Background ticker loop for explicit Heartbeats & Motion Hold Timeouts per DeviceId
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;

                if (TestDurationSeconds > 0 && (now - startTime).TotalSeconds >= TestDurationSeconds)
                {
                    _logger.LogInformation("BleScannerService: Test duration ({Duration}s) reached. Requesting application stop...", TestDurationSeconds);
                    _lifetime?.StopApplication();
                    break;
                }

                foreach (var kvp in _beaconsByDeviceId)
                {
                    var state = kvp.Value;

                    // Motion Hold Timeout Check
                    if (state.HasMotionData && state.IsMoving)
                    {
                        if (state.LastMotionDetectedAt == null || 
                            (now - state.LastMotionDetectedAt.Value).TotalSeconds >= MotionHoldSeconds)
                        {
                            state.IsMoving = false;
                            _logger.LogInformation("[ACC Movement] DeviceId: {DeviceId} | Beacon: {Beacon} | Hold timeout ({Hold:F1}s) reached | IsMoving: False",
                                state.DeviceId, state.Key, MotionHoldSeconds);
                            SignalBeaconDispatch(state, isMovement: true, isHeartbeat: false);
                        }
                    }

                    // 5-Second Explicit Heartbeat Check
                    if ((now - state.LastTelemetrySentAt).TotalSeconds >= 5.0)
                    {
                        SignalBeaconDispatch(state, isMovement: false, isHeartbeat: true);
                    }
                }

                await Task.Delay(1000, stoppingToken);
            }
        }

        private static string FormatBluetoothAddress(ulong address)
        {
            byte[] bytes = BitConverter.GetBytes(address);
            return string.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
                bytes[5], bytes[4], bytes[3], bytes[2], bytes[1], bytes[0]);
        }

        private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            string realMac = FormatBluetoothAddress(args.BluetoothAddress);

            bool isTargetMac = string.Equals(realMac, "C3:00:00:4F:89:CB", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(realMac, "C3:00:00:4F:89:CD", StringComparison.OrdinalIgnoreCase);

            bool isTargetUuid = false;
            foreach (var guid in args.Advertisement.ServiceUuids)
            {
                if (string.Equals(guid.ToString(), TargetUuid, StringComparison.OrdinalIgnoreCase))
                {
                    isTargetUuid = true;
                    break;
                }
            }

            if (!isTargetMac && !isTargetUuid)
            {
                return;
            }

            short rssi = args.RawSignalStrengthInDBm;
            var nowObserved = DateTimeOffset.UtcNow;

            if (rssi < MinimumRssi)
            {
                return;
            }

            bool parsedAcc = false;
            bool parsedIBeacon = false;
            string detectedFrameType = "UNSUPPORTED";

            // Stage B — Process Service Data for Minew ACC Frames (0x16 Service Data)
            foreach (var section in args.Advertisement.DataSections)
            {
                if (section.DataType != 0x16) continue;

                byte[] bytes = new byte[section.Data.Length];
                try
                {
                    using (var reader = DataReader.FromBuffer(section.Data))
                    {
                        reader.ReadBytes(bytes);
                    }
                }
                catch
                {
                    continue;
                }

                if (MinewAccFrameParser.TryParse(bytes, out var frame, _logger, realMac) && frame != null)
                {
                    parsedAcc = true;
                    detectedFrameType = "ACC";
                    string macAddress = string.IsNullOrEmpty(frame.MacAddress) ? realMac : frame.MacAddress;

                    if (_addressToAssociation.TryGetValue(macAddress, out var assoc))
                    {
                        double assocAgeMs = (nowObserved - assoc.LastIdentityConfirmedAt).TotalMilliseconds;
                        bool isAssociationValid = assocAgeMs <= AddressMappingExpiryMs;

                        _logger.LogInformation("[ASSOCIATION] BLE Address: {Mac} | Resolved DeviceId: {DeviceId} | AssociationAgeMs: {Age:F0}ms | FrameType: ACC | Accepted: {Accepted}",
                            macAddress, isAssociationValid ? assoc.DeviceId.ToString() : "EXPIRED", assocAgeMs, isAssociationValid);

                        if (isAssociationValid && _beaconsByDeviceId.TryGetValue(assoc.DeviceId, out var state))
                        {
                            lock (state)
                            {
                                state.BleAdvertisementsCount++;
                                double deltaX = Math.Abs(frame.XAxis - state.PreviousXAxis);
                                double deltaY = Math.Abs(frame.YAxis - state.PreviousYAxis);
                                double deltaZ = Math.Abs(frame.ZAxis - state.PreviousZAxis);
                                double movementDelta = Math.Sqrt(deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ);

                                bool previousIsMoving = state.IsMoving;
                                bool isMovingChanged = false;

                                if (state.HasMotionData)
                                {
                                    if (movementDelta >= MotionDeltaThresholdG)
                                    {
                                        state.IsMoving = true;
                                        state.LastMotionDetectedAt = nowObserved;
                                        if (!previousIsMoving) isMovingChanged = true;

                                        _logger.LogInformation("[ACC Movement] DeviceId: {DeviceId} | Beacon: {Beacon} | MAC: {Mac} | Prev: {PrevX:F3},{PrevY:F3},{PrevZ:F3} | Current: {X:F3},{Y:F3},{Z:F3} | Delta: {Delta:F4} | Threshold: {Threshold:F4} | MotionDetected: True | IsMoving: True",
                                            state.DeviceId, state.Key, macAddress, state.PreviousXAxis, state.PreviousYAxis, state.PreviousZAxis, frame.XAxis, frame.YAxis, frame.ZAxis, movementDelta, MotionDeltaThresholdG);
                                    }
                                    else
                                    {
                                        double quietDurationSec = state.LastMotionDetectedAt.HasValue ? (nowObserved - state.LastMotionDetectedAt.Value).TotalSeconds : 0.0;
                                        if (state.LastMotionDetectedAt == null || quietDurationSec >= MotionHoldSeconds)
                                        {
                                            state.IsMoving = false;
                                            if (previousIsMoving) isMovingChanged = true;
                                        }

                                        _logger.LogInformation("[ACC Movement] DeviceId: {DeviceId} | Beacon: {Beacon} | MAC: {Mac} | Delta: {Delta:F4} | Threshold: {Threshold:F4} | QuietDuration: {QuietDuration:F1}s | IsMoving: {IsMoving}",
                                            state.DeviceId, state.Key, macAddress, movementDelta, MotionDeltaThresholdG, quietDurationSec, state.IsMoving);
                                    }
                                }
                                else
                                {
                                    state.HasMotionData = true;
                                    state.IsMoving = false;
                                }

                                state.PreviousXAxis = frame.XAxis;
                                state.PreviousYAxis = frame.YAxis;
                                state.PreviousZAxis = frame.ZAxis;

                                state.XAxis = frame.XAxis;
                                state.YAxis = frame.YAxis;
                                state.ZAxis = frame.ZAxis;
                                state.BatteryLevel = frame.BatteryLevel;
                                state.LastAccObservedAt = nowObserved;

                                _logger.LogInformation("[BLE][ACC] DeviceId: {DeviceId} | Beacon: {Beacon} | MAC: {Mac} | Batt: {Battery}% | X: {X:F3} | Y: {Y:F3} | Z: {Z:F3} | ObservedAt: {ObservedAt:HH:mm:ss.fff}",
                                    state.DeviceId, state.Key, macAddress, frame.BatteryLevel, frame.XAxis, frame.YAxis, frame.ZAxis, state.LastAccObservedAt.Value.ToLocalTime());

                                if (isMovingChanged)
                                {
                                    SignalBeaconDispatch(state, isMovement: true, isHeartbeat: false);
                                }
                            }
                        }
                        else
                        {
                            lock (_statsLock) { _wrongAssociationCount++; }
                        }
                    }
                    else
                    {
                        _logger.LogWarning("[ASSOCIATION] BLE Address: {Mac} | Resolved DeviceId: NONE | FrameType: ACC | Accepted: False", macAddress);
                        lock (_statsLock) { _wrongAssociationCount++; }
                    }
                    break;
                }
            }

            // Stage A — Process Manufacturer Data for Apple iBeacon Packets (0x004C) - Identity Packet
            if (!parsedAcc)
            {
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

                                parsedIBeacon = true;
                                detectedFrameType = "IBEACON";

                                var key = new BeaconKey(TargetUuid, major, minor);
                                int deviceId = minor; // Minor corresponds to registered DeviceId

                                var state = _beaconsByDeviceId.GetOrAdd(deviceId, id => new BeaconRealtimeState(id, key, realMac));
                                _addressToAssociation[realMac] = new BleAddressAssociation(deviceId, key, nowObserved);

                                _logger.LogInformation("[IDENTITY] DeviceId: {DeviceId} | UUID: {Uuid} | Major: {Major} | Minor: {Minor} | BLE Address: {Mac} | RSSI: {Rssi} dBm | IdentitySource: IBEACON",
                                    deviceId, key.Uuid, major, minor, realMac, rssi);

                                bool triggerDispatch = false;

                                lock (state)
                                {
                                    state.BleAdvertisementsCount++;
                                    state.FreshRssiObservationsCount++;
                                    state.IdentityPacketsCount++;

                                    if (state.LastFreshRssiObservedAt.HasValue)
                                    {
                                        double gapMs = (nowObserved - state.LastFreshRssiObservedAt.Value).TotalMilliseconds;
                                        state.ObservationGapsMs.Add(gapMs);
                                    }
                                    state.LastFreshRssiObservedAt = nowObserved;

                                    state.MacAddress = realMac;
                                    state.LatestRssi = rssi;
                                    state.RssiObservedAt = nowObserved;

                                    _logger.LogInformation("[BLE][RSSI] DeviceId: {DeviceId} | Beacon: {Beacon} | RSSI: {Rssi} dBm | ObservedAt: {ObservedAt:HH:mm:ss.fff}",
                                        state.DeviceId, state.Key, rssi, nowObserved.ToLocalTime());

                                    int rssiDiff = Math.Abs(rssi - state.LastRssiSent);
                                    double timeSinceLastSendMs = (nowObserved - state.LastTelemetrySentAt).TotalMilliseconds;

                                    if (rssiDiff >= 2 || timeSinceLastSendMs >= MinSendIntervalMs)
                                    {
                                        triggerDispatch = true;
                                    }
                                }

                                if (triggerDispatch)
                                {
                                    SignalBeaconDispatch(state, isMovement: false, isHeartbeat: false);
                                }

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

            // Record frame classification metrics
            lock (_statsLock)
            {
                _totalBleRxCount++;
                if (parsedAcc) _accFrameCount++;
                else if (parsedIBeacon) _iBeaconFrameCount++;
                else _unsupportedFrameCount++;
            }

            _logger.LogInformation("[BLE][RX] MAC: {Mac} | RSSI: {Rssi} dBm | FrameType: {FrameType} | ObservedAt: {ObservedAt:HH:mm:ss.fff}",
                realMac, rssi, detectedFrameType, nowObserved.ToLocalTime());
        }

        private void SignalBeaconDispatch(BeaconRealtimeState state, bool isMovement, bool isHeartbeat)
        {
            bool written = state.DispatchChannel.Writer.TryWrite(true);
            if (!written)
            {
                lock (state) { state.SupersededRssiUpdates++; }
                lock (_statsLock) { _supersededRssiUpdates++; }
                _logger.LogInformation("[QUEUE] DeviceId: {DeviceId} | Beacon: {Beacon} | Latest RSSI replaced previous pending RSSI", state.DeviceId, state.Key);
            }

            if (Interlocked.CompareExchange(ref state.IsSendingFlag, 1, 0) == 0)
            {
                _ = Task.Run(async () => await ProcessBeaconQueueAsync(state));
            }
        }

        private async Task ProcessBeaconQueueAsync(BeaconRealtimeState state)
        {
            try
            {
                while (state.DispatchChannel.Reader.TryRead(out _))
                {
                    short rssiToSend;
                    DateTimeOffset obsAt;
                    double xAxis, yAxis, zAxis;
                    bool isMoving;
                    int battery;
                    int major, minor;
                    int deviceId;
                    string mac, deviceName;

                    lock (state)
                    {
                        rssiToSend = state.LatestRssi;
                        obsAt = state.RssiObservedAt;
                        xAxis = state.XAxis;
                        yAxis = state.YAxis;
                        zAxis = state.ZAxis;
                        isMoving = state.IsMoving;
                        battery = state.BatteryLevel;
                        major = state.Key.Major;
                        minor = state.Key.Minor;
                        deviceId = state.DeviceId;
                        mac = state.MacAddress;
                        deviceName = state.DeviceName;
                    }

                    var sentAt = DateTimeOffset.UtcNow;
                    var rssiAgeMs = (sentAt - obsAt).TotalMilliseconds;
                    bool isHeartbeat = (sentAt - state.LastTelemetrySentAt).TotalSeconds >= 5.0;

                    // Normal telemetry requires fresh RSSI (age <= MaxRssiAgeMs, e.g. 1200ms)
                    if (!isHeartbeat && rssiAgeMs > MaxRssiAgeMs)
                    {
                        lock (state) { state.StaleRssiMeasurementsRejected++; }
                        lock (_statsLock) { _staleRssiMeasurementsRejected++; }
                        _logger.LogWarning("[REJECT] DeviceId: {DeviceId} | Beacon: {Beacon} | RSSI age {Age:F0}ms > {MaxAge}ms | Rejected stale RSSI measurement", state.DeviceId, state.Key, rssiAgeMs, MaxRssiAgeMs);
                        continue;
                    }

                    // Avoid duplicate RSSI if unchanged and not heartbeat
                    if (!isHeartbeat && rssiToSend == state.LastRssiSent && (sentAt - state.LastTelemetrySentAt).TotalMilliseconds < 2000)
                    {
                        lock (state) { state.SkippedDuplicateRssi++; }
                        lock (_statsLock) { _skippedDuplicateRssi++; }
                        continue;
                    }

                    state.LastRssiSent = rssiToSend;
                    state.LastTelemetrySentAt = sentAt;
                    if (isHeartbeat) state.LastHeartbeatAt = sentAt;

                    bool isFreshRssi = !isHeartbeat && rssiAgeMs <= MaxRssiAgeMs;

                    var telemetry = new BeaconTelemetryDto
                    {
                        MacAddress = mac,
                        DeviceName = deviceName,
                        Rssi = rssiToSend,
                        BatteryLevel = battery,
                        XAxis = xAxis,
                        YAxis = yAxis,
                        ZAxis = zAxis,
                        IsMoving = isMoving,
                        ReceiveTime = DateTime.Now,
                        ObservedAt = obsAt.UtcDateTime,
                        SentAt = sentAt.UtcDateTime,
                        IsFreshObservation = isFreshRssi,
                        IsHeartbeat = isHeartbeat,
                        ObservationAgeMs = Math.Max(0, rssiAgeMs),
                        ScannerId = Environment.MachineName,
                        Major = major,
                        Minor = minor
                    };

                    lock (state)
                    {
                        if (isHeartbeat)
                        {
                            state.HeartbeatTelemetrySent++;
                            if (rssiAgeMs > MaxRssiAgeMs)
                            {
                                state.HeartbeatWithCachedRssi++;
                            }
                        }
                        else
                        {
                            if (isMoving)
                            {
                                state.MovementTriggeredTelemetries++;
                            }
                            state.FreshTelemetrySent++;
                            state.FreshRssiAges.Add(rssiAgeMs);
                        }
                    }

                    lock (_statsLock)
                    {
                        _totalTelemetrySent++;
                        if (isHeartbeat)
                        {
                            _heartbeatTelemetrySent++;
                            if (rssiAgeMs > MaxRssiAgeMs) _heartbeatWithCachedRssi++;
                        }
                        else
                        {
                            if (isMoving) _movementTelemetrySent++;
                            _freshTelemetrySent++;
                            _sumObsAgeMs += rssiAgeMs;
                            _freshRssiAges.Add(rssiAgeMs);
                            if (rssiAgeMs < _minObsAgeMs) _minObsAgeMs = rssiAgeMs;
                            if (rssiAgeMs > _maxObsAgeMs) _maxObsAgeMs = rssiAgeMs;
                        }
                    }

                    _logger.LogInformation("[TELEMETRY] DeviceId: {DeviceId} | Major: {Major} | Minor: {Minor} | RSSI: {Rssi} dBm | RSSI Age: {Age:F0}ms | Fresh: {Fresh} | Heartbeat: {Hb}",
                        deviceId, major, minor, rssiToSend, rssiAgeMs, isFreshRssi, isHeartbeat);

                    try
                    {
                        var baseUrl = ApiBaseUrl.TrimEnd('/');
                        var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/api/beacon/telemetry", telemetry);
                        if (response.IsSuccessStatusCode)
                        {
                            _logger.LogInformation("[T2] DeviceId: {DeviceId} | Beacon: {Beacon} | RSSI: {Rssi} dBm (Age: {Age:F0}ms, Fresh: {Fresh}) | IsMoving: {Moving} | Heartbeat: {Hb}",
                                state.DeviceId, state.Key, rssiToSend, rssiAgeMs, isFreshRssi, isMoving, isHeartbeat);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error posting telemetry for device {DeviceId} ({Beacon})", state.DeviceId, state.Key);
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref state.IsSendingFlag, 0);

                if (state.DispatchChannel.Reader.Count > 0 && Interlocked.CompareExchange(ref state.IsSendingFlag, 1, 0) == 0)
                {
                    _ = Task.Run(async () => await ProcessBeaconQueueAsync(state));
                }
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

            PrintMetricsReport();

            await base.StopAsync(cancellationToken);
        }

        private void PrintMetricsReport()
        {
            lock (_statsLock)
            {
                double globalAvgAge = _freshTelemetrySent > 0 ? _sumObsAgeMs / _freshTelemetrySent : 0;
                double globalP95Age = CalculatePercentile(_freshRssiAges, 0.95);

                _logger.LogInformation("=== BLE SCANNER GLOBAL METRICS REPORT ===");
                _logger.LogInformation("Total BLE Advertisements Received: {RxCount}", _totalBleRxCount);
                _logger.LogInformation("  iBeacon Frames Parsed: {IBeaconCount}", _iBeaconFrameCount);
                _logger.LogInformation("  ACC Frames Parsed: {AccCount}", _accFrameCount);
                _logger.LogInformation("  Unsupported Payload Frames: {UnsupportedCount}", _unsupportedFrameCount);
                _logger.LogInformation("Total Telemetries Sent: {Total}", _totalTelemetrySent);
                _logger.LogInformation("  Fresh RSSI Telemetries Sent: {Fresh}", _freshTelemetrySent);
                _logger.LogInformation("  Heartbeat Telemetries Sent: {Heartbeat} (Cached RSSI: {Cached})", _heartbeatTelemetrySent, _heartbeatWithCachedRssi);
                _logger.LogInformation("  Movement Triggered Telemetries Sent: {Movement}", _movementTelemetrySent);
                _logger.LogInformation("  Skipped Duplicate RSSI: {Dup}", _skippedDuplicateRssi);
                _logger.LogInformation("  Superseded RSSI Updates: {Superseded}", _supersededRssiUpdates);
                _logger.LogInformation("  Stale RSSI Measurements Rejected: {Rejected}", _staleRssiMeasurementsRejected);
                _logger.LogInformation("  Wrong Association Count: {WrongAssoc}", _wrongAssociationCount);
                _logger.LogInformation("  Actual Stale RSSI Sends: {Stale}", _actualStaleRssiSends);
                _logger.LogInformation("RSSI Observation Age:");
                _logger.LogInformation("  Min RSSI Age: {Min:F1} ms", _minObsAgeMs == double.MaxValue ? 0 : _minObsAgeMs);
                _logger.LogInformation("  Avg RSSI Age: {Avg:F1} ms", globalAvgAge);
                _logger.LogInformation("  P95 RSSI Age: {P95:F1} ms", globalP95Age);
                _logger.LogInformation("  Max RSSI Age: {Max:F1} ms", _maxObsAgeMs);
                _logger.LogInformation("===============================================");
            }

            foreach (var kvp in _beaconsByDeviceId.OrderBy(k => k.Key))
            {
                var state = kvp.Value;
                lock (state)
                {
                    double minAge = state.FreshRssiAges.Count > 0 ? state.FreshRssiAges.Min() : 0;
                    double avgAge = state.FreshRssiAges.Count > 0 ? state.FreshRssiAges.Average() : 0;
                    double p95Age = CalculatePercentile(state.FreshRssiAges, 0.95);

                    double avgGap = state.ObservationGapsMs.Count > 0 ? state.ObservationGapsMs.Average() : 0;
                    double p95Gap = CalculatePercentile(state.ObservationGapsMs, 0.95);
                    double maxGap = state.ObservationGapsMs.Count > 0 ? state.ObservationGapsMs.Max() : 0;

                    _logger.LogInformation("");
                    _logger.LogInformation("=== BEACON {DeviceId} ===", state.DeviceId);
                    _logger.LogInformation("DeviceId: {DeviceId}", state.DeviceId);
                    _logger.LogInformation("Major: {Major}", state.Key.Major);
                    _logger.LogInformation("Minor: {Minor}", state.Key.Minor);
                    _logger.LogInformation("Identity packets: {IdentityPackets}", state.IdentityPacketsCount);
                    _logger.LogInformation("RSSI observations: {Obs}", state.FreshRssiObservationsCount);
                    _logger.LogInformation("Fresh telemetry: {Fresh}", state.FreshTelemetrySent);
                    _logger.LogInformation("Heartbeat telemetry: {Heartbeat}", state.HeartbeatTelemetrySent);
                    _logger.LogInformation("Skipped duplicates: {Duplicates}", state.SkippedDuplicateRssi);
                    _logger.LogInformation("Stale measurements rejected: {StaleRejected}", state.StaleRssiMeasurementsRejected);
                    _logger.LogInformation("Avg observation gap: {AvgGap:F1} ms", avgGap);
                    _logger.LogInformation("P95 observation gap: {P95Gap:F1} ms", p95Gap);
                    _logger.LogInformation("Max observation gap: {MaxGap:F1} ms", maxGap);
                    _logger.LogInformation("Avg send age: {AvgAge:F1} ms", avgAge);
                    _logger.LogInformation("Wrong association count: {WrongAssoc}", state.WrongAssociationCount);
                    _logger.LogInformation("========================");
                }
            }
        }

        private static double CalculatePercentile(List<double> values, double percentile)
        {
            if (values == null || values.Count == 0) return 0;
            var sorted = values.OrderBy(v => v).ToList();
            int index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
            return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
        }
    }
}
