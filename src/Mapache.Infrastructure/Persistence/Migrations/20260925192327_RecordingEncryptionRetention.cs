using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mapache.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecordingEncryptionRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EncryptRecordings",
                table: "StorageSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "RecordingKeyEncrypted",
                table: "StorageSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Encrypted",
                table: "Recordings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RecordingRetentionDays",
                table: "CallSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TranscriptRetentionDays",
                table: "CallSettings",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EncryptRecordings",
                table: "StorageSettings");

            migrationBuilder.DropColumn(
                name: "RecordingKeyEncrypted",
                table: "StorageSettings");

            migrationBuilder.DropColumn(
                name: "Encrypted",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "RecordingRetentionDays",
                table: "CallSettings");

            migrationBuilder.DropColumn(
                name: "TranscriptRetentionDays",
                table: "CallSettings");
        }
    }
}
