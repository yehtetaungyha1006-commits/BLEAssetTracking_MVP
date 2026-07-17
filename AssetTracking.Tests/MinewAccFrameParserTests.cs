using System;
using Xunit;
using AssetTracking.Scanner.Parsers;

namespace AssetTracking.Tests
{
    public class MinewAccFrameParserTests
    {
        private static byte[] ConvertHexStringToBytes(string hex)
        {
            hex = hex.Replace("-", "");
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }

        [Fact]
        public void Test1_ValidPacket_ParsesCorrectly()
        {
            // Arrange
            byte[] input = ConvertHexStringToBytes("E1-FF-A1-03-64-00-05-00-00-01-0A-CB-89-4F-00-00-C3");

            // Act
            bool result = MinewAccFrameParser.TryParse(input, out var frame);

            // Assert
            Assert.True(result);
            Assert.NotNull(frame);
            Assert.Equal(100, frame.BatteryLevel);
            Assert.Equal(0.01953125, frame.XAxis);
            Assert.Equal(0.0, frame.YAxis);
            Assert.Equal(1.0390625, frame.ZAxis);
            Assert.Equal("C3:00:00:4F:89:CB", frame.MacAddress);
        }

        [Fact]
        public void Test2_ValidPacketWithNegativeValues_ParsesCorrectly()
        {
            // Arrange
            byte[] input = ConvertHexStringToBytes("E1-FF-A1-03-64-00-02-FF-FE-01-0A-CB-89-4F-00-00-C3");

            // Act
            bool result = MinewAccFrameParser.TryParse(input, out var frame);

            // Assert
            Assert.True(result);
            Assert.NotNull(frame);
            Assert.Equal(100, frame.BatteryLevel);
            Assert.Equal(0.0078125, frame.XAxis);
            Assert.Equal(-0.0078125, frame.YAxis);
            Assert.Equal(1.0390625, frame.ZAxis);
            Assert.Equal("C3:00:00:4F:89:CB", frame.MacAddress);
        }

        [Fact]
        public void Test3_InvalidUuid_ReturnsFalse()
        {
            // Arrange (UUID FEAA instead of FFE1)
            byte[] input = ConvertHexStringToBytes("AA-FE-A1-03-64-00-05-00-00-01-0A-CB-89-4F-00-00-C3");

            // Act
            bool result = MinewAccFrameParser.TryParse(input, out var frame);

            // Assert
            Assert.False(result);
            Assert.Null(frame);
        }

        [Fact]
        public void Test4_InvalidFrameType_ReturnsFalse()
        {
            // Arrange (Frame Type 40 instead of A1)
            byte[] input = ConvertHexStringToBytes("E1-FF-40-03-64-00-05-00-00-01-0A-CB-89-4F-00-00-C3");

            // Act
            bool result = MinewAccFrameParser.TryParse(input, out var frame);

            // Assert
            Assert.False(result);
            Assert.Null(frame);
        }

        [Fact]
        public void Test5_ShortPacket_ReturnsFalse()
        {
            // Arrange (13 bytes instead of 17)
            byte[] input = ConvertHexStringToBytes("E1-FF-A1-08-64-CB-89-4F-00-00-C3-45-37");

            // Act
            bool result = MinewAccFrameParser.TryParse(input, out var frame);

            // Assert
            Assert.False(result);
            Assert.Null(frame);
        }
    }
}
