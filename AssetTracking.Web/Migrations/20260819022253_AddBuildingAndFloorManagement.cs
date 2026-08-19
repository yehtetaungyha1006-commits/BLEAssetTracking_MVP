using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetTracking.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildingAndFloorManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BuildingId",
                table: "Scanners",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FloorId",
                table: "Scanners",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Buildings",
                columns: table => new
                {
                    BuildingId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BuildingName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Buildings", x => x.BuildingId);
                });

            migrationBuilder.CreateTable(
                name: "Floors",
                columns: table => new
                {
                    FloorId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BuildingId = table.Column<int>(type: "int", nullable: false),
                    FloorName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FloorNumber = table.Column<int>(type: "int", nullable: true),
                    FloorMapImagePath = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Floors", x => x.FloorId);
                    table.ForeignKey(
                        name: "FK_Floors_Buildings_BuildingId",
                        column: x => x.BuildingId,
                        principalTable: "Buildings",
                        principalColumn: "BuildingId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Scanners_BuildingId",
                table: "Scanners",
                column: "BuildingId");

            migrationBuilder.CreateIndex(
                name: "IX_Scanners_FloorId",
                table: "Scanners",
                column: "FloorId");

            migrationBuilder.CreateIndex(
                name: "IX_Floors_BuildingId",
                table: "Floors",
                column: "BuildingId");

            migrationBuilder.AddForeignKey(
                name: "FK_Scanners_Buildings_BuildingId",
                table: "Scanners",
                column: "BuildingId",
                principalTable: "Buildings",
                principalColumn: "BuildingId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Scanners_Floors_FloorId",
                table: "Scanners",
                column: "FloorId",
                principalTable: "Floors",
                principalColumn: "FloorId",
                onDelete: ReferentialAction.Restrict);

            // Seed / Data Migration: Convert existing Scanner text Building/Floor to relational entities
            migrationBuilder.Sql(@"
                -- Insert distinct buildings from Scanners
                INSERT INTO Buildings (BuildingName, Description, IsActive, CreatedAt)
                SELECT DISTINCT s.Building, 'Auto-migrated Building', 1, GETDATE()
                FROM Scanners s
                WHERE s.Building IS NOT NULL AND s.Building <> ''
                  AND NOT EXISTS (SELECT 1 FROM Buildings b WHERE b.BuildingName = s.Building);

                -- Insert distinct floors from Scanners
                INSERT INTO Floors (BuildingId, FloorName, IsActive, CreatedAt)
                SELECT DISTINCT b.BuildingId, s.Floor, 1, GETDATE()
                FROM Scanners s
                INNER JOIN Buildings b ON b.BuildingName = s.Building
                WHERE s.Floor IS NOT NULL AND s.Floor <> ''
                  AND NOT EXISTS (SELECT 1 FROM Floors f WHERE f.BuildingId = b.BuildingId AND f.FloorName = s.Floor);

                -- Link Scanners to BuildingId and FloorId
                UPDATE s
                SET s.BuildingId = b.BuildingId,
                    s.FloorId = f.FloorId
                FROM Scanners s
                INNER JOIN Buildings b ON b.BuildingName = s.Building
                INNER JOIN Floors f ON f.BuildingId = b.BuildingId AND f.FloorName = s.Floor;

                -- Fallback seed if Buildings table is still empty
                IF NOT EXISTS (SELECT 1 FROM Buildings)
                BEGIN
                    INSERT INTO Buildings (BuildingName, Description, IsActive, CreatedAt)
                    VALUES ('Siriraj Building', 'Main Siriraj Building', 1, GETDATE());

                    DECLARE @SirirajId INT = SCOPE_IDENTITY();

                    INSERT INTO Floors (BuildingId, FloorName, FloorNumber, IsActive, CreatedAt)
                    VALUES (@SirirajId, 'Floor 6', 6, 1, GETDATE());
                END
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Scanners_Buildings_BuildingId",
                table: "Scanners");

            migrationBuilder.DropForeignKey(
                name: "FK_Scanners_Floors_FloorId",
                table: "Scanners");

            migrationBuilder.DropTable(
                name: "Floors");

            migrationBuilder.DropTable(
                name: "Buildings");

            migrationBuilder.DropIndex(
                name: "IX_Scanners_BuildingId",
                table: "Scanners");

            migrationBuilder.DropIndex(
                name: "IX_Scanners_FloorId",
                table: "Scanners");

            migrationBuilder.DropColumn(
                name: "BuildingId",
                table: "Scanners");

            migrationBuilder.DropColumn(
                name: "FloorId",
                table: "Scanners");
        }
    }
}
