using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NowFound.Infrastructure.FilesMigrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Files",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SearchFolderId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    FolderPath = table.Column<string>(type: "TEXT", nullable: false),
                    FullPath = table.Column<string>(type: "TEXT", nullable: false),
                    IsDirectory = table.Column<bool>(type: "INTEGER", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: true),
                    Extension = table.Column<string>(type: "TEXT", nullable: false),
                    Owner = table.Column<string>(type: "TEXT", nullable: true),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Files", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Files_Extension",
                table: "Files",
                column: "Extension");

            migrationBuilder.CreateIndex(
                name: "IX_Files_ModifiedAt",
                table: "Files",
                column: "ModifiedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Files_SearchFolderId_FullPath",
                table: "Files",
                columns: new[] { "SearchFolderId", "FullPath" },
                unique: true);

            // FTS5（trigram: 部分一致検索）。Files テーブルを外部コンテンツとして参照し、トリガーで同期する。
            migrationBuilder.Sql(@"CREATE VIRTUAL TABLE FilesFts USING fts5(
                Name, FolderPath, content='Files', content_rowid='Id', tokenize='trigram');");
            migrationBuilder.Sql(@"CREATE TRIGGER Files_ai AFTER INSERT ON Files BEGIN
                INSERT INTO FilesFts(rowid, Name, FolderPath) VALUES (new.Id, new.Name, new.FolderPath);
            END;");
            migrationBuilder.Sql(@"CREATE TRIGGER Files_ad AFTER DELETE ON Files BEGIN
                INSERT INTO FilesFts(FilesFts, rowid, Name, FolderPath) VALUES ('delete', old.Id, old.Name, old.FolderPath);
            END;");
            migrationBuilder.Sql(@"CREATE TRIGGER Files_au AFTER UPDATE ON Files BEGIN
                INSERT INTO FilesFts(FilesFts, rowid, Name, FolderPath) VALUES ('delete', old.Id, old.Name, old.FolderPath);
                INSERT INTO FilesFts(rowid, Name, FolderPath) VALUES (new.Id, new.Name, new.FolderPath);
            END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Files_ai;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Files_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Files_au;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS FilesFts;");
            migrationBuilder.DropTable(
                name: "Files");
        }
    }
}
