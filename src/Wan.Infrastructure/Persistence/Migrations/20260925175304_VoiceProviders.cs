using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wan.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VoiceProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeepgramApiKeyEncrypted",
                table: "VoiceSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeepgramLanguage",
                table: "VoiceSettings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeepgramListenModel",
                table: "VoiceSettings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeepgramSpeakModel",
                table: "VoiceSettings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Greeting",
                table: "VoiceSettings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OpenAiApiKeyEncrypted",
                table: "VoiceSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OpenAiModel",
                table: "VoiceSettings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OpenAiTranscriptionModel",
                table: "VoiceSettings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OpenAiVoice",
                table: "VoiceSettings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "VoiceSettings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "ElevenLabs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeepgramApiKeyEncrypted",
                table: "VoiceSettings");

            migrationBuilder.DropColumn(
                name: "DeepgramLanguage",
                table: "VoiceSettings");

            migrationBuilder.DropColumn(
                name: "DeepgramListenModel",
                table: "VoiceSettings");

            migrationBuilder.DropColumn(
                name: "DeepgramSpeakModel",
                table: "VoiceSettings");

            migrationBuilder.DropColumn(
                name: "Greeting",
                table: "VoiceSettings");

            migrationBuilder.DropColumn(
                name: "OpenAiApiKeyEncrypted",
                table: "VoiceSettings");

            migrationBuilder.DropColumn(
                name: "OpenAiModel",
                table: "VoiceSettings");

            migrationBuilder.DropColumn(
                name: "OpenAiTranscriptionModel",
                table: "VoiceSettings");

            migrationBuilder.DropColumn(
                name: "OpenAiVoice",
                table: "VoiceSettings");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "VoiceSettings");
        }
    }
}
