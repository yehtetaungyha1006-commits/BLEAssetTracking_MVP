using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetTracking.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertLogDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "DeviceId",
                table: "AlertLogs",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<bool>(
                name: "IsResolved",
                table: "AlertLogs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolvedAt",
                table: "AlertLogs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScannerId",
                table: "AlertLogs",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Severity",
                table: "AlertLogs",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Info");

            migrationBuilder.CreateIndex(
                name: "IX_AlertLogs_ScannerId",
                table: "AlertLogs",
                column: "ScannerId");

            migrationBuilder.AddForeignKey(
                name: "FK_AlertLogs_Scanners_ScannerId",
                table: "AlertLogs",
                column: "ScannerId",
                principalTable: "Scanners",
                principalColumn: "ScannerId",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AlertLogs_Scanners_ScannerId",
                table: "AlertLogs");

            migrationBuilder.DropIndex(
                name: "IX_AlertLogs_ScannerId",
                table: "AlertLogs");

            migrationBuilder.DropColumn(
                name: "IsResolved",
                table: "AlertLogs");

            migrationBuilder.DropColumn(
                name: "ResolvedAt",
                table: "AlertLogs");

            migrationBuilder.DropColumn(
                name: "ScannerId",
                table: "AlertLogs");

            migrationBuilder.DropColumn(
                name: "Severity",
                table: "AlertLogs");

            migrationBuilder.AlterColumn<int>(
                name: "DeviceId",
                table: "AlertLogs",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);
        }
    }
}
