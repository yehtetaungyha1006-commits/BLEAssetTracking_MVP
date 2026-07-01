using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetTracking.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddScanners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ScannerId",
                table: "BeaconTelemetries",
                type: "nvarchar(450)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Scanners",
                columns: table => new
                {
                    ScannerId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ScannerName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Floor = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Location = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    LastSeen = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Scanners", x => x.ScannerId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BeaconTelemetries_ScannerId",
                table: "BeaconTelemetries",
                column: "ScannerId");

            migrationBuilder.AddForeignKey(
                name: "FK_BeaconTelemetries_Scanners_ScannerId",
                table: "BeaconTelemetries",
                column: "ScannerId",
                principalTable: "Scanners",
                principalColumn: "ScannerId",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BeaconTelemetries_Scanners_ScannerId",
                table: "BeaconTelemetries");

            migrationBuilder.DropTable(
                name: "Scanners");

            migrationBuilder.DropIndex(
                name: "IX_BeaconTelemetries_ScannerId",
                table: "BeaconTelemetries");

            migrationBuilder.DropColumn(
                name: "ScannerId",
                table: "BeaconTelemetries");
        }
    }
}
