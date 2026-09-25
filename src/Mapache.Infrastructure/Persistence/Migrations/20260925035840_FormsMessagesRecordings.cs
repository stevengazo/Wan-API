using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mapache.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FormsMessagesRecordings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CallMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Recipient = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DirectoryEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    CallerName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CallbackNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Urgent = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ConversationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CallMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CallMessages_DirectoryEntries_DirectoryEntryId",
                        column: x => x.DirectoryEntryId,
                        principalTable: "DirectoryEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "CallSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    RecordCalls = table.Column<bool>(type: "boolean", nullable: false),
                    RecordingNotice = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CallSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FormTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Fields = table.Column<string>(type: "jsonb", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FormTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Recordings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    FileName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Recordings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FormSubmissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FormTemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Values = table.Column<string>(type: "jsonb", nullable: false),
                    ConversationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CallerNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FormSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FormSubmissions_FormTemplates_FormTemplateId",
                        column: x => x.FormTemplateId,
                        principalTable: "FormTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CallMessages_CreatedAt",
                table: "CallMessages",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CallMessages_DirectoryEntryId",
                table: "CallMessages",
                column: "DirectoryEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_FormSubmissions_CreatedAt",
                table: "FormSubmissions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_FormSubmissions_FormTemplateId",
                table: "FormSubmissions",
                column: "FormTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_Recordings_ConversationId",
                table: "Recordings",
                column: "ConversationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Recordings_CreatedAt",
                table: "Recordings",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CallMessages");

            migrationBuilder.DropTable(
                name: "CallSettings");

            migrationBuilder.DropTable(
                name: "FormSubmissions");

            migrationBuilder.DropTable(
                name: "Recordings");

            migrationBuilder.DropTable(
                name: "FormTemplates");
        }
    }
}
