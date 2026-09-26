using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mapache.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FormHttpActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BodyTemplate",
                table: "FormActions",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HeadersEncrypted",
                table: "FormActions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Method",
                table: "FormActions",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BodyTemplate",
                table: "FormActions");

            migrationBuilder.DropColumn(
                name: "HeadersEncrypted",
                table: "FormActions");

            migrationBuilder.DropColumn(
                name: "Method",
                table: "FormActions");
        }
    }
}
