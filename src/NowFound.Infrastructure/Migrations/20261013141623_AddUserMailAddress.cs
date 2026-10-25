using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NowFound.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserMailAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MailAddress",
                table: "AspNetUsers",
                type: "TEXT",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MailAddress",
                table: "AspNetUsers");
        }
    }
}
