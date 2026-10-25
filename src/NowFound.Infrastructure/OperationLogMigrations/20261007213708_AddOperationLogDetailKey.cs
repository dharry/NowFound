using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NowFound.Infrastructure.OperationLogMigrations
{
    /// <inheritdoc />
    public partial class AddOperationLogDetailKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DetailArgs",
                table: "OperationLogs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DetailKey",
                table: "OperationLogs",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DetailArgs",
                table: "OperationLogs");

            migrationBuilder.DropColumn(
                name: "DetailKey",
                table: "OperationLogs");
        }
    }
}
