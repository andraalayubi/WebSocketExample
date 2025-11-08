using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebSocketExample.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTitleFromDocument : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Title",
                table: "Documents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "Documents",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
