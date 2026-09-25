using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mapache.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FormActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FormActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FormTemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Target = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    SecretEncrypted = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FormActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FormActions_FormTemplates_FormTemplateId",
                        column: x => x.FormTemplateId,
                        principalTable: "FormTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SmtpSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Host = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    UseTls = table.Column<bool>(type: "boolean", nullable: false),
                    Username = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PasswordEncrypted = table.Column<string>(type: "text", nullable: true),
                    From = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmtpSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ActionExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FormActionId = table.Column<Guid>(type: "uuid", nullable: false),
                    FormSubmissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActionExecutions_FormActions_FormActionId",
                        column: x => x.FormActionId,
                        principalTable: "FormActions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ActionExecutions_FormSubmissions_FormSubmissionId",
                        column: x => x.FormSubmissionId,
                        principalTable: "FormSubmissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActionExecutions_FormActionId",
                table: "ActionExecutions",
                column: "FormActionId");

            migrationBuilder.CreateIndex(
                name: "IX_ActionExecutions_FormSubmissionId",
                table: "ActionExecutions",
                column: "FormSubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_FormActions_FormTemplateId",
                table: "FormActions",
                column: "FormTemplateId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActionExecutions");

            migrationBuilder.DropTable(
                name: "SmtpSettings");

            migrationBuilder.DropTable(
                name: "FormActions");
        }
    }
}
