using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mapache.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExtensionLlm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LlmModel",
                table: "Extensions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LlmProvider",
                table: "Extensions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SystemPrompt",
                table: "Extensions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LlmModel",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "LlmProvider",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "SystemPrompt",
                table: "Extensions");
        }
    }
}
