using System;
using Microsoft.Extensions.Logging;

namespace AssetTracking.Scanner.Parsers
{
    public sealed class MinewAccFrame
    {
        public string MacAddress { get; init; } = string.Empty;
        public int BatteryLevel { get; init; }
        public double XAxis { get; init; }
        public double YAxis { get; init; }
        public double ZAxis { get; init; }
    }

    /// <summary>
    /// Parser for Minew E7 Accelerometer frames.
    /// Note: The Minew ACC packet layout must be verified against actual Windows advertisement data.
    /// DataType 0x16 only indicates Service Data, not ACC data.
    /// FEAA packets belong to Eddystone.
    /// </summary>
    public static class MinewAccFrameParser
    {
        public static bool TryParse(
            ReadOnlySpan<byte> bytes,
            out MinewAccFrame? frame)
        {
            return TryParse(bytes, out frame, null, string.Empty);
        }

        public static bool TryParse(
            ReadOnlySpan<byte> bytes,
            out MinewAccFrame? frame,
            ILogger? logger,
            string macAddress)
        {
            frame = null;

            if (bytes.Length < 17)
            {
                logger?.LogDebug("Ignored packet shorter than 17 bytes from {MacAddress}. Length: {Length}", macAddress, bytes.Length);
                return false;
            }

            if (bytes[0] != 0xE1 || bytes[1] != 0xFF)
            {
                logger?.LogDebug("Ignored packet with invalid UUID from {MacAddress}.", macAddress);
                return false;
            }

            if (bytes[2] != 0xA1)
            {
                logger?.LogDebug("Ignored packet with invalid Frame Type from {MacAddress}.", macAddress);
                return false;
            }

            if (bytes[3] != 0x03)
            {
                logger?.LogDebug("Ignored packet with invalid Version from {MacAddress}.", macAddress);
                return false;
            }

            int battery = bytes[4];
            short rawX = ReadInt16BigEndian(bytes[5], bytes[6]);
            short rawY = ReadInt16BigEndian(bytes[7], bytes[8]);
            short rawZ = ReadInt16BigEndian(bytes[9], bytes[10]);

            double x = rawX / 256.0;
            double y = rawY / 256.0;
            double z = rawZ / 256.0;

            string mac = FormatLittleEndianMac(bytes.Slice(11, 6));

            frame = new MinewAccFrame
            {
                MacAddress = mac,
                BatteryLevel = battery,
                XAxis = x,
                YAxis = y,
                ZAxis = z
            };

            return true;
        }

        private static short ReadInt16BigEndian(byte high, byte low)
        {
            return unchecked((short)((high << 8) | low));
        }

        public static string FormatLittleEndianMac(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 6)
            {
                return string.Empty;
            }

            return string.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
                bytes[5],
                bytes[4],
                bytes[3],
                bytes[2],
                bytes[1],
                bytes[0]);
        }
    }
}
