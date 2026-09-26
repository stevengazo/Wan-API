using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wan.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecordingStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CallId",
                table: "Recordings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentType",
                table: "Recordings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "audio/mpeg");

            migrationBuilder.AddColumn<double>(
                name: "DurationSeconds",
                table: "Recordings",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Storage",
                table: "Recordings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Local");

            migrationBuilder.CreateTable(
                name: "StorageSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    S3Bucket = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    S3Region = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    S3ServiceUrl = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    S3AccessKeyId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    S3SecretKeyEncrypted = table.Column<string>(type: "text", nullable: true),
                    AzureConnectionStringEncrypted = table.Column<string>(type: "text", nullable: true),
                    AzureContainer = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Prefix = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StorageSettings");

            migrationBuilder.DropColumn(
                name: "CallId",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "ContentType",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "DurationSeconds",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "Storage",
                table: "Recordings");
        }
    }
}
