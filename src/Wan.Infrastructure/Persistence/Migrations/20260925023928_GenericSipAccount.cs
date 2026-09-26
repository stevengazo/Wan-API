using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mapache.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GenericSipAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnswerMode",
                table: "Extensions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Bot");

            migrationBuilder.AddColumn<string>(
                name: "AuthUsername",
                table: "Extensions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "Extensions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "KeepAliveSeconds",
                table: "Extensions",
                type: "integer",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.AddColumn<string>(
                name: "PublicAddress",
                table: "Extensions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RegisterExpirySeconds",
                table: "Extensions",
                type: "integer",
                nullable: false,
                defaultValue: 300);

            migrationBuilder.AddColumn<string>(
                name: "SipDomain",
                table: "Extensions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SipProxy",
                table: "Extensions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SipServer",
                table: "Extensions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "StunServer",
                table: "Extensions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Transport",
                table: "Extensions",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "Udp");

            // El servidor pasaba por el proveedor: se copia a cada extensión antes de borrar la tabla.
            migrationBuilder.Sql(
                """
                UPDATE "Extensions" e SET "SipServer" = p."SipServer"
                FROM "Providers" p WHERE p."Id" = e."ProviderId";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Extensions_Providers_ProviderId",
                table: "Extensions");

            migrationBuilder.DropTable(
                name: "Providers");

            migrationBuilder.DropIndex(
                name: "IX_Extensions_ProviderId_SipUsername",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "ProviderId",
                table: "Extensions");

            migrationBuilder.CreateIndex(
                name: "IX_Extensions_SipServer_SipUsername",
                table: "Extensions",
                columns: new[] { "SipServer", "SipUsername" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Extensions_SipServer_SipUsername",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "AnswerMode",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "AuthUsername",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "KeepAliveSeconds",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "PublicAddress",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "RegisterExpirySeconds",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "SipDomain",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "SipProxy",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "SipServer",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "StunServer",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "Transport",
                table: "Extensions");

            migrationBuilder.AddColumn<Guid>(
                name: "ProviderId",
                table: "Extensions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "Providers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SipServer = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Providers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Extensions_ProviderId_SipUsername",
                table: "Extensions",
                columns: new[] { "ProviderId", "SipUsername" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Extensions_Providers_ProviderId",
                table: "Extensions",
                column: "ProviderId",
                principalTable: "Providers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
