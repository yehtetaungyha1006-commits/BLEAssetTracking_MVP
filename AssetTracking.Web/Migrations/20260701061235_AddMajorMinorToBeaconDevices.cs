using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetTracking.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddMajorMinorToBeaconDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Major",
                table: "BeaconDevices",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Minor",
                table: "BeaconDevices",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("UPDATE BeaconDevices SET Minor = DeviceId;");

            migrationBuilder.CreateIndex(
                name: "IX_BeaconDevices_Major_Minor",
                table: "BeaconDevices",
                columns: new[] { "Major", "Minor" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BeaconDevices_Major_Minor",
                table: "BeaconDevices");

            migrationBuilder.DropColumn(
                name: "Major",
                table: "BeaconDevices");

            migrationBuilder.DropColumn(
                name: "Minor",
                table: "BeaconDevices");
        }
    }
}
