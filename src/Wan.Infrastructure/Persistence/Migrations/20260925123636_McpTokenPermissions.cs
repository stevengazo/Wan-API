using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wan.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class McpTokenPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowAll",
                table: "McpAccessTokens",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "AllowedTools",
                table: "McpAccessTokens",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowAll",
                table: "McpAccessTokens");

            migrationBuilder.DropColumn(
                name: "AllowedTools",
                table: "McpAccessTokens");
        }
    }
}
