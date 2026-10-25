using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NowFound.Infrastructure.FilesMigrations
{
    /// <inheritdoc />
    public partial class AddIndexFolderState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IndexFolderStates",
                columns: table => new
                {
                    SearchFolderId = table.Column<int>(type: "INTEGER", nullable: false),
                    SettingsHash = table.Column<string>(type: "TEXT", nullable: false),
                    LastScanUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastFullScanUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexFolderStates", x => x.SearchFolderId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IndexFolderStates");
        }
    }
}
