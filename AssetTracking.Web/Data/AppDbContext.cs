using Microsoft.EntityFrameworkCore;
using AssetTracking.Web.Models;

namespace AssetTracking.Web.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<BeaconDevice> BeaconDevices { get; set; }
        public DbSet<BeaconTelemetry> BeaconTelemetries { get; set; }
        public DbSet<AlertLog> AlertLogs { get; set; }
        public DbSet<ScannerDevice> Scanners { get; set; }
        public DbSet<Building> Buildings { get; set; }
        public DbSet<Floor> Floors { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Configure Building
            modelBuilder.Entity<Building>(entity =>
            {
                entity.HasKey(e => e.BuildingId);
                entity.Property(e => e.BuildingName).IsRequired().HasMaxLength(100);
                entity.Property(e => e.Description).HasMaxLength(250);
                entity.Property(e => e.IsActive).HasDefaultValue(true);
                entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETDATE()");
            });

            // Configure Floor
            modelBuilder.Entity<Floor>(entity =>
            {
                entity.HasKey(e => e.FloorId);
                entity.Property(e => e.FloorName).IsRequired().HasMaxLength(100);
                entity.Property(e => e.FloorMapImagePath).HasMaxLength(250);
                entity.Property(e => e.IsActive).HasDefaultValue(true);
                entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETDATE()");

                entity.HasOne(f => f.Building)
                      .WithMany(b => b.Floors)
                      .HasForeignKey(f => f.BuildingId)
                      .OnDelete(DeleteBehavior.Restrict);
            });

            // Configure ScannerDevice
            modelBuilder.Entity<ScannerDevice>(entity =>
            {
                entity.HasKey(e => e.ScannerId);
                entity.Property(e => e.ScannerName).IsRequired().HasMaxLength(100);
                entity.Property(e => e.Building).IsRequired().HasMaxLength(100);
                entity.Property(e => e.Floor).IsRequired().HasMaxLength(50);
                entity.Property(e => e.Location).IsRequired().HasMaxLength(100);
                entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
                entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETDATE()");

                entity.HasOne(s => s.BuildingRef)
                      .WithMany()
                      .HasForeignKey(s => s.BuildingId)
                      .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(s => s.FloorRef)
                      .WithMany()
                      .HasForeignKey(s => s.FloorId)
                      .OnDelete(DeleteBehavior.Restrict);
            });

            // Configure BeaconDevice
            modelBuilder.Entity<BeaconDevice>(entity =>
            {
                entity.HasKey(e => e.DeviceId);
                entity.Property(e => e.MacAddress).IsRequired().HasMaxLength(50);
                entity.HasIndex(e => e.MacAddress).IsUnique();
                entity.HasIndex(e => new { e.Major, e.Minor }).IsUnique();
                entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETDATE()");
            });

            // Configure BeaconTelemetry
            modelBuilder.Entity<BeaconTelemetry>(entity =>
            {
                entity.HasKey(e => e.TelemetryId);
                entity.Property(e => e.ReceiveTime).HasDefaultValueSql("GETDATE()");

                // Relationship with BeaconDevice
                entity.HasOne(t => t.Device)
                      .WithMany(d => d.Telemetries)
                      .HasForeignKey(t => t.DeviceId)
                      .OnDelete(DeleteBehavior.Cascade);

                // Relationship with ScannerDevice
                entity.HasOne(t => t.Scanner)
                      .WithMany(s => s.Telemetries)
                      .HasForeignKey(t => t.ScannerId)
                      .OnDelete(DeleteBehavior.SetNull);
            });

            // Configure AlertLog
            modelBuilder.Entity<AlertLog>(entity =>
            {
                entity.HasKey(e => e.AlertId);
                entity.Property(e => e.AlertTime).HasDefaultValueSql("GETDATE()");
                entity.Property(e => e.IsResolved).HasDefaultValue(false);
                entity.Property(e => e.Severity).IsRequired().HasMaxLength(50).HasDefaultValue("Info");
                entity.Property(e => e.ScannerId).HasMaxLength(450);

                // Relationship with BeaconDevice
                entity.HasOne(a => a.Device)
                      .WithMany(d => d.Alerts)
                      .HasForeignKey(a => a.DeviceId)
                      .OnDelete(DeleteBehavior.Cascade);

                // Relationship with ScannerDevice
                entity.HasOne(a => a.Scanner)
                      .WithMany()
                      .HasForeignKey(a => a.ScannerId)
                      .OnDelete(DeleteBehavior.SetNull);
            });
        }
    }
}
