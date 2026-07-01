using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetTracking.Web.Migrations
{
    /// <inheritdoc />
    public partial class RefactorLocationManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Location",
                table: "BeaconDevices");

            migrationBuilder.AddColumn<string>(
                name: "Building",
                table: "Scanners",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Building",
                table: "Scanners");

            migrationBuilder.AddColumn<string>(
                name: "Location",
                table: "BeaconDevices",
                type: "nvarchar(max)",
                nullable: true);
        }
    }
}
