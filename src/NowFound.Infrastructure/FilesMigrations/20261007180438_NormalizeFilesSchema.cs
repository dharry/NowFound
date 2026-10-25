using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NowFound.Infrastructure.FilesMigrations
{
    /// <summary>
    /// files.db のデータ量を減らすためのスキーマ変更。
    ///   - フルパス列を廃止（フォルダパス＋名前から組み立てる）。
    ///   - フォルダパスを Directories 表へ、オーナー名を Owners 表へ分離（ファイルごとの重複をなくす）。
    ///   - FTS5 はファイル名のみ（Files）と、フォルダパス（Directories）に分ける。
    /// files.db は Indexer が再生成できる派生データのため、データ移行はせず作り直す。
    /// 次回の Indexer 実行で全件を収集し直す（IndexFolderStates を空にして、必ずフル走査にする）。
    /// </summary>
    public partial class NormalizeFilesSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            DropOldFullTextSearch(migrationBuilder);
            migrationBuilder.DropTable(name: "Files");
            migrationBuilder.Sql("DELETE FROM IndexFolderStates;");

            migrationBuilder.CreateTable(
                name: "Directories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SearchFolderId = table.Column<int>(type: "INTEGER", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Directories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Owners",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Owners", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Files",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SearchFolderId = table.Column<int>(type: "INTEGER", nullable: false),
                    DirectoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    IsDirectory = table.Column<bool>(type: "INTEGER", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: true),
                    Extension = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: true),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Files_Directories_DirectoryId",
                        column: x => x.DirectoryId,
                        principalTable: "Directories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Files_Owners_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Owners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Directories_SearchFolderId_Path",
                table: "Directories",
                columns: new[] { "SearchFolderId", "Path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Owners_Name",
                table: "Owners",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Files_DirectoryId_Name",
                table: "Files",
                columns: new[] { "DirectoryId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(name: "IX_Files_Extension", table: "Files", column: "Extension");
            migrationBuilder.CreateIndex(name: "IX_Files_ModifiedAt", table: "Files", column: "ModifiedAt");
            migrationBuilder.CreateIndex(name: "IX_Files_OwnerId", table: "Files", column: "OwnerId");
            migrationBuilder.CreateIndex(name: "IX_Files_SearchFolderId", table: "Files", column: "SearchFolderId");

            // FTS5（trigram: 部分一致検索）。ファイル名は Files、フォルダパスは Directories を外部コンテンツとして参照する。
            // フォルダパスは Directories に 1 回だけ保存しているので、FTS のキーもフォルダごとに 1 回で済む。
            migrationBuilder.Sql(@"CREATE VIRTUAL TABLE FilesFts USING fts5(
                Name, content='Files', content_rowid='Id', tokenize='trigram');");
            migrationBuilder.Sql(@"CREATE TRIGGER Files_ai AFTER INSERT ON Files BEGIN
                INSERT INTO FilesFts(rowid, Name) VALUES (new.Id, new.Name);
            END;");
            migrationBuilder.Sql(@"CREATE TRIGGER Files_ad AFTER DELETE ON Files BEGIN
                INSERT INTO FilesFts(FilesFts, rowid, Name) VALUES ('delete', old.Id, old.Name);
            END;");
            // Indexer の更新（サイズ・日時・オーナー）では名前は変わらないため、名前が変わるときだけ作り直す。
            migrationBuilder.Sql(@"CREATE TRIGGER Files_au AFTER UPDATE OF Name ON Files BEGIN
                INSERT INTO FilesFts(FilesFts, rowid, Name) VALUES ('delete', old.Id, old.Name);
                INSERT INTO FilesFts(rowid, Name) VALUES (new.Id, new.Name);
            END;");

            migrationBuilder.Sql(@"CREATE VIRTUAL TABLE DirectoriesFts USING fts5(
                Path, content='Directories', content_rowid='Id', tokenize='trigram');");
            migrationBuilder.Sql(@"CREATE TRIGGER Directories_ai AFTER INSERT ON Directories BEGIN
                INSERT INTO DirectoriesFts(rowid, Path) VALUES (new.Id, new.Path);
            END;");
            migrationBuilder.Sql(@"CREATE TRIGGER Directories_ad AFTER DELETE ON Directories BEGIN
                INSERT INTO DirectoriesFts(DirectoriesFts, rowid, Path) VALUES ('delete', old.Id, old.Path);
            END;");
            migrationBuilder.Sql(@"CREATE TRIGGER Directories_au AFTER UPDATE OF Path ON Directories BEGIN
                INSERT INTO DirectoriesFts(DirectoriesFts, rowid, Path) VALUES ('delete', old.Id, old.Path);
                INSERT INTO DirectoriesFts(rowid, Path) VALUES (new.Id, new.Path);
            END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Directories_au;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Directories_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Directories_ai;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS DirectoriesFts;");
            DropOldFullTextSearch(migrationBuilder);
            migrationBuilder.DropTable(name: "Files");
            migrationBuilder.DropTable(name: "Owners");
            migrationBuilder.DropTable(name: "Directories");
            migrationBuilder.Sql("DELETE FROM IndexFolderStates;");

            // 変更前（InitialCreate）のスキーマ。データは再収集で戻る。
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

            migrationBuilder.CreateIndex(name: "IX_Files_Extension", table: "Files", column: "Extension");
            migrationBuilder.CreateIndex(name: "IX_Files_ModifiedAt", table: "Files", column: "ModifiedAt");
            migrationBuilder.CreateIndex(
                name: "IX_Files_SearchFolderId_FullPath",
                table: "Files",
                columns: new[] { "SearchFolderId", "FullPath" },
                unique: true);

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

        private static void DropOldFullTextSearch(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Files_ai;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Files_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Files_au;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS FilesFts;");
        }
    }
}
