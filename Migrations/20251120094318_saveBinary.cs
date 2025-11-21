using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebSocketExample.Migrations
{
    /// <inheritdoc />
    public partial class saveBinary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Content",
                table: "Documents",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<byte[]>(
                name: "LastUpdate",
                table: "Documents",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "YjsState",
                table: "Documents",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastUpdate",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "YjsState",
                table: "Documents");

            migrationBuilder.AlterColumn<string>(
                name: "Content",
                table: "Documents",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
