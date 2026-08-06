using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetTracking.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddScannerCoordinates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "MapXPercent",
                table: "Scanners",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MapYPercent",
                table: "Scanners",
                type: "float",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MapXPercent",
                table: "Scanners");

            migrationBuilder.DropColumn(
                name: "MapYPercent",
                table: "Scanners");
        }
    }
}
