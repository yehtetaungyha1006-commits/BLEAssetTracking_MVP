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

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Configure BeaconDevice
            modelBuilder.Entity<BeaconDevice>(entity =>
            {
                entity.HasKey(e => e.DeviceId);
                entity.Property(e => e.MacAddress).IsRequired().HasMaxLength(50);
                entity.HasIndex(e => e.MacAddress).IsUnique();
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
            });

            // Configure AlertLog
            modelBuilder.Entity<AlertLog>(entity =>
            {
                entity.HasKey(e => e.AlertId);
                entity.Property(e => e.AlertTime).HasDefaultValueSql("GETDATE()");

                // Relationship with BeaconDevice
                entity.HasOne(a => a.Device)
                      .WithMany(d => d.Alerts)
                      .HasForeignKey(a => a.DeviceId)
                      .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}
