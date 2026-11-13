using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Codegrove.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTutorCharactersAndSolutionStep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Text",
                table: "LessonScripts",
                newName: "SpeechText");

            migrationBuilder.AddColumn<string>(
                name: "DisplayText",
                table: "LessonScripts",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Mood",
                table: "LessonScripts",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Neutral");

            migrationBuilder.AddColumn<string>(
                name: "Mood",
                table: "Hints",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Thinking");

            migrationBuilder.AddColumn<string>(
                name: "SpeechText",
                table: "Hints",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "SolutionReveals",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChallengeId = table.Column<int>(type: "int", nullable: false),
                    RevealedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolutionReveals", x => new { x.UserId, x.ChallengeId });
                    table.ForeignKey(
                        name: "FK_SolutionReveals_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SolutionReveals_Challenges_ChallengeId",
                        column: x => x.ChallengeId,
                        principalTable: "Challenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TutorCharacters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Slug = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Band = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    VoiceId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PortraitUrl = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    PersonaPrompt = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TutorCharacters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TutorLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CharacterId = table.Column<int>(type: "int", nullable: false),
                    Event = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    DisplayText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SpeechText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Mood = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TutorLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TutorLines_TutorCharacters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "TutorCharacters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SolutionReveals_ChallengeId",
                table: "SolutionReveals",
                column: "ChallengeId");

            migrationBuilder.CreateIndex(
                name: "IX_TutorCharacters_Band",
                table: "TutorCharacters",
                column: "Band",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TutorCharacters_Slug",
                table: "TutorCharacters",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TutorLines_CharacterId_Event_SortOrder",
                table: "TutorLines",
                columns: new[] { "CharacterId", "Event", "SortOrder" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SolutionReveals");

            migrationBuilder.DropTable(
                name: "TutorLines");

            migrationBuilder.DropTable(
                name: "TutorCharacters");

            migrationBuilder.DropColumn(
                name: "DisplayText",
                table: "LessonScripts");

            migrationBuilder.DropColumn(
                name: "Mood",
                table: "LessonScripts");

            migrationBuilder.DropColumn(
                name: "Mood",
                table: "Hints");

            migrationBuilder.DropColumn(
                name: "SpeechText",
                table: "Hints");

            migrationBuilder.RenameColumn(
                name: "SpeechText",
                table: "LessonScripts",
                newName: "Text");
        }
    }
}
