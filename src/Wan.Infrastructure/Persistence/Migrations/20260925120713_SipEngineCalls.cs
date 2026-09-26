using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mapache.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SipEngineCalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AnsweredAt",
                table: "Calls",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Direction",
                table: "Calls",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Inbound");

            migrationBuilder.AddColumn<string>(
                name: "EndReason",
                table: "Calls",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VoiceSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    ApiKeyEncrypted = table.Column<string>(type: "text", nullable: true),
                    AgentId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoiceSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VoiceSettings");

            migrationBuilder.DropColumn(
                name: "AnsweredAt",
                table: "Calls");

            migrationBuilder.DropColumn(
                name: "Direction",
                table: "Calls");

            migrationBuilder.DropColumn(
                name: "EndReason",
                table: "Calls");
        }
    }
}
