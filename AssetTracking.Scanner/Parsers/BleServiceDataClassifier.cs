using System;

namespace AssetTracking.Scanner.Parsers
{
    public enum BleServiceDataKind
    {
        Unknown,
        EddystoneUid,
        EddystoneUrl,
        EddystoneTlm,
        MinewInfo,
        MinewAccSensor
    }

    public sealed class BleServiceDataClassification
    {
        public BleServiceDataKind Kind { get; init; }
        public ushort? ServiceUuid { get; init; }
        public byte? FrameType { get; init; }
        public byte[] Payload { get; init; } = Array.Empty<byte>();
    }

    public static class BleServiceDataClassifier
    {
        public static BleServiceDataClassification Classify(ReadOnlySpan<byte> bytes)
        {
            ushort? serviceUuid = null;
            byte? frameType = null;
            BleServiceDataKind kind = BleServiceDataKind.Unknown;

            if (bytes.Length >= 2)
            {
                // Check if Service UUID is Eddystone (0xFEAA, transmitted as AA FE in little-endian)
                if (bytes[0] == 0xAA && bytes[1] == 0xFE)
                {
                    serviceUuid = 0xFEAA;
                    if (bytes.Length >= 3)
                    {
                        frameType = bytes[2];
                        if (frameType == 0x00)
                        {
                            kind = BleServiceDataKind.EddystoneUid;
                        }
                        else if (frameType == 0x10)
                        {
                            kind = BleServiceDataKind.EddystoneUrl;
                        }
                        else if (frameType == 0x20)
                        {
                            kind = BleServiceDataKind.EddystoneTlm;
                        }
                    }
                }
                // Check if Service UUID is Minew (0xFFE1, transmitted as E1 FF in little-endian)
                else if (bytes[0] == 0xE1 && bytes[1] == 0xFF)
                {
                    serviceUuid = 0xFFE1;
                    if (bytes.Length >= 3)
                    {
                        frameType = bytes[2];
                        // If it starts with E1 FF A1 03, it matches our Minew ACC Layout A
                        if (bytes.Length >= 4 && bytes[2] == 0xA1 && bytes[3] == 0x03)
                        {
                            kind = BleServiceDataKind.MinewAccSensor;
                        }
                        // If it starts with E1 FF 40, it is the Minew Info frame type (0x40)
                        else if (frameType == 0x40)
                        {
                            kind = BleServiceDataKind.MinewInfo;
                        }
                    }
                }
                // Layout B: starts with A1 03
                else if (bytes[0] == 0xA1 && bytes.Length >= 15)
                {
                    if (bytes[1] == 0x03)
                    {
                        frameType = 0xA1;
                        kind = BleServiceDataKind.MinewAccSensor;
                    }
                }
                // Layout C: starts with 03 (version) and is 14 bytes long
                else if (bytes[0] == 0x03 && bytes.Length == 14)
                {
                    frameType = 0x03;
                    kind = BleServiceDataKind.MinewAccSensor;
                }
            }

            return new BleServiceDataClassification
            {
                Kind = kind,
                ServiceUuid = serviceUuid,
                FrameType = frameType,
                Payload = bytes.ToArray()
            };
        }
    }
}
