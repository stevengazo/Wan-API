using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wan.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GeneralLlmSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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

            migrationBuilder.CreateTable(
                name: "LlmProviderSettings",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ApiKeyEncrypted = table.Column<string>(type: "text", nullable: true),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlmProviderSettings", x => x.Provider);
                });

            migrationBuilder.CreateTable(
                name: "LlmSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    ActiveProvider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SystemPrompt = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlmSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LlmProviderSettings");

            migrationBuilder.DropTable(
                name: "LlmSettings");

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
    }
}
