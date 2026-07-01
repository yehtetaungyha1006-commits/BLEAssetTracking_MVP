using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetTracking.Web.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BeaconDevices",
                columns: table => new
                {
                    DeviceId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MacAddress = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DeviceName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Location = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastSeen = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BeaconDevices", x => x.DeviceId);
                });

            migrationBuilder.CreateTable(
                name: "AlertLogs",
                columns: table => new
                {
                    AlertId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<int>(type: "int", nullable: false),
                    AlertType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AlertMessage = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AlertTime = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertLogs", x => x.AlertId);
                    table.ForeignKey(
                        name: "FK_AlertLogs_BeaconDevices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "BeaconDevices",
                        principalColumn: "DeviceId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BeaconTelemetries",
                columns: table => new
                {
                    TelemetryId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<int>(type: "int", nullable: false),
                    Rssi = table.Column<int>(type: "int", nullable: false),
                    BatteryLevel = table.Column<int>(type: "int", nullable: false),
                    XAxis = table.Column<double>(type: "float", nullable: false),
                    YAxis = table.Column<double>(type: "float", nullable: false),
                    ZAxis = table.Column<double>(type: "float", nullable: false),
                    IsMoving = table.Column<bool>(type: "bit", nullable: false),
                    ReceiveTime = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BeaconTelemetries", x => x.TelemetryId);
                    table.ForeignKey(
                        name: "FK_BeaconTelemetries_BeaconDevices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "BeaconDevices",
                        principalColumn: "DeviceId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertLogs_DeviceId",
                table: "AlertLogs",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_BeaconDevices_MacAddress",
                table: "BeaconDevices",
                column: "MacAddress",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BeaconTelemetries_DeviceId",
                table: "BeaconTelemetries",
                column: "DeviceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertLogs");

            migrationBuilder.DropTable(
                name: "BeaconTelemetries");

            migrationBuilder.DropTable(
                name: "BeaconDevices");
        }
    }
}
