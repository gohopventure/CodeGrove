using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Codegrove.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChallengeCarryOver : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CarryFilesJson",
                table: "Challenges",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContinuesFromLessonSlug",
                table: "Challenges",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CarryFilesJson",
                table: "Challenges");

            migrationBuilder.DropColumn(
                name: "ContinuesFromLessonSlug",
                table: "Challenges");
        }
    }
}
