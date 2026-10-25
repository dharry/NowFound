using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NowFound.Infrastructure.FilesMigrations
{
    /// <inheritdoc />
    public partial class AddDirectoriesUpdateTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"CREATE TRIGGER IF NOT EXISTS Directories_au AFTER UPDATE OF Path ON Directories BEGIN
                INSERT INTO DirectoriesFts(DirectoriesFts, rowid, Path) VALUES ('delete', old.Id, old.Path);
                INSERT INTO DirectoriesFts(rowid, Path) VALUES (new.Id, new.Path);
            END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Directories_au;");
        }
    }
}
