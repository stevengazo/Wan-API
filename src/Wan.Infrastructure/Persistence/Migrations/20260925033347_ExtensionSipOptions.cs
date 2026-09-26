using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mapache.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExtensionSipOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowIpRewrite",
                table: "Extensions",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "Codecs",
                table: "Extensions",
                type: "text[]",
                nullable: false,
                defaultValueSql: "ARRAY['PCMU','PCMA']::text[]");

            migrationBuilder.AddColumn<string>(
                name: "DialPrefix",
                table: "Extensions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DtmfMode",
                table: "Extensions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Rfc2833");

            migrationBuilder.AddColumn<bool>(
                name: "HideCallerId",
                table: "Extensions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "LocalPort",
                table: "Extensions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxConcurrentCalls",
                table: "Extensions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "MediaEncryption",
                table: "Extensions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Disabled");

            migrationBuilder.AddColumn<int>(
                name: "RegisterRetrySeconds",
                table: "Extensions",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<string>(
                name: "SecondarySipServer",
                table: "Extensions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SessionTimerSeconds",
                table: "Extensions",
                type: "integer",
                nullable: false,
                defaultValue: 1800);

            migrationBuilder.AddColumn<bool>(
                name: "UseIce",
                table: "Extensions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "VoicemailNumber",
                table: "Extensions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowIpRewrite",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "Codecs",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "DialPrefix",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "DtmfMode",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "HideCallerId",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "LocalPort",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "MaxConcurrentCalls",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "MediaEncryption",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "RegisterRetrySeconds",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "SecondarySipServer",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "SessionTimerSeconds",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "UseIce",
                table: "Extensions");

            migrationBuilder.DropColumn(
                name: "VoicemailNumber",
                table: "Extensions");
        }
    }
}
