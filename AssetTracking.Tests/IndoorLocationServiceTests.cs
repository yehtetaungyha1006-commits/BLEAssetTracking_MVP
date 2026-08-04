using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using AssetTracking.Web.Data;
using AssetTracking.Web.Helpers;
using AssetTracking.Web.Models;
using AssetTracking.Web.Services;
using Xunit;

namespace AssetTracking.Tests
{
    public class IndoorLocationServiceTests
    {
        private AppDbContext CreateDbContext(string dbName)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: dbName)
                .Options;
            return new AppDbContext(options);
        }

        private IServiceScopeFactory CreateScopeFactory(AppDbContext context)
        {
            var services = new ServiceCollection();
            services.AddSingleton(context);
            var serviceProvider = services.BuildServiceProvider();
            return serviceProvider.GetRequiredService<IServiceScopeFactory>();
        }

        private IConfiguration CreateConfiguration(int window = 10, int minRssi = -95, int margin = 6, int stable = 3)
        {
            var myConfiguration = new Dictionary<string, string?>
            {
                {"IndoorLocationSettings:ObservationWindowSeconds", window.ToString()},
                {"IndoorLocationSettings:MinimumRssi", minRssi.ToString()},
                {"IndoorLocationSettings:SwitchMarginDb", margin.ToString()},
                {"IndoorLocationSettings:RequiredStableReadings", stable.ToString()}
            };

            return new ConfigurationBuilder()
                .AddInMemoryCollection(myConfiguration)
                .Build();
        }

        private async Task SeedBaseDataAsync(AppDbContext context)
        {
            // Seed Scanners
            context.Scanners.AddRange(
                new ScannerDevice { ScannerId = "ScannerA", ScannerName = "Scanner-A", Building = "B1", Floor = "F1", Location = "Room A", Status = "Online", LastSeen = DateTime.Now, CreatedAt = DateTime.Now },
                new ScannerDevice { ScannerId = "ScannerB", ScannerName = "Scanner-B", Building = "B1", Floor = "F1", Location = "Room B", Status = "Online", LastSeen = DateTime.Now, CreatedAt = DateTime.Now }
            );

            // Seed Beacon Device
            context.BeaconDevices.Add(
                new BeaconDevice { DeviceId = 1, MacAddress = "C3:00:00:00:00:01", DeviceName = "TestBeacon", Major = 1, Minor = 1, Status = "Online", LastSeen = DateTime.Now, CreatedAt = DateTime.Now }
            );

            await context.SaveChangesAsync();
        }

        [Fact]
        public async Task Test1_StrongestScannerSelected()
        {
            using var context = CreateDbContext(nameof(Test1_StrongestScannerSelected));
            await SeedBaseDataAsync(context);

            // Add telemetries for device 1: B first (weaker), then A second (stronger)
            context.BeaconTelemetries.Add(
                new BeaconTelemetry { DeviceId = 1, ScannerId = "ScannerB", Rssi = -65, ReceiveTime = DateTime.Now }
            );
            await context.SaveChangesAsync();

            context.BeaconTelemetries.Add(
                new BeaconTelemetry { DeviceId = 1, ScannerId = "ScannerA", Rssi = -45, ReceiveTime = DateTime.Now }
            );
            await context.SaveChangesAsync();

            var service = new IndoorLocationService(CreateScopeFactory(context), NullLogger<IndoorLocationService>.Instance, CreateConfiguration());
            var result = await service.DetermineCurrentLocationAsync(1);

            Assert.True(result.IsAvailable);
            Assert.Equal("ScannerA", result.ScannerId);
            Assert.Equal(-45.0, result.RepresentativeRssi);
        }

        [Fact]
        public async Task Test2_RemainScannerWithinSwitchMargin()
        {
            using var context = CreateDbContext(nameof(Test2_RemainScannerWithinSwitchMargin));
            await SeedBaseDataAsync(context);

            // Initial location is ScannerA
            context.BeaconTelemetries.Add(
                new BeaconTelemetry { DeviceId = 1, ScannerId = "ScannerA", Rssi = -50, ReceiveTime = DateTime.Now }
            );
            await context.SaveChangesAsync();

            var service = new IndoorLocationService(CreateScopeFactory(context), NullLogger<IndoorLocationService>.Instance, CreateConfiguration(margin: 6));
            
            // First evaluation -> selected ScannerA
            var r1 = await service.DetermineCurrentLocationAsync(1);
            Assert.Equal("ScannerA", r1.ScannerId);

            // Add telemetry for ScannerB at -47 (difference is 3, which is < 6 margin)
            context.BeaconTelemetries.Add(
                new BeaconTelemetry { DeviceId = 1, ScannerId = "ScannerB", Rssi = -47, ReceiveTime = DateTime.Now }
            );
            await context.SaveChangesAsync();

            // Evaluate again -> should remain ScannerA
            var r2 = await service.DetermineCurrentLocationAsync(1);
            Assert.Equal("ScannerA", r2.ScannerId);
        }

        [Fact]
        public async Task Test3_RemainScannerWithSingleStrongReading()
        {
            using var context = CreateDbContext(nameof(Test3_RemainScannerWithSingleStrongReading));
            await SeedBaseDataAsync(context);

            // Initial location is ScannerA
            context.BeaconTelemetries.Add(
                new BeaconTelemetry { DeviceId = 1, ScannerId = "ScannerA", Rssi = -50, ReceiveTime = DateTime.Now }
            );
            await context.SaveChangesAsync();

            var service = new IndoorLocationService(CreateScopeFactory(context), NullLogger<IndoorLocationService>.Instance, CreateConfiguration(stable: 3, margin: 6));
            
            var r1 = await service.DetermineCurrentLocationAsync(1);
            Assert.Equal("ScannerA", r1.ScannerId);

            // Add single telemetry for ScannerB at -42 (difference is 8, eligible, but needs 3 stable readings)
            context.BeaconTelemetries.Add(
                new BeaconTelemetry { DeviceId = 1, ScannerId = "ScannerB", Rssi = -42, ReceiveTime = DateTime.Now }
            );
            await context.SaveChangesAsync();

            // Evaluate first time with ScannerB as strongest
            var r2 = await service.DetermineCurrentLocationAsync(1);
            Assert.Equal("ScannerA", r2.ScannerId); // Still ScannerA
        }

        [Fact]
        public async Task Test4_SwitchToScannerBAfterStableReadings()
        {
            using var context = CreateDbContext(nameof(Test4_SwitchToScannerBAfterStableReadings));
            await SeedBaseDataAsync(context);

            // Initial location is ScannerA
            context.BeaconTelemetries.Add(
                new BeaconTelemetry { DeviceId = 1, ScannerId = "ScannerA", Rssi = -50, ReceiveTime = DateTime.Now }
            );
            await context.SaveChangesAsync();

            var service = new IndoorLocationService(CreateScopeFactory(context), NullLogger<IndoorLocationService>.Instance, CreateConfiguration(stable: 3, margin: 6));
            
            var r1 = await service.DetermineCurrentLocationAsync(1);
            Assert.Equal("ScannerA", r1.ScannerId);

            // 1st evaluation with ScannerB strongest at -42
            context.BeaconTelemetries.Add(new BeaconTelemetry { DeviceId = 1, ScannerId = "ScannerB", Rssi = -42, ReceiveTime = DateTime.Now });
            await context.SaveChangesAsync();
            var r2 = await service.DetermineCurrentLocationAsync(1);
            Assert.Equal("ScannerA", r2.ScannerId);

            // 2nd evaluation with ScannerB strongest
            context.BeaconTelemetries.Add(new BeaconTelemetry { DeviceId = 1, ScannerId = "ScannerB", Rssi = -42, ReceiveTime = DateTime.Now });
            await context.SaveChangesAsync();
            var r3 = await service.DetermineCurrentLocationAsync(1);
            Assert.Equal("ScannerA", r3.ScannerId);

            // 3rd evaluation with ScannerB strongest -> now stable readings count = 3 -> should switch!
            context.BeaconTelemetries.Add(new BeaconTelemetry { DeviceId = 1, ScannerId = "ScannerB", Rssi = -42, ReceiveTime = DateTime.Now });
            await context.SaveChangesAsync();
            var r4 = await service.DetermineCurrentLocationAsync(1);
            Assert.Equal("ScannerB", r4.ScannerId);
        }

        [Fact]
        public async Task Test5_StrongestScannerOffline_SelectsNextStrongest()
        {
            using var context = CreateDbContext(nameof(Test5_StrongestScannerOffline_SelectsNextStrongest));
            await SeedBaseDataAsync(context);

            // Scanner A is offline
            var scannerA = await context.Scanners.FindAsync("ScannerA");
            scannerA!.LastSeen = DateTime.Now.AddSeconds(-60); // offline
            await context.SaveChangesAsync();

            // Telemetries: Scanner A is -45 (stronger, but offline), Scanner B is -65 (weaker, but online)
            context.BeaconTelemetries.AddRange(
                new BeaconTelemetry { DeviceId = 1, ScannerId = "ScannerA", Rssi = -45, ReceiveTime = DateTime.Now },
                new BeaconTelemetry { DeviceId = 1, ScannerId = "ScannerB", Rssi = -65, ReceiveTime = DateTime.Now }
            );
            await context.SaveChangesAsync();

            var service = new IndoorLocationService(CreateScopeFactory(context), NullLogger<IndoorLocationService>.Instance, CreateConfiguration());
            var result = await service.DetermineCurrentLocationAsync(1);

            Assert.True(result.IsAvailable);
            Assert.Equal("ScannerB", result.ScannerId); // ScannerB should be selected because ScannerA is offline
        }

        [Fact]
        public async Task Test6_AllReadingsOlderThanObservationWindow_ReturnsUnavailable()
        {
            using var context = CreateDbContext(nameof(Test6_AllReadingsOlderThanObservationWindow_ReturnsUnavailable));
            await SeedBaseDataAsync(context);

            // Telemetry older than 10 seconds (e.g. 15 seconds ago)
            context.BeaconTelemetries.Add(
                new BeaconTelemetry { DeviceId = 1, ScannerId = "ScannerA", Rssi = -45, ReceiveTime = DateTime.Now.AddSeconds(-15) }
            );
            await context.SaveChangesAsync();

            var service = new IndoorLocationService(CreateScopeFactory(context), NullLogger<IndoorLocationService>.Instance, CreateConfiguration(window: 10));
            var result = await service.DetermineCurrentLocationAsync(1);

            Assert.False(result.IsAvailable);
        }

        [Fact]
        public async Task Test7_AllRssiBelowMinimum_ReturnsUnavailable()
        {
            using var context = CreateDbContext(nameof(Test7_AllRssiBelowMinimum_ReturnsUnavailable));
            await SeedBaseDataAsync(context);

            // RSSI is -98, which is below minimum of -95
            context.BeaconTelemetries.Add(
                new BeaconTelemetry { DeviceId = 1, ScannerId = "ScannerA", Rssi = -98, ReceiveTime = DateTime.Now }
            );
            await context.SaveChangesAsync();

            var service = new IndoorLocationService(CreateScopeFactory(context), NullLogger<IndoorLocationService>.Instance, CreateConfiguration(minRssi: -95));
            var result = await service.DetermineCurrentLocationAsync(1);

            Assert.False(result.IsAvailable);
        }

        [Fact]
        public async Task Test8_MedianReducesTemporaryRssiSpike()
        {
            using var context = CreateDbContext(nameof(Test8_MedianReducesTemporaryRssiSpike));
            await SeedBaseDataAsync(context);

            // Scanner A: -45, -46, -30 (temporary spike) -> sorted: -46, -45, -30 -> Median: -45
            // Scanner B: -40, -42, -43 -> sorted: -43, -42, -40 -> Median: -42
            // Median -42 (Scanner B) is stronger than Median -45 (Scanner A) -> Scanner B should be selected
            // (If we used raw Max, Scanner A would win because of the -30 spike, but Median prevents this!)
            context.BeaconTelemetries.AddRange(
                new BeaconTelemetry { DeviceId = 1, ScannerId = "ScannerA", Rssi = -45, ReceiveTime = DateTime.Now },
                new BeaconTelemetry { DeviceId = 1, ScannerId = "ScannerA", Rssi = -46, ReceiveTime = DateTime.Now },
                new BeaconTelemetry { DeviceId = 1, ScannerId = "ScannerA", Rssi = -30, ReceiveTime = DateTime.Now }, // Spike

                new BeaconTelemetry { DeviceId = 1, ScannerId = "ScannerB", Rssi = -40, ReceiveTime = DateTime.Now },
                new BeaconTelemetry { DeviceId = 1, ScannerId = "ScannerB", Rssi = -42, ReceiveTime = DateTime.Now },
                new BeaconTelemetry { DeviceId = 1, ScannerId = "ScannerB", Rssi = -43, ReceiveTime = DateTime.Now }
            );
            await context.SaveChangesAsync();

            var service = new IndoorLocationService(CreateScopeFactory(context), NullLogger<IndoorLocationService>.Instance, CreateConfiguration());
            var result = await service.DetermineCurrentLocationAsync(1);

            Assert.True(result.IsAvailable);
            Assert.Equal("ScannerB", result.ScannerId);
            Assert.Equal(-42.0, result.RepresentativeRssi);
        }
    }
}
